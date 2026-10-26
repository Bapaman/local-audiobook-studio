import copy
import hashlib
import json
import struct
import subprocess
import sys
import types
import unittest
from pathlib import Path
from unittest.mock import patch

ENGINE = Path(__file__).resolve().parents[2] / 'src' / 'VoxCpmInferenceWorker'
sys.path.insert(0, str(ENGINE))
import mapped_backend
from inference_contract import InferenceError
from mapped_backend import (MappedBackend, copy_adapters, load_mapped_backend,
                            validate_adapter_payload)
from test_contract import binding
from fakes import Model, Tensor, Torch


def layout():
    # 192 pairs * 2 matrices * (2*1472) = 1,130,496 scalars, no real speaker data.
    return [dict(name=f'base_lm.layers.{index:03}.self_attn.q_proj.lora_{kind}',
                 shape=([2, 1472] if kind == 'A' else [1472, 2]), dtype='F32')
            for index in range(192) for kind in ('A', 'B')]


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':')).encode('utf-8')


def bundle(entries=None, mutation=None):
    entries = entries or layout()
    header, offset = {}, 0
    for entry in entries:
        end = offset + entry['shape'][0] * entry['shape'][1] * 4
        header[entry['name']] = dict(dtype='F32', shape=entry['shape'], data_offsets=[offset, end])
        offset = end
    if mutation:
        mutation(header)
    encoded = canonical(header)
    return struct.pack('<Q', len(encoded)) + encoded + b'\0' * offset


def request_and_binding():
    entries = layout()
    payload = bundle(entries)
    item = binding(adapter_sha256=hashlib.sha256(payload).hexdigest(),
                   layout_sha256=hashlib.sha256(canonical(entries)).hexdigest())
    request = dict(identity=dict(fingerprint=item.runtime_fingerprint, recipeSha256=item.recipe_sha256,
                                 adapterLayoutSha256=item.layout_sha256), parameterLayout=entries)
    return request, item, payload


class Pin:
    def __init__(self):
        self.closed = 0
    def __exit__(self, *args):
        self.closed += 1


class AdapterModel(Model):
    def __init__(self, torch, entries):
        super().__init__(torch)
        self.training = True
        self.pin = Pin()
        self._training_cache_pin = self.pin
        self._training_cache_stream = object()
        self._training_backing = object()
        self.params = {entry['name']: Tensor([0.0], entry['shape']) for entry in entries}
        for parameter in self.params.values():
            parameter.requires_grad = True
        self.params['frozen.weight'] = Tensor([9.0])
        self.modules_by_name = {entry['name'].rsplit('.', 1)[0]: types.SimpleNamespace(
            r=2, alpha=2, scaling=Tensor([1.0], ()), enabled=True) for entry in entries}

    def parameters(self):
        return iter(self.params.values())
    def named_parameters(self):
        return self.params.items()
    def get_parameter(self, name):
        return self.params[name]
    def get_submodule(self, name):
        return self.modules_by_name[name]


class BackendTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.request, cls.item, cls.payload = request_and_binding()

    def test_strict_payload_accepts_exact_layout_and_rejects_corruption_before_tensors(self):
        validate_adapter_payload(self.payload, self.request['parameterLayout'], self.item.adapter_sha256)
        with self.assertRaises(InferenceError):
            validate_adapter_payload(self.payload, self.request['parameterLayout'], '0' * 64)
        for mutated in (self.payload[:-1], self.payload + b'x', bytearray(self.payload)):
            with self.subTest(kind=type(mutated)), self.assertRaises(InferenceError):
                validate_adapter_payload(mutated, self.request['parameterLayout'], hashlib.sha256(mutated).hexdigest())
        nonfinite = self.payload[:-4] + struct.pack('<f', float('nan'))
        with self.assertRaises(InferenceError):
            validate_adapter_payload(nonfinite, self.request['parameterLayout'], hashlib.sha256(nonfinite).hexdigest())

    def test_missing_extra_overlapping_wrong_shape_and_duplicate_tensors_rejected(self):
        entries = self.request['parameterLayout']
        first, second = entries[0]['name'], entries[1]['name']
        mutations = (
            lambda h: h.pop(first),
            lambda h: h.update(__metadata__={}),
            lambda h: h[second].update(data_offsets=h[first]['data_offsets']),
            lambda h: h[first].update(shape=[2.0, 1472]),
            lambda h: h[first].update(dtype='F16'))
        for mutation in mutations:
            payload = bundle(entries, mutation)
            with self.subTest(mutation=mutation), self.assertRaises(InferenceError):
                validate_adapter_payload(payload, entries, hashlib.sha256(payload).hexdigest())
        header = b'{"x":1,"x":2}'
        payload = struct.pack('<Q', len(header)) + header
        with self.assertRaises(InferenceError):
            validate_adapter_payload(payload, entries, hashlib.sha256(payload).hexdigest())

    def test_layout_must_be_complete_sorted_rank_two_pairs_with_exact_scalar_count(self):
        for change in ('missing', 'reversed', 'rank', 'count', 'name'):
            entries = copy.deepcopy(self.request['parameterLayout'])
            if change == 'missing':
                entries.pop()
            elif change == 'reversed':
                entries.reverse()
            elif change == 'rank':
                entries[0]['shape'] = [1, 2944]
            elif change == 'count':
                entries[0]['shape'] = [2, 1473]
            else:
                entries[0]['name'] = '../../q_proj.lora_A'
            with self.subTest(change=change), self.assertRaises(InferenceError):
                validate_adapter_payload(self.payload, entries, self.item.adapter_sha256)

    def test_identity_drift_is_rejected_before_lazy_bootstrap(self):
        for field in ('fingerprint', 'recipeSha256', 'adapterLayoutSha256'):
            request = copy.deepcopy(self.request)
            request['identity'][field] = '9' * 64
            with patch.object(mapped_backend, '_load_model', side_effect=AssertionError('must not bootstrap')):
                with self.subTest(field=field), self.assertRaises(InferenceError):
                    load_mapped_backend(request, self.item, self.payload)

    def test_only_verified_adapters_are_copied_and_equal_before_eval_generation(self):
        torch = Torch()
        model = AdapterModel(torch, self.request['parameterLayout'])
        values = {entry['name']: Tensor([0.25], entry['shape']) for entry in self.request['parameterLayout']}
        seen = []
        def load(payload):
            seen.append(payload)
            return values
        with patch.object(mapped_backend, '_load_model', return_value=(model, torch, load)):
            backend = load_mapped_backend(self.request, self.item, self.payload)
        self.assertIs(seen[0], self.payload)
        self.assertFalse(model.training)
        self.assertTrue(all(not p.requires_grad for p in model.parameters()))
        self.assertEqual(model.params['frozen.weight'].values, [9.0])
        self.assertTrue(all(model.params[name].values == [0.25] for name in values))
        with backend:
            _, proof = backend.synthesize('new text')
        self.assertEqual(proof['binding'], self.item.as_dict())
        self.assertEqual(model.pin.closed, 1)
        with self.assertRaises(InferenceError):
            backend.synthesize('again')
        backend.close()
        self.assertEqual(model.pin.closed, 1)

    def test_copy_rejects_nonfinite_wrong_tensor_set_shape_device_dtype_and_failed_copy(self):
        entries = [dict(name='base_lm.x.q_proj.lora_A', shape=[2, 1], dtype='F32')]
        for mode in ('extra', 'missing', 'shape', 'device', 'dtype', 'nan', 'copy'):
            torch = Torch()
            model = AdapterModel(torch, entries)
            name = entries[0]['name']
            source = Tensor([0.5], (2, 1))
            values = {name: source}
            if mode == 'extra':
                values['extra'] = source
            elif mode == 'missing':
                values.clear()
            elif mode == 'shape':
                source.shape = (1, 2)
            elif mode == 'device':
                source.device.type = 'cuda'
            elif mode == 'dtype':
                source.dtype = 'float16'
            elif mode == 'nan':
                source.values = [float('nan')]
            else:
                model.params[name].copy_ = lambda value: None
            with self.subTest(mode=mode), self.assertRaises(InferenceError):
                copy_adapters(model, torch, values, entries)

    def test_scaling_tensor_failure_and_generation_failure_always_close_model_pin(self):
        for mode in ('scale', 'payload', 'generation'):
            torch = Torch()
            model = AdapterModel(torch, self.request['parameterLayout'])
            values = {entry['name']: Tensor([0.25], entry['shape']) for entry in self.request['parameterLayout']}
            if mode == 'scale':
                next(iter(model.modules_by_name.values())).scaling.values = [0.0]
            if mode == 'payload':
                values.pop(next(iter(values)))
            with patch.object(mapped_backend, '_load_model', return_value=(model, torch, lambda payload: values)):
                if mode in ('scale', 'payload'):
                    with self.assertRaises(InferenceError):
                        load_mapped_backend(self.request, self.item, self.payload)
                else:
                    backend = load_mapped_backend(self.request, self.item, self.payload)
                    model.failure = True
                    with self.assertRaises(RuntimeError):
                        backend.synthesize('text')
            self.assertEqual(model.pin.closed, 1)

    def test_fresh_isolated_import_has_no_optional_dependencies_or_bytecode_writes(self):
        code = ("import sys; sys.path.insert(0, sys.argv[1]); "
                "import mapped_backend, synthesis, inference_contract; "
                "assert sys.dont_write_bytecode; "
                "assert not any(n.split('.')[0] in {'torch','numpy','safetensors','voxcpm'} for n in sys.modules)")
        result = subprocess.run([sys.executable, '-I', '-S', '-B', '-c', code, str(ENGINE)],
                                capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors='replace'))
        self.assertEqual(result.stdout, b'')


if __name__ == '__main__':
    unittest.main()
