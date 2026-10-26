import contextlib
import io
import struct
import tempfile
import types
import unittest
from pathlib import Path
import fixtures
import protocol
import frozen_layout
import mapped_model


class LoaderTests(unittest.TestCase):
    def test_trusted_vae_ordered_state_wrapper_is_accepted_without_frozen_copy(self):
        from collections import OrderedDict
        state = OrderedDict([("decoder.sr_bin_boundaries", object())])
        wrapper = dict(metadata={}, state_dict=state)
        self.assertIs(mapped_model.checked_vae_state(wrapper, {"decoder.sr_bin_boundaries"}), state)
        with self.assertRaises(protocol.ProtocolError):
            mapped_model.checked_vae_state(wrapper, {"unexpected.weight"})
    def test_static_header_adapter_shapes_and_byte_accounting(self):
        header = {"base_lm.layers.0.self_attn.q_proj.weight": dict(dtype="BF16", shape=[4, 6], data_offsets=[0, 48]),
                  "feat_encoder.ignored.weight": dict(dtype="F32", shape=[1], data_offsets=[48, 52])}
        raw = fixtures.encoded(header)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "model.safetensors"
            payload = struct.pack("<Q", len(raw)) + raw + bytes(52)
            path.write_bytes(payload)
            read = frozen_layout.read_main_header(path, len(payload))
            self.assertEqual(frozen_layout.expected_adapter_layout(read), [
                dict(name="base_lm.layers.0.self_attn.q_proj.lora_A", shape=[2, 6], dtype="F32"),
                dict(name="base_lm.layers.0.self_attn.q_proj.lora_B", shape=[4, 2], dtype="F32")])
            with self.assertRaises(protocol.ProtocolError):
                frozen_layout.read_main_header(path, len(payload) + 1)

    def test_unknown_frozen_write_or_buffer_is_rejected(self):
        class Rope:
            _non_persistent_buffers_set = {"inv_freq", "cos_cached", "sin_cached"}
            _buffers = dict(inv_freq=1, cos_cached=2, sin_cached=3)
        class Lora:
            _non_persistent_buffers_set = {"scaling"}
            _buffers = dict(scaling=1)
        model = types.SimpleNamespace(named_modules=lambda: iter([("rope", Rope()), ("adapter", Lora())]))
        ropes, adapters, names = mapped_model.buffer_inventory(model, Rope, Lora)
        self.assertEqual(len(ropes), 1)
        self.assertEqual(len(adapters), 1)
        self.assertEqual(names, {"rope.inv_freq", "rope.cos_cached", "rope.sin_cached", "adapter.scaling"})
        unknown = types.SimpleNamespace(_non_persistent_buffers_set={"unexplained"}, _buffers=dict(unexplained=1))
        model.named_modules = lambda: iter([("unknown", unknown)])
        with self.assertRaises(protocol.ProtocolError):
            mapped_model.buffer_inventory(model, Rope, Lora)
        # Generic full-model restore must never become a path to mapped weights.
        with self.assertRaises(protocol.ProtocolError):
            mapped_model.validate_copy_targets({"base_lm.weight": object()}, fixtures.request()["parameterLayout"])

    def test_static_kv_patch_is_restored_even_if_construction_fails(self):
        class Mini:
            def setup_cache(self, *args):
                raise AssertionError("static cache allocated")
        original = Mini.setup_cache
        with self.assertRaisesRegex(RuntimeError, "constructor failure"):
            with mapped_model.without_static_cache(Mini):
                Mini().setup_cache()
                raise RuntimeError("constructor failure")
        self.assertIs(Mini.setup_cache, original)
