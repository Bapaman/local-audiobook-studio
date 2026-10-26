import dataclasses
import hashlib
import sys
import types
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'src' / 'VoxCpmInferenceWorker'))
from inference_contract import (CheckpointBinding, InferenceError, StopEvidence,
                                kv_cache_bytes, plan_synthesis)


def binding(**changes):
    values = dict(project_id='a' * 32, checkpoint_id='b' * 32, global_step=29,
                  manifest_sha256='c' * 64, adapter_sha256='d' * 64,
                  runtime_fingerprint='e' * 64, recipe_sha256='f' * 64,
                  layout_sha256='0' * 64)
    values.update(changes)
    return CheckpointBinding(**values)


class ContractTests(unittest.TestCase):
    def test_budget_covers_prefill_and_final_unused_step_without_truncating(self):
        for count, limit, capacity in ((1, 16, 18), (5, 40, 46), (256, 256, 513)):
            with self.subTest(count=count):
                text = '  Полный текст.\n'
                plan = plan_synthesis(text, list(range(count)), 8192)
                self.assertEqual((plan.patch_limit, plan.cache_capacity), (limit, capacity))
                self.assertEqual(plan.text, text)
                self.assertEqual(plan.tokens, tuple(range(count)))
                self.assertEqual(plan.text_sha256, hashlib.sha256(text.encode('utf-8')).hexdigest())
                if count == 5:
                    self.assertEqual(plan.tokens_sha256,
                                     hashlib.sha256(b'[0,1,2,3,4]').hexdigest())
                with self.assertRaises(dataclasses.FrozenInstanceError):
                    plan.cache_capacity = 8192
        self.assertEqual(plan_synthesis('x', [1], 18).cache_capacity, 18)
        with self.assertRaises(InferenceError):
            plan_synthesis('x', [1], 17)

    def test_invalid_or_oversize_text_is_rejected_instead_of_shortened(self):
        for text in ('', ' \n\t', 'x' * 513, 'x\0', '\ud800', 'x\x7f', 5):
            with self.subTest(text=repr(text)), self.assertRaises(InferenceError):
                plan_synthesis(text, [1], 8192)
        self.assertEqual(plan_synthesis('Я\tтут\r\n', [1], 8192).text, 'Я\tтут\r\n')

    def test_invalid_tokens_and_position_limits_are_rejected(self):
        for tokens in ([], [True], [-1], [1.0], [2**31], [1] * 257, (1,)):
            with self.subTest(tokens=str(tokens)[:32]), self.assertRaises(InferenceError):
                plan_synthesis('text', tokens, 8192)
        for positions in (True, 0, -1, 8192.0):
            with self.assertRaises(InferenceError):
                plan_synthesis('text', [1], positions)

    def test_checkpoint_identity_is_exact_and_immutable(self):
        item = binding()
        item.validate()
        self.assertEqual(item.as_dict()['checkpointId'], 'b' * 32)
        self.assertEqual(item.as_dict()['globalStep'], 29)
        for changes in (dict(project_id='A' * 32), dict(adapter_sha256='d' * 63),
                        dict(global_step=True), dict(global_step=0), dict(global_step=513)):
            with self.subTest(changes=changes), self.assertRaises(InferenceError):
                binding(**changes).validate()

    def test_kv_bytes_are_derived_from_both_key_and_value_fp32_buffers(self):
        config = types.SimpleNamespace(num_hidden_layers=2, num_key_value_heads=2,
                                       hidden_size=8, num_attention_heads=2, kv_channels=None)
        self.assertEqual(kv_cache_bytes(config, 18), 2304)
        config.kv_channels = 3
        self.assertEqual(kv_cache_bytes(config, 18), 1728)
        for field, value in (('num_hidden_layers', 0), ('num_key_value_heads', True),
                             ('kv_channels', -1), ('num_attention_heads', 0)):
            broken = types.SimpleNamespace(**vars(config))
            setattr(broken, field, value)
            with self.subTest(field=field), self.assertRaises(InferenceError):
                kv_cache_bytes(broken, 18)
        config.kv_channels = None
        config.hidden_size = 9
        with self.assertRaises(InferenceError):
            kv_cache_bytes(config, 18)

    def test_natural_eos_on_last_patch_is_not_mistaken_for_cap_exhaustion(self):
        observed = StopEvidence(4)
        for flag in (1, 1, 1, 1):
            observed.observe(flag)
        self.assertEqual(observed.finish(), 4)
        observed = StopEvidence(16)
        for flag in (0, 0, 0, 0, 1):
            observed.observe(flag)
        self.assertEqual(observed.finish(), 5)

    def test_cap_missing_hook_extra_calls_and_non_integer_flags_fail_closed(self):
        for flags in ([], [1, 1, 1], [0, 0, 0, 0], [0, 0, 0, 1, 0],
                      [0, 0, 0, True], [0, 0, 0, 2]):
            with self.subTest(flags=flags), self.assertRaises(InferenceError):
                observed = StopEvidence(4)
                for flag in flags:
                    observed.observe(flag)
                observed.finish()


if __name__ == '__main__':
    unittest.main()
