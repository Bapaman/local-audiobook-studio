"""Bounded synthetic protocol fixtures; no speech, model or tensor imports."""
import copy
import hashlib
import io
import json
from pathlib import Path
import pickle
import struct
import sys
import zipfile

REPO = Path(__file__).resolve().parents[2]
WORKER = REPO / "src" / "VoxCpmTrainingWorker"
sys.path.insert(0, str(WORKER))
GOLDEN = REPO / "tests" / "fixtures" / "VoxCpmTrainingProtocol"


def encoded(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()


def sha(payload):
    return hashlib.sha256(payload).hexdigest()


def request(root=None):
    value = json.loads((GOLDEN / "request.json").read_text(encoding="utf-8"))
    if root:
        value["projectRoot"] = str(root)
        for key, name in (("sourceRoot", "source"), ("modelRoot", "models"), ("runtimeInventoryPath", "inventory.json")):
            value[key] = str(root / name)
        value["dependencySites"] = [str(root / "sites")]
        value["cache"]["path"] = str(root / "frozen.bin")
    return value


def initial(value):
    value = copy.deepcopy(value)
    value["cursor"] = dict(globalStep=0, completedEpochs=0, epochSize=2, nextRetainedOrdinal=0, nextClipId="9" * 32)
    value["selectedClip"].update(trainingOrdinal=0, sourceIndex=0, id="9" * 32, startSample=0, endSample=16000)
    value["parentCheckpoint"] = None
    return value


class Tensor:
    def __init__(self, key, shape, dtype="Float", announced=None):
        self.key, self.shape, self.dtype = str(key), tuple(shape), dtype
        self.numel = 1
        for n in shape:
            self.numel *= n
        self.announced = self.numel if announced is None else announced


def pickle_bytes(value):
    """Emit protocol2 Torch grammar without importing or executing Torch globals."""
    def emit(item):
        if isinstance(item, Tensor):
            storage = b"(" + emit("storage") + b"ctorch\n" + item.dtype.encode() + b"Storage\n" + emit(item.key) + emit("cpu") + emit(item.announced) + b"tQ"
            stride, step = [], 1
            for size in reversed(item.shape):
                stride.insert(0, step)
                step *= size
            return b"ctorch._utils\n_rebuild_tensor_v2\n(" + storage + emit(0) + emit(item.shape) + emit(tuple(stride)) + emit(False) + b"ccollections\nOrderedDict\n)RtR"
        if type(item) is dict:
            return b"}(" + b"".join(emit(k) + emit(v) for k, v in item.items()) + b"u"
        if type(item) is list:
            return b"](" + b"".join(emit(v) for v in item) + b"e"
        if type(item) is tuple:
            return b"(" + b"".join(emit(v) for v in item) + b"t"
        if type(item) is str:
            raw = item.encode("utf-8")
            return b"X" + struct.pack("<I", len(raw)) + raw
        return pickle.dumps(item, protocol=2)[2:-1]
    return b"\x80\x02" + emit(value) + b"."


def archive(value, storages, extra=None):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_STORED) as z:
        for name, data in {"data.pkl": pickle_bytes(value), ".format_version": b"1", ".storage_alignment": b"64", "byteorder": b"little", "version": b"3\n", ".data/serialization_id": b"0", **{f"data/{k}": v for k, v in storages.items()}, **(extra or {})}.items():
            z.writestr(zipfile.ZipInfo("archive/" + name, date_time=(1980, 1, 1, 0, 0, 0)), data)
    return output.getvalue()


def optimizer_settings():
    return dict(lr=0.0001, betas=(0.9, 0.999), eps=1e-8, weight_decay=0.01, amsgrad=False,
                maximize=False, foreach=False, capturable=False, differentiable=False, fused=None,
                decoupled_weight_decay=True, initial_lr=0.0001)


def payloads(value, bomb=False):
    layout = value["parameterLayout"]
    next_step = value["cursor"]["globalStep"] + 1
    header, raw, state, storages = {}, bytearray(), {}, {}
    for i, entry in enumerate(layout):
        count = 1
        for n in entry["shape"]:
            count *= n
        block = struct.pack("<" + "f" * count, *([0.25] * count))
        header[entry["name"]] = dict(dtype="F32", shape=entry["shape"], data_offsets=[len(raw), len(raw) + len(block)])
        raw.extend(block)
        fields = {}
        for j, (field, shape, data) in enumerate((("step", (), struct.pack("<f", next_step)), ("exp_avg", entry["shape"], block), ("exp_avg_sq", entry["shape"], block))):
            key = str(i * 3 + j)
            fields[field] = Tensor(key, shape, announced=(2**40 if bomb and i == 0 and j == 1 else None))
            storages[key] = data
        state[i] = fields
    head = encoded(header)
    head += b" " * (-len(head) % 8)
    lora = struct.pack("<Q", len(head)) + head + raw
    optimizer = archive(dict(state=state, param_groups=[dict(optimizer_settings(), params=list(range(len(layout))))]), storages)
    scheduler = archive(dict(step_size=100, gamma=0.99, base_lrs=[0.0001], last_epoch=next_step, _step_count=next_step + 1, _is_initial=False, _get_lr_called_within_step=False, _last_lr=[0.0001]), {})
    rng = archive(dict(python=(3, tuple([4294967295] * 624 + [624]), None), torch=Tensor(0, [5056], "Byte"), numpy_keys=Tensor(1, [624], "Long"), numpy_kind="MT19937", numpy_pos=624, numpy_has_gauss=0, numpy_cached_gaussian=0.0), {"0": bytes(5056), "1": bytes(4992)})
    metadata = encoded(dict(format=1, optimizerNames=[p["name"] for p in layout], recipeSha256=value["identity"]["recipeSha256"], adapterLayoutSha256=value["identity"]["adapterLayoutSha256"]))
    return {"lora_weights.safetensors": lora, "optimizer.pth": optimizer, "scheduler.pth": scheduler, "rng.pth": rng, "metadata.json": metadata}


def manifest(value, request_sha, payload):
    result = {k: copy.deepcopy(value[k]) for k in ("requestId", "projectId", "trainingId", "actionId", "executionId", "windowId", "checkpointId", "corpusVersionId", "corpusManifestSha256", "corpusBindingSha256", "identity", "parameterLayout")}
    next_step = value["cursor"]["globalStep"] + 1
    epoch, ordinal = divmod(next_step, len(value["clipIds"]))
    result.update(format=1, requestSha256=request_sha, parentManifestSha256=value["parentCheckpoint"]["manifestSha256"] if value["parentCheckpoint"] else None,
                  beforeCursor=copy.deepcopy(value["cursor"]), afterCursor=dict(globalStep=next_step, completedEpochs=epoch, epochSize=len(value["clipIds"]), nextRetainedOrdinal=ordinal, nextClipId=value["clipIds"][ordinal]),
                  metrics=dict(loss=0.5, gradientNorm=0.25), files={k: dict(bytes=len(v), sha256=sha(v)) for k, v in payload.items()})
    return result


def write_bundle(root, value, request_sha="e" * 64, bomb=False):
    root.mkdir(parents=True)
    payload = payloads(value, bomb)
    meta = manifest(value, request_sha, payload)
    for name, data in payload.items():
        (root / name).write_bytes(data)
    (root / "manifest.json").write_bytes(encoded(meta))
    (root / "READY").write_bytes((sha(encoded(meta)) + "\n").encode())
    return meta
