"""Checkpoint admission without importing Torch or executing pickle instructions."""
import dataclasses
import io
import math
import pickletools
import struct
import zipfile
from owned_files import pin_directories
from protocol import (PAYLOAD_LIMITS, MANIFEST_LIMIT, ProtocolError, require, keys, integer, finite,
                      canonical_bytes, json_bytes, sha256, read_bytes, read_json, validate_manifest,
                      ID_KEYS, digest, identity, validate_cursor, advance_cursor, manifest_bindings, safe_path)


@dataclasses.dataclass(frozen=True)
class Global:
    module: str
    name: str


@dataclasses.dataclass(frozen=True)
class Storage:
    key: str
    dtype: str
    count: int


@dataclasses.dataclass(frozen=True)
class Tensor:
    storage: Storage
    shape: tuple


ITEM_SIZES = {"Float": 4, "Long": 8, "Byte": 1}
ALLOWED_GLOBALS = {("torch._utils", "_rebuild_tensor_v2"), ("collections", "OrderedDict"),
                   *(("torch", dtype + "Storage") for dtype in ITEM_SIZES)}


def _symbolic_pickle(payload, budget):
    """Small protocol2 stack machine. REDUCE is interpreted, never called."""
    require(0 < len(payload) <= 1024 * 1024 and payload[:2] == b"\x80\x02")
    mark, stack, memo, storages = object(), [], {}, {}
    def pop():
        require(len(stack) > 0)
        return stack.pop()
    def items():
        result = []
        while stack and stack[-1] is not mark:
            result.append(pop())
        require(stack and pop() is mark)
        return list(reversed(result))
    def setitems(target, values):
        require(type(target) is dict and len(values) % 2 == 0 and len(target) + len(values) // 2 <= 8192)
        for key, value in zip(values[::2], values[1::2]):
            require(type(key) in (str, int) and key not in target)
            target[key] = value
    try:
        for counter, (opcode, arg, position) in enumerate(pickletools.genops(payload)):
            require(counter <= 200000 and len(stack) <= 8192 and len(memo) <= 32768)
            op = opcode.name
            if op == "PROTO":
                require(position == 0 and arg == 2)
            elif op == "MARK":
                stack.append(mark)
            elif op in ("NONE", "NEWTRUE", "NEWFALSE"):
                stack.append({"NONE": None, "NEWTRUE": True, "NEWFALSE": False}[op])
            elif op in ("BININT", "BININT1", "BININT2", "LONG1", "LONG4"):
                require(type(arg) is int and abs(arg) <= 2**64)
                stack.append(arg)
            elif op == "BINFLOAT":
                finite(arg)
                stack.append(arg)
            elif op == "BINUNICODE":
                require(type(arg) is str and len(arg) <= 4096)
                stack.append(arg)
            elif op in ("EMPTY_DICT", "EMPTY_LIST", "EMPTY_TUPLE"):
                stack.append({"EMPTY_DICT": dict, "EMPTY_LIST": list, "EMPTY_TUPLE": tuple}[op]())
            elif op in ("BINPUT", "LONG_BINPUT"):
                integer(arg, 0, 32767)
                require(stack and arg not in memo)
                memo[arg] = stack[-1]
            elif op in ("BINGET", "LONG_BINGET"):
                require(arg in memo)
                stack.append(memo[arg])
            elif op == "GLOBAL":
                module, name = arg.split(" ")
                require((module, name) in ALLOWED_GLOBALS)
                stack.append(Global(module, name))
            elif op == "TUPLE":
                stack.append(tuple(items()))
            elif op in ("TUPLE1", "TUPLE2", "TUPLE3"):
                count = int(op[-1])
                require(len(stack) >= count)
                result = tuple(stack[-count:])
                del stack[-count:]
                stack.append(result)
            elif op == "SETITEMS":
                values = items()
                require(stack)
                setitems(stack[-1], values)
            elif op == "SETITEM":
                value, key = pop(), pop()
                require(stack)
                setitems(stack[-1], [key, value])
            elif op == "APPENDS":
                values = items()
                require(stack and type(stack[-1]) is list and len(stack[-1]) + len(values) <= 8192)
                stack[-1].extend(values)
            elif op == "APPEND":
                value = pop()
                require(stack and type(stack[-1]) is list and len(stack[-1]) < 8192)
                stack[-1].append(value)
            elif op == "BINPERSID":
                value = pop()
                require(type(value) is tuple and len(value) == 5)
                kind, storage, key, location, count = value
                require(kind == "storage" and isinstance(storage, Global) and storage.module == "torch" and storage.name.endswith("Storage"))
                dtype = storage.name.removesuffix("Storage")
                require(dtype in ITEM_SIZES and type(key) is str and key.isascii() and key.isdecimal() and str(int(key)) == key and location == "cpu")
                integer(count, 1, budget // ITEM_SIZES[dtype])
                item = Storage(key, dtype, count)
                require(key not in storages or storages[key] == item)
                storages[key] = item
                require(sum(v.count * ITEM_SIZES[v.dtype] for v in storages.values()) <= budget)
                stack.append(item)
            elif op == "REDUCE":
                args, target = pop(), pop()
                require(type(args) is tuple and isinstance(target, Global))
                if (target.module, target.name) == ("collections", "OrderedDict"):
                    require(args == ())
                    stack.append({})
                else:
                    require((target.module, target.name) == ("torch._utils", "_rebuild_tensor_v2") and len(args) == 6)
                    storage, offset, shape, stride, gradient, hooks = args
                    require(isinstance(storage, Storage) and type(offset) is int and offset == 0 and gradient is False and type(hooks) is dict and not hooks)
                    require(type(shape) is tuple and type(stride) is tuple and len(shape) == len(stride) <= 2)
                    count, expected_stride = 1, []
                    for n in reversed(shape):
                        expected_stride.insert(0, count)
                        count *= integer(n, 1, 32768)
                    require(all(type(n) is int for n in stride) and stride == tuple(expected_stride) and count == storage.count)
                    stack.append(Tensor(storage, shape))
            elif op == "STOP":
                require(position == len(payload) - 1 and len(stack) == 1)
                return stack[0], storages
            else:
                raise ProtocolError()
    except (ValueError, IndexError, KeyError, TypeError, OverflowError, RecursionError) as error:
        raise ProtocolError() from error
    raise ProtocolError()


def _archive(payload, budget):
    require(len(payload) <= budget and len(payload) >= 22 and payload[-22:-18] == b"PK\x05\x06")
    try:
        with zipfile.ZipFile(io.BytesIO(payload)) as z:
            files = z.infolist()
            require(not z.comment and 6 <= len(files) <= 2048)
            names, total, ranges = set(), 0, []
            for entry in files:
                require(entry.filename.startswith("archive/") and entry.filename not in names and not entry.is_dir())
                name = entry.filename.removeprefix("archive/")
                require(name in {"data.pkl", ".format_version", ".storage_alignment", "byteorder", "version", ".data/serialization_id"} or name.startswith("data/") and name[5:].isascii() and name[5:].isdecimal() and str(int(name[5:])) == name[5:])
                require(not entry.flag_bits & ~0x808 and entry.compress_type == zipfile.ZIP_STORED and entry.file_size == entry.compress_size)
                total += entry.file_size
                require(total <= budget and 0 <= entry.header_offset < z.start_dir)
                offset = entry.header_offset
                require(payload[offset:offset + 4] == b"PK\x03\x04")
                name_len, extra_len = struct.unpack_from("<HH", payload, offset + 26)
                begin = offset + 30 + name_len + extra_len
                require(begin + entry.file_size <= z.start_dir)
                ranges.append((offset, begin + entry.file_size))
                names.add(entry.filename)
            ranges.sort()
            require(ranges[0][0] == 0 and all(a[1] <= b[0] for a, b in zip(ranges, ranges[1:])))
            def read(name, limit):
                entry = z.getinfo("archive/" + name)
                require(entry.file_size <= limit)
                return z.read(entry)
            require(read(".format_version", 8) == b"1" and read(".storage_alignment", 8) == b"64")
            require(read("byteorder", 8) == b"little" and read("version", 8) == b"3\n")
            serial = read(".data/serialization_id", 128)
            require(serial and serial.isdigit())
            decoded, storages = _symbolic_pickle(read("data.pkl", 1024 * 1024), budget)
            require(names == {"archive/" + n for n in ("data.pkl", ".format_version", ".storage_alignment", "byteorder", "version", ".data/serialization_id")} | {"archive/data/" + k for k in storages})
            data = {}
            for key, storage in storages.items():
                data[key] = read("data/" + key, storage.count * ITEM_SIZES[storage.dtype])
                require(len(data[key]) == storage.count * ITEM_SIZES[storage.dtype])
            return decoded, data
    except (OSError, ValueError, KeyError, struct.error, zipfile.BadZipFile, RuntimeError) as error:
        raise ProtocolError() from error


def _tensor(value, data, dtype, shape, nonnegative=False):
    require(isinstance(value, Tensor) and value.storage.dtype == dtype and value.shape == tuple(shape))
    payload = data[value.storage.key]
    if dtype == "Float":
        for (number,) in struct.iter_unpack("<f", payload):
            finite(number, 0 if nonnegative else -1e100)
    elif dtype == "Long":
        for (number,) in struct.iter_unpack("<q", payload):
            integer(number, 0, 2**32 - 1)
    return payload


def _optimizer(payload, request, step):
    value, data = _archive(payload, PAYLOAD_LIMITS["optimizer.pth"])
    keys(value, ("state", "param_groups"))
    require(type(value["param_groups"]) is list and len(value["param_groups"]) == 1)
    group = value["param_groups"][0]
    settings = dict(lr=learning_rate(step), betas=(0.9, 0.999), eps=1e-8, weight_decay=0.01, amsgrad=False,
                    maximize=False, foreach=False, capturable=False, differentiable=False, fused=None,
                    decoupled_weight_decay=True, initial_lr=0.0001)
    keys(group, (*settings, "params"))
    require(type(group["params"]) is list and all(type(i) is int for i in group["params"]) and group["params"] == list(range(len(request["parameterLayout"]))))
    for key, expected in settings.items():
        require(type(group[key]) is type(expected) and group[key] == expected)
    state = value["state"]
    require(type(state) is dict and 1 <= len(state) <= len(group["params"]))
    used = set()
    for index, item in state.items():
        integer(index, 0, len(group["params"]) - 1)
        keys(item, ("step", "exp_avg", "exp_avg_sq"))
        shape = request["parameterLayout"][index]["shape"]
        step_value = struct.unpack("<f", _tensor(item["step"], data, "Float", (), True))[0]
        require(step_value.is_integer() and 1 <= step_value <= step)
        _tensor(item["exp_avg"], data, "Float", shape)
        _tensor(item["exp_avg_sq"], data, "Float", shape, True)
        for tensor in item.values():
            require(tensor.storage.key not in used)
            used.add(tensor.storage.key)
    require(used == set(data))


def _scheduler(payload, step):
    value, data = _archive(payload, PAYLOAD_LIMITS["scheduler.pth"])
    require(not data)
    expected = dict(step_size=100, gamma=0.99, base_lrs=[0.0001], last_epoch=step, _step_count=step + 1,
                    _is_initial=False, _get_lr_called_within_step=False, _last_lr=[learning_rate(step)])
    # Canonical JSON equality alone would accept integers as boolean aliases.
    keys(value, expected)
    for key, item in expected.items():
        require(type(value[key]) is type(item) and value[key] == item)
    require(all(type(v) is float for k in ("base_lrs", "_last_lr") for v in value[k]))


def learning_rate(step):
    integer(step, 0, 512)
    value = 0.0001
    for _ in range(step // 100):
        value *= 0.99
    return value


def _rng(payload):
    value, data = _archive(payload, PAYLOAD_LIMITS["rng.pth"])
    keys(value, ("python", "torch", "numpy_keys", "numpy_kind", "numpy_pos", "numpy_has_gauss", "numpy_cached_gaussian"))
    py = value["python"]
    require(type(py) is tuple and len(py) == 3 and type(py[0]) is int and py[0] == 3 and type(py[1]) is tuple and len(py[1]) == 625)
    for item in py[1][:-1]:
        integer(item, 0, 2**32 - 1)
    integer(py[1][-1], 0, 624)
    require(py[2] is None or type(py[2]) is float and math.isfinite(py[2]))
    require(value["numpy_kind"] == "MT19937")
    integer(value["numpy_pos"], 0, 624)
    integer(value["numpy_has_gauss"], 0, 1)
    finite(value["numpy_cached_gaussian"])
    _tensor(value["torch"], data, "Byte", (5056,))
    _tensor(value["numpy_keys"], data, "Long", (624,))
    require(len(data) == 2 and value["torch"].storage.key != value["numpy_keys"].storage.key)


def _safetensors(payload, layout):
    require(len(payload) >= 8)
    size = struct.unpack_from("<Q", payload)[0]
    require(0 < size <= 1024 * 1024 and 8 + size <= len(payload))
    header = json_bytes(payload[8:8 + size])
    keys(header, (entry["name"] for entry in layout))
    ranges = []
    for entry in layout:
        tensor = header[entry["name"]]
        keys(tensor, ("dtype", "shape", "data_offsets"))
        require(tensor["dtype"] == "F32" and type(tensor["shape"]) is list and all(type(n) is int for n in tensor["shape"]) and tensor["shape"] == entry["shape"])
        offsets = tensor["data_offsets"]
        require(type(offsets) is list and len(offsets) == 2)
        begin, end = (integer(n, 0, len(payload)) for n in offsets)
        require(end - begin == math.prod(entry["shape"]) * 4 and end <= len(payload) - 8 - size)
        ranges.append((begin, end))
        for (number,) in struct.iter_unpack("<f", memoryview(payload)[8 + size + begin:8 + size + end]):
            finite(number)
    ranges.sort()
    require(ranges[0][0] == 0 and all(a[1] == b[0] for a, b in zip(ranges, ranges[1:])) and ranges[-1][1] == len(payload) - 8 - size)


def preflight_bundle(root, manifest, expected):
    keys(expected, ("request", "requestSha256"))
    request = expected["request"]
    validate_manifest(manifest, request, expected["requestSha256"])
    with pin_directories(root) as root:
        require({p.name for p in root.iterdir()} == {*PAYLOAD_LIMITS, "manifest.json", "READY"})
        manifest_payload = read_bytes(root / "manifest.json", MANIFEST_LIMIT)
        require(canonical_bytes(json_bytes(manifest_payload)) == canonical_bytes(manifest))
        require(read_bytes(root / "READY", 65) == (sha256(manifest_payload) + "\n").encode("ascii"))
        data = {}
        for name, limit in PAYLOAD_LIMITS.items():
            payload = read_bytes(root / name, limit)
            entry = manifest["files"][name]
            require(len(payload) == entry["bytes"] and sha256(payload) == entry["sha256"])
            data[name] = payload
        _safetensors(data["lora_weights.safetensors"], request["parameterLayout"])
        _optimizer(data["optimizer.pth"], request, manifest["afterCursor"]["globalStep"])
        _scheduler(data["scheduler.pth"], manifest["afterCursor"]["globalStep"])
        _rng(data["rng.pth"])
        meta = json_bytes(data["metadata.json"])
        keys(meta, ("format", "optimizerNames", "recipeSha256", "adapterLayoutSha256"))
        require(type(meta["format"]) is int and meta["format"] == 1)
        require(meta["optimizerNames"] == [entry["name"] for entry in request["parameterLayout"]])
        require(meta["recipeSha256"] == request["identity"]["recipeSha256"] and meta["adapterLayoutSha256"] == request["identity"]["adapterLayoutSha256"])


def parent_expectation(request, manifest):
    parent = request["parentCheckpoint"]
    require(parent is not None)
    for key in ("projectId", "trainingId", "actionId", "corpusVersionId", "corpusManifestSha256", "corpusBindingSha256", "identity", "parameterLayout"):
        require(canonical_bytes(manifest[key]) == canonical_bytes(request[key]))
    for key, reference_key in (("checkpointId", "id"), ("executionId", "executionId"), ("windowId", "windowId")):
        require(manifest[key] == parent[reference_key])
    require(canonical_bytes(manifest["afterCursor"]) == canonical_bytes(request["cursor"]))
    validate_cursor(manifest["beforeCursor"], request["clipIds"])
    require(advance_cursor(manifest["beforeCursor"], request["clipIds"]) == request["cursor"])
    old = dict(request)
    for key in ID_KEYS:
        old[key] = identity(manifest[key])
    old["cursor"] = manifest["beforeCursor"]
    parent_sha = manifest["parentManifestSha256"]
    if old["cursor"]["globalStep"] == 0:
        require(parent_sha is None)
        old["parentCheckpoint"] = None
    else:
        old["parentCheckpoint"] = dict(manifestSha256=digest(parent_sha))
    expected = dict(request=old, requestSha256=digest(manifest["requestSha256"]))
    validate_manifest(manifest, old, expected["requestSha256"])
    return expected


def restore_rng(value, torch, np, python_random):
    python_random.setstate(value["python"])
    numpy_keys = value["numpy_keys"].numpy().astype(np.uint32)
    np.random.set_state((value["numpy_kind"], numpy_keys, value["numpy_pos"], value["numpy_has_gauss"], value["numpy_cached_gaussian"]))
    torch.set_rng_state(value["torch"])
    current = np.random.get_state()
    require(python_random.getstate() == value["python"] and torch.equal(torch.get_rng_state(), value["torch"]))
    require(current[0] == value["numpy_kind"] and np.array_equal(current[1], numpy_keys) and current[2:] == (value["numpy_pos"], value["numpy_has_gauss"], value["numpy_cached_gaussian"]))


def _equal_tree(left, right, torch):
    if isinstance(left, torch.Tensor):
        return isinstance(right, torch.Tensor) and left.dtype == right.dtype and left.shape == right.shape and torch.equal(left, right)
    if type(left) is dict:
        return type(right) is dict and left.keys() == right.keys() and all(_equal_tree(left[k], right[k], torch) for k in left)
    if type(left) in (tuple, list):
        return type(right) is type(left) and len(left) == len(right) and all(_equal_tree(a, b, torch) for a, b in zip(left, right))
    return type(left) is type(right) and left == right


def restore_training_state(model, optimizer, scheduler, root, expected):
    # Exact accepted manifest hash is the authority; reconstructed previous
    # request bindings are checked against this lineage, not the current window.
    request = expected["request"]
    reference = request["parentCheckpoint"]
    from owned_files import io_path
    require(reference is not None and io_path(root) == safe_path(request["projectRoot"], reference["manifestRelativePath"]).parent)
    manifest = read_json(root / "manifest.json", reference["manifestSha256"], MANIFEST_LIMIT)
    previous = parent_expectation(request, manifest)
    preflight_bundle(root, manifest, previous)
    # Rehash the same bounded immutable bytes handed to tensor loaders. No
    # unverified reopen by a library and no path-based mmap of checkpoint files.
    payloads = {}
    for name, limit in PAYLOAD_LIMITS.items():
        payload = read_bytes(root / name, limit)
        require(len(payload) == manifest["files"][name]["bytes"] and sha256(payload) == manifest["files"][name]["sha256"])
        payloads[name] = payload
    import random
    import numpy as np
    import torch
    from safetensors.torch import load
    from trainer import ordered_adapters
    from mapped_model import validate_copy_targets, tensor_finite
    adapters = load(payloads["lora_weights.safetensors"])
    validate_copy_targets(adapters, request["parameterLayout"])
    named = ordered_adapters(model, request["parameterLayout"])
    with torch.no_grad():
        for name, parameter in named:
            value = adapters[name]
            require(value.dtype == torch.float32 and value.device.type == "cpu" and value.shape == parameter.shape and tensor_finite(value, torch))
            parameter.copy_(value)
    def tensor_load(name):
        return torch.load(io.BytesIO(payloads[name]), weights_only=True, map_location="cpu", mmap=False)
    saved_optimizer, saved_scheduler, rng = (tensor_load(name) for name in ("optimizer.pth", "scheduler.pth", "rng.pth"))
    optimizer.load_state_dict(saved_optimizer)
    scheduler.load_state_dict(saved_scheduler)
    require(all(torch.equal(adapters[name], p.detach()) for name, p in named))
    require(_equal_tree(saved_optimizer, optimizer.state_dict(), torch) and _equal_tree(saved_scheduler, scheduler.state_dict(), torch))
    require(scheduler.last_epoch == request["cursor"]["globalStep"])
    # Construction/initialization and all restore allocations precede RNG.
    # No packer/tokenizer/forward call is allowed between here and train_one.
    restore_rng(rng, torch, np, random)


def save_training_state(model, optimizer, scheduler, root, metadata):
    import random
    import numpy as np
    import torch
    from safetensors.torch import save
    from trainer import ordered_adapters
    from mapped_model import tensor_finite
    from owned_files import io_path
    request = metadata["request"]
    require(io_path(root) == safe_path(request["projectRoot"], request["outputRelativeRoot"]))
    named = ordered_adapters(model, request["parameterLayout"])
    require(all(p.grad is None and tensor_finite(p, torch) for _, p in named))
    adapters = {name: p.detach().cpu().contiguous().clone() for name, p in named}
    numpy_rng = np.random.get_state()
    rng = dict(python=random.getstate(), torch=torch.get_rng_state(), numpy_kind=numpy_rng[0],
               numpy_keys=torch.from_numpy(numpy_rng[1].astype(np.int64)), numpy_pos=int(numpy_rng[2]),
               numpy_has_gauss=int(numpy_rng[3]), numpy_cached_gaussian=float(numpy_rng[4]))
    data = {"lora_weights.safetensors": save(adapters)}
    for name, value in (("optimizer.pth", optimizer.state_dict()), ("scheduler.pth", scheduler.state_dict()), ("rng.pth", rng)):
        stream = io.BytesIO()
        torch.save(value, stream)
        data[name] = stream.getvalue()
    data["metadata.json"] = canonical_bytes(dict(format=1, optimizerNames=[name for name, _ in named],
                                                recipeSha256=request["identity"]["recipeSha256"], adapterLayoutSha256=request["identity"]["adapterLayoutSha256"]))
    for name, limit in PAYLOAD_LIMITS.items():
        require(0 < len(data[name]) <= limit)
    # Same static admission as the parent, before publication, within the Job.
    step = advance_cursor(request["cursor"], request["clipIds"])["globalStep"]
    _safetensors(data["lora_weights.safetensors"], request["parameterLayout"])
    _optimizer(data["optimizer.pth"], request, step)
    _scheduler(data["scheduler.pth"], step)
    _rng(data["rng.pth"])
    return data
