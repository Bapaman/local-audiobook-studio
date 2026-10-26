import unittest
import types
import copy
import contextlib
from pathlib import Path
import random
import tempfile
from unittest.mock import patch
import sys
import fixtures
import checkpoint
import protocol


class RestoreTests(unittest.TestCase):
    def test_parent_bundle_cannot_substitute_runtime_or_cursor(self):
        current = fixtures.request()
        previous = fixtures.initial(current)
        previous.update(requestId="0" * 32, executionId="f" * 32, windowId="0" * 32, checkpointId="e" * 32)
        meta = fixtures.manifest(previous, "e" * 64, fixtures.payloads(previous))
        meta["afterCursor"] = current["cursor"]
        expected = checkpoint.parent_expectation(current, meta)
        protocol.validate_manifest(meta, expected["request"], expected["requestSha256"])
        meta["identity"] = dict(meta["identity"], fingerprint="f" * 64)
        with self.assertRaises(protocol.ProtocolError):
            checkpoint.parent_expectation(current, meta)

    def test_restore_rng_after_construction_before_packer(self):
        # Real restoration ordering with fake slow/tensor primitives only.
        events = []
        values = dict(python=(3, (1,), None), torch="tensor-state", numpy_kind="MT19937", numpy_keys=types.SimpleNamespace(numpy=lambda: types.SimpleNamespace(astype=lambda dtype: [1, 2])), numpy_pos=2, numpy_has_gauss=0, numpy_cached_gaussian=0.0)
        rng = types.SimpleNamespace(setstate=lambda state: events.append("python"), getstate=lambda: values["python"])
        np = types.SimpleNamespace(uint32="uint32", array_equal=lambda a, b: a == b, random=types.SimpleNamespace(set_state=lambda state: events.append("numpy"), get_state=lambda: ("MT19937", [1, 2], 2, 0, 0.0)))
        torch = types.SimpleNamespace(set_rng_state=lambda state: events.append("torch"), get_rng_state=lambda: "tensor-state", equal=lambda a, b: a == b)
        checkpoint.restore_rng(values, torch, np, rng)
        self.assertEqual(events, ["python", "numpy", "torch"])

    def test_partial_optimizer_states_do_not_require_all_steps_equal_global_step(self):
        import struct
        state = {1: dict(step=fixtures.Tensor(0, ()), exp_avg=fixtures.Tensor(1, [2, 2]), exp_avg_sq=fixtures.Tensor(2, [2, 2]))}
        group = dict(fixtures.optimizer_settings(), params=[0, 1])
        payload = fixtures.archive(dict(state=state, param_groups=[group]), {"0": struct.pack("<f", 1), "1": bytes(16), "2": bytes(16)})
        checkpoint._optimizer(payload, fixtures.request(), 2)

    def test_full_restore_preflights_bytes_copies_adapters_only_then_restores_rng(self):
        with tempfile.TemporaryDirectory() as directory:
            current = fixtures.request(Path(directory))
            previous = fixtures.initial(current)
            previous.update(requestId="0" * 32, executionId="f" * 32, windowId="0" * 32, checkpointId="e" * 32)
            root = Path(directory) / current["parentCheckpoint"]["manifestRelativePath"]
            root = root.parent
            fixtures.write_bundle(root, previous)
            current["parentCheckpoint"]["manifestSha256"] = fixtures.sha((root / "manifest.json").read_bytes())
            events = []
            class Tensor:
                dtype = "float32"
                device = types.SimpleNamespace(type="cpu")
                requires_grad = True
                shape = (2, 2)
                def __init__(self, number):
                    self.number = number
                def copy_(self, value):
                    self.number = value.number
                    events.append("adapter-copy")
                def detach(self):
                    return self
                def is_floating_point(self):
                    return False
            class Keys:
                def numpy(self):
                    return self
                def astype(self, dtype):
                    return [0] * 624
            adapters = {entry["name"]: Tensor(i + 1) for i, entry in enumerate(current["parameterLayout"])}
            targets = {name: Tensor(0) for name in adapters}
            model = types.SimpleNamespace(named_parameters=lambda: iter(targets.items()))
            opt_state, sched_state = {}, {}
            optimizer = types.SimpleNamespace(load_state_dict=lambda value: (events.append("optimizer-restore"), opt_state.update(value)), state_dict=lambda: opt_state)
            scheduler = types.SimpleNamespace(last_epoch=1, load_state_dict=lambda value: (events.append("scheduler-restore"), sched_state.update(value)), state_dict=lambda: sched_state)
            python_state = random.getstate()
            rng_state = dict(python=python_state, torch="byte-state", numpy_kind="MT19937", numpy_keys=Keys(), numpy_pos=624, numpy_has_gauss=0, numpy_cached_gaussian=0.0)
            load_values = iter([dict(state={}, param_groups=[]), dict(last_epoch=1), rng_state])
            torch = types.ModuleType("torch")
            torch.Tensor, torch.float32 = Tensor, "float32"
            torch.no_grad = contextlib.nullcontext
            torch.equal = lambda a, b: a.number == b.number if isinstance(a, Tensor) else a == b
            torch.set_rng_state = lambda state: events.append("rng-restore")
            torch.get_rng_state = lambda: "byte-state"
            def load(stream, **kwargs):
                self.assertIsInstance(stream.read(4), bytes)
                self.assertEqual(kwargs, dict(weights_only=True, map_location="cpu", mmap=False))
                events.append("weights-only-load")
                return next(load_values)
            torch.load = load
            np = types.ModuleType("numpy")
            np.uint32 = "uint32"
            np.array_equal = lambda a, b: a == b
            np.random = types.SimpleNamespace(set_state=lambda value: None, get_state=lambda: ("MT19937", [0] * 624, 624, 0, 0.0))
            safe = types.ModuleType("safetensors.torch")
            safe.load = lambda payload: adapters
            with patch.dict(sys.modules, {"torch": torch, "numpy": np, "safetensors.torch": safe}):
                checkpoint.restore_training_state(model, optimizer, scheduler, root, dict(request=current))
            self.assertEqual([p.number for p in targets.values()], [1, 2])
            self.assertEqual(events, ["adapter-copy", "adapter-copy", "weights-only-load", "weights-only-load", "weights-only-load", "optimizer-restore", "scheduler-restore", "rng-restore"])

    def test_corrupt_parent_fails_before_any_tensor_load(self):
        with tempfile.TemporaryDirectory() as directory:
            current = fixtures.request(Path(directory))
            previous = fixtures.initial(current)
            previous.update(requestId="0" * 32, executionId="f" * 32, windowId="0" * 32, checkpointId="e" * 32)
            root = (Path(directory) / current["parentCheckpoint"]["manifestRelativePath"]).parent
            fixtures.write_bundle(root, previous, bomb=True)
            current["parentCheckpoint"]["manifestSha256"] = fixtures.sha((root / "manifest.json").read_bytes())
            with patch.dict(sys.modules, {"torch": None, "numpy": None, "safetensors.torch": None}):
                with self.assertRaises(protocol.ProtocolError):
                    checkpoint.restore_training_state(None, None, None, root, dict(request=current))
