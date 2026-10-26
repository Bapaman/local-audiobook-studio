import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'src' / 'VoxCpmInferenceWorker'))
from inference_contract import InferenceError
from synthesis import synthesize_loaded
from test_contract import binding
from fakes import Model, Tensor, Torch


class SynthesisTests(unittest.TestCase):
    def setUp(self):
        self.torch = Torch()
        self.model = Model(self.torch)
        self.original = self.model.text_tokenizer

    def assert_restored(self):
        self.assertIs(self.model.text_tokenizer, self.original)
        self.assertIsNone(self.model.stop_head.hook)
        self.assertIsNone(self.model.base_lm.kv_cache)
        self.assertIsNone(self.model.residual_lm.kv_cache)
        self.assertFalse(self.torch.inference_active)

    def test_complete_text_uses_direct_adapter_generation_and_small_cache(self):
        text = '  Полная фраза.\n'
        audio, proof = synthesize_loaded(self.model, self.torch, text, binding())
        self.assertIs(audio, self.model.waveform)
        self.assertEqual(self.model.tokenizer_inputs, [text, text, text])
        self.assertEqual(self.model.arguments, dict(
            target_text=text, prompt_text='', prompt_wav_path='', reference_wav_path='',
            min_len=2, max_len=256, inference_timesteps=10, cfg_value=2.0,
            retry_badcase=False, retry_badcase_max_times=1,
            retry_badcase_ratio_threshold=6.0, trim_silence_vad=False, seed=42))
        self.assertEqual(self.model.base_lm.allocations, [(1, 46, 'cpu', 'float32')])
        self.assertEqual(self.model.residual_lm.allocations, [(1, 46, 'cpu', 'float32')])
        self.assertEqual(proof['cacheBytes'], 11776)  # 2 LMs * 2 * 2 * 2 * 46 * 4 * 4
        self.assertEqual((proof['effectivePatchLimit'], proof['generatedPatches']), (40, 4))
        self.assertTrue(proof['naturalEos'])
        self.assertEqual((proof['sampleRate'], proof['samples'], proof['peak']), (48000, 4, 0.5))
        self.assertEqual(proof['binding'], binding().as_dict())
        self.assertNotIn(text, str(proof))
        self.assert_restored()

    def test_natural_stop_exactly_at_effective_cap_is_accepted(self):
        self.model.flags = [0] * 39 + [1]
        _, proof = synthesize_loaded(self.model, self.torch, 'text', binding())
        self.assertEqual(proof['generatedPatches'], 40)
        self.assert_restored()

    def test_cap_or_missing_hook_or_post_eos_calls_never_return_audio(self):
        for flags in ([0] * 40, [], [1, 1, 1], [0, 0, 0, 1, 0]):
            self.model.flags = flags
            with self.subTest(flags=flags[:5]), self.assertRaises(InferenceError):
                synthesize_loaded(self.model, self.torch, 'text', binding())
            self.assert_restored()

    def test_changed_or_missing_tokenizer_calls_are_rejected_and_restored(self):
        for mode in ('text', 'tokens', 'missing'):
            torch = Torch()
            model = Model(torch)
            original = model.text_tokenizer
            if mode == 'text':
                model.drift_text = 'shortened'
            elif mode == 'tokens':
                calls = []
                def drift(text):
                    calls.append(text)
                    return [1, 2, 3, 4, 5] if len(calls) == 1 else [7]
                model.text_tokenizer = original = drift
            else:
                model.skip_tokenizer = True
            with self.subTest(mode=mode), self.assertRaises(InferenceError):
                synthesize_loaded(model, torch, 'whole text', binding())
            self.assertIs(model.text_tokenizer, original)
            self.assertIsNone(model.stop_head.hook)
            self.assertIsNone(model.base_lm.kv_cache)

    def test_nonfinite_logits_audio_silence_clipping_and_bad_shapes_fail(self):
        for audio in (Tensor([float('nan')], (1, 1)), Tensor([0.0], (1, 1)),
                      Tensor([1.01], (1, 1)), Tensor([0.5], (2, 1)),
                      Tensor([0.5], (1, 1), dtype='float16'),
                      Tensor([0.5], (1, 1), device='cuda'),
                      Tensor([0.5], (1, 4 * 4 * 1920 + 1))):
            self.model.waveform = audio
            with self.subTest(shape=audio.shape), self.assertRaises(InferenceError):
                synthesize_loaded(self.model, self.torch, 'text', binding())
            self.assert_restored()
        self.model.bad_logits = True
        with self.assertRaises(InferenceError):
            synthesize_loaded(self.model, self.torch, 'text', binding())
        self.assert_restored()

    def test_runtime_failure_restores_resources_without_fallback(self):
        self.model.failure = True
        with self.assertRaises(RuntimeError):
            synthesize_loaded(self.model, self.torch, 'text', binding())
        self.assert_restored()

    def test_position_and_aggregate_memory_checked_before_cache_allocation(self):
        for mode in ('model', 'base', 'residual', 'memory'):
            torch = Torch()
            model = Model(torch)
            if mode == 'model':
                model.config.max_length = 45
            elif mode == 'memory':
                model.base_lm.config.num_hidden_layers = 1024
                model.base_lm.config.kv_channels = 1024
            else:
                getattr(model, mode + '_lm').config.max_position_embeddings = 45
            with self.subTest(mode=mode), self.assertRaises(InferenceError):
                synthesize_loaded(model, torch, 'text', binding())
            self.assertEqual(model.base_lm.allocations, [])
            self.assertEqual(model.residual_lm.allocations, [])


if __name__ == '__main__':
    unittest.main()
