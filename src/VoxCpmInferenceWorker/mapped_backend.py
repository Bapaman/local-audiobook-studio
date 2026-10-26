"""Lazy adapter-only backend. Native admission and checkpoint authority belong to host."""
import gc
import json
import math
import re
import struct
import sys
from inference_contract import (InferenceError, canonical, digest, integer, require,
                                sha256)
from synthesis import synthesize_loaded


def _layout(layout):
    require(type(layout) is list and len(layout) == 384)
    names, count = [], 0
    for entry in layout:
        require(type(entry) is dict and set(entry) == {'name', 'shape', 'dtype'})
        name, shape = entry['name'], entry['shape']
        require(type(name) is str and len(name) <= 256 and re.fullmatch(
            r'(?:base_lm|residual_lm|feat_decoder\.estimator)\.[A-Za-z0-9_.]+\.(?:q_proj|k_proj|v_proj|o_proj)\.lora_[AB]', name))
        require(entry['dtype'] == 'F32' and type(shape) is list and len(shape) == 2)
        a, b = (integer(n, 1, 32768) for n in shape)
        require(a == 2 if name.endswith('.lora_A') else b == 2)
        names.append(name)
        count += a * b
    require(names == sorted(set(names)) and count == 1130496)
    pairs = {name.rsplit('.', 1)[0] for name in names}
    require(len(pairs) == 192 and set(names) == {p + '.lora_' + k for p in pairs for k in ('A', 'B')})


def validate_adapter_payload(payload, layout, expected_sha):
    _layout(layout)
    require(type(payload) is bytes and 8 < len(payload) <= 8 * 1024**2)
    require(sha256(payload) == digest(expected_sha))
    length = struct.unpack_from('<Q', payload)[0]
    require(0 < length <= 1024**2 and 8 + length < len(payload))

    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result)
            result[key] = value
        return result

    def constant(_):
        raise InferenceError()

    try:
        header = json.loads(payload[8:8 + length].decode('utf-8'),
                            object_pairs_hook=pairs, parse_constant=constant)
    except (ValueError, UnicodeError, RecursionError) as error:
        raise InferenceError() from error
    require(type(header) is dict and set(header) == {entry['name'] for entry in layout})
    body = memoryview(payload)[8 + length:]
    ranges = []
    for entry in layout:
        tensor = header[entry['name']]
        require(type(tensor) is dict and set(tensor) == {'shape', 'dtype', 'data_offsets'})
        require(tensor['dtype'] == 'F32' and type(tensor['shape']) is list
                and all(type(n) is int for n in tensor['shape']) and tensor['shape'] == entry['shape'])
        offsets = tensor['data_offsets']
        require(type(offsets) is list and len(offsets) == 2)
        start, end = (integer(n, 0, len(body)) for n in offsets)
        require(end - start == math.prod(entry['shape']) * 4)
        ranges.append((start, end))
    ranges.sort()
    require(ranges[0][0] == 0 and ranges[-1][1] == len(body)
            and all(a[1] == b[0] for a, b in zip(ranges, ranges[1:])))
    require(all(math.isfinite(number) for (number,) in struct.iter_unpack('<f', body)))


def _tensor(value, shape, torch):
    require(isinstance(value, torch.Tensor) and value.device.type == 'cpu'
            and value.dtype == torch.float32 and tuple(value.shape) == tuple(shape))
    require(bool(torch.isfinite(value).all().item()))


def copy_adapters(model, torch, values, layout):
    """All tensor checks precede writes; never copies into frozen parameters."""
    expected = {entry['name'] for entry in layout}
    require(type(values) is dict and set(values) == expected)
    parameters = dict(model.named_parameters())
    require({n for n in parameters if n.endswith(('.lora_A', '.lora_B'))} == expected)
    for entry in layout:
        name, shape = entry['name'], entry['shape']
        _tensor(values[name], shape, torch)
        _tensor(parameters[name], shape, torch)
    with torch.no_grad():
        for entry in layout:
            name = entry['name']
            parameters[name].copy_(values[name])
            require(torch.equal(parameters[name], values[name]))


def _load_model(request):
    # Host has already admitted/pinned these exact modules, files and namespaces.
    # Imports must stay here: merely importing the engine never starts native ML.
    from runtime import install_dependencies
    install_dependencies(request)
    import torch
    from safetensors.torch import load
    from mapped_model import load_training_model
    require(torch.get_default_dtype() == torch.float32)
    torch.set_num_threads(2)
    torch.set_num_interop_threads(1)
    require(torch.get_num_threads() == 2 and torch.get_num_interop_threads() == 1)
    return load_training_model(request, lambda _: None), torch, load


class MappedBackend:
    """Single segment owner; no process, cache lease or publication ownership."""
    def __init__(self, model, torch, binding):
        self._model, self._torch, self._binding = model, torch, binding
        self._used = False

    def __enter__(self):
        require(self._model is not None and not self._used)
        return self

    def __exit__(self, *args):
        self.close()

    def synthesize(self, text, reference=None, controls=None):
        require(self._model is not None and not self._used)
        self._used = True
        try:
            if controls is not None:
                return synthesize_loaded(self._model, self._torch, text, self._binding, reference, controls)
            if reference is None:
                return synthesize_loaded(self._model, self._torch, text, self._binding)
            return synthesize_loaded(self._model, self._torch, text, self._binding, reference)
        finally:
            self.close()

    def close(self):
        model, self._model = self._model, None
        if model is None:
            return
        pin = model.__dict__.pop('_training_cache_pin', None)
        model.__dict__.pop('_training_cache_stream', None)
        model.__dict__.pop('_training_backing', None)
        self._torch = None
        del model
        gc.collect()
        if pin is not None:
            pin.__exit__(None, None, None)


def load_mapped_backend(runtime_request, binding, adapter_payload):
    """Host supplies admitted runtime and accepted binding; bytes stay immutable."""
    binding.validate()
    require(type(runtime_request) is dict and type(runtime_request.get('identity')) is dict)
    identity = runtime_request['identity']
    require(identity.get('fingerprint') == binding.runtime_fingerprint
            and identity.get('recipeSha256') == binding.recipe_sha256
            and identity.get('adapterLayoutSha256') == binding.layout_sha256)
    layout = runtime_request.get('parameterLayout')
    _layout(layout)
    require(sha256(canonical(layout)) == binding.layout_sha256)
    validate_adapter_payload(adapter_payload, layout, binding.adapter_sha256)
    sys.dont_write_bytecode = True
    model, torch, load = _load_model(runtime_request)
    backend = MappedBackend(model, torch, binding)
    try:
        values = load(adapter_payload)  # Never pass a reopenable library path.
        copy_adapters(model, torch, values, layout)
        for name in sorted({entry['name'].rsplit('.', 1)[0] for entry in layout}):
            module = model.get_submodule(name)
            _tensor(module.scaling, (), torch)
            require(module.r == 2 and module.alpha == 2 and module.enabled is True
                    and module.scaling.item() == 1.0)
        for parameter in model.parameters():
            parameter.requires_grad_(False)
        model.eval()
        require(model.training is False and all(p.requires_grad is False for p in model.parameters()))
        return backend
    except BaseException:
        model = None
        backend.close()
        raise
