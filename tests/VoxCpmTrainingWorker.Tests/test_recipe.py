import struct
import tempfile
import unittest
from pathlib import Path
import fixtures
import protocol
import trainer


class RecipeTests(unittest.TestCase):
    def test_public_lock_preserves_exact_recipe_numeric_types_and_canonical_hash(self):
        lock = protocol.read_json(Path(__file__).resolve().parents[2] / 'eng' / 'locks' / 'voxcpm2-training.lock.json')
        self.assertEqual(protocol.canonical_bytes(lock['recipe']), protocol.canonical_bytes(trainer.recipe()))
        self.assertEqual(lock['recipeSha256'], protocol.sha256(protocol.canonical_bytes(lock['recipe'])))

    def test_exact_recipe_and_no_resample_or_truncation(self):
        recipe = trainer.recipe()
        self.assertEqual(recipe["optimizer"], dict(lr=0.0001, weight_decay=0.01, betas=[0.9, 0.999], eps=1e-8,
                                                  foreach=False, amsgrad=False, maximize=False, capturable=False,
                                                  differentiable=False, fused=None, decoupled_weight_decay=True))
        self.assertEqual(recipe["scheduler"], dict(name="StepLR", stepSize=100, gamma=0.99))
        self.assertEqual(recipe["lora"], dict(enable_lm=True, enable_dit=True, enable_proj=False, r=2, alpha=2, dropout=0.0,
                                              target_modules_lm=["q_proj", "v_proj", "k_proj", "o_proj"], target_modules_dit=["q_proj", "v_proj", "k_proj", "o_proj"],
                                              target_proj_modules=["enc_to_lm_proj", "lm_to_dit_proj", "res_to_dit_proj", "fusion_concat_proj"]))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            value = fixtures.initial(fixtures.request(root))
            value["selectedClip"]["endSample"] = 3
            pcm = struct.pack("<hhh", -32768, 0, 32767)
            payload = b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, 1, 16000, 32000, 2, 16) + b"data" + struct.pack("<I", len(pcm)) + pcm
            path = root / value["selectedClip"]["audioRelativePath"]
            path.parent.mkdir(parents=True)
            path.write_bytes(payload)
            value["selectedClip"]["audioSha256"] = fixtures.sha(payload)
            self.assertEqual(trainer.read_training_pcm(value), pcm)
            value["selectedClip"]["endSample"] = 2
            with self.assertRaises(protocol.ProtocolError):
                trainer.read_training_pcm(value)

    def test_optimizer_order_matches_static_public_layout(self):
        import types
        layout = fixtures.request()["parameterLayout"]
        a, b, frozen = types.SimpleNamespace(shape=(2, 2), requires_grad=True), types.SimpleNamespace(shape=(2, 2), requires_grad=True), types.SimpleNamespace(shape=(2, 2), requires_grad=False)
        model = types.SimpleNamespace(named_parameters=lambda: iter([(layout[1]["name"], b), ("base_lm.weight", frozen), (layout[0]["name"], a)]))
        self.assertEqual(trainer.ordered_adapters(model, layout), [(layout[0]["name"], a), (layout[1]["name"], b)])
        frozen.requires_grad = True
        with self.assertRaises(protocol.ProtocolError):
            trainer.ordered_adapters(model, layout)

    def test_overlong_packed_sequence_fails_without_clipping_or_cursor_advance(self):
        self.assertEqual(trainer.packed_length(5, 16000, 640, 4, 14, 16), (14, 7))
        for max_length, rope in ((13, 16), (16, 13)):
            with self.assertRaises(protocol.ProtocolError):
                trainer.packed_length(5, 16000, 640, 4, max_length, rope)
