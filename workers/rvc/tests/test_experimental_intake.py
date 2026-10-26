"""Source intake policy fixtures do not demonstrate real speech or ML quality."""
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointError
try:
    from las_rvc.experimental_intake import evaluate_experimental_source, create_experimental_source_preflight
except ImportError:
    evaluate_experimental_source = create_experimental_source_preflight = None
from test_features import PreparedFixture


class ExperimentalPolicyTests(unittest.TestCase):
    @staticmethod
    def manifest():
        return {"format": 1, "dataset_id": "b" * 32, "source": {
            "consent_confirmed": True, "decoded_samples": 15600000, "sample_rate": 40000},
            "duration_seconds": 390.0, "usable_samples": 10400000, "usable_seconds": 260.0,
            "eligible": False, "reasons": ["dataset.usable_duration.short"],
            "qc": {"decoded_clipping_proxy_fraction": 0.0,
                "usable_duration_method": "unique-covered-above-threshold-RMS-samples",
                "speech_music_other_speakers_verified": False},
            "clips": [{"source_start_sample": i * 120000, "source_end_sample": i * 120000 + 148000}
                for i in range(100)]}

    def test_low_data_is_warning_not_verified_speech_or_training_admission(self):
        self.assertIsNotNone(evaluate_experimental_source, "Experimental source policy is not implemented")
        result = evaluate_experimental_source(self.manifest(), opt_in=True)
        self.assertTrue(result["source_preflight_passed"])
        self.assertFalse(result["training_admitted"])
        self.assertFalse(result["original_preparation"]["eligible"])
        self.assertIn("experimental.low_signal_activity", result["warnings"])
        self.assertIn("experimental.speech_unverified", result["warnings"])
        self.assertEqual(390.0, result["metrics"]["recording_seconds"])
        self.assertEqual(260.0, result["metrics"]["signal_activity_seconds"])
        self.assertEqual(300.7, result["metrics"]["unique_clip_coverage_seconds"])
        self.assertIsNone(result["metrics"]["speech_duration_seconds"])
        self.assertFalse(result["metrics"]["speech_duration_verified"])

    def test_recording_range_clip_count_and_clipping_remain_hard_rejections(self):
        self.assertIsNotNone(evaluate_experimental_source, "Experimental source policy is not implemented")
        for change, expected in (("clipping", "dataset.clipping.excessive"),
                ("clips", "dataset.clips.insufficient"), ("short_recording", "experimental.recording_duration.invalid")):
            manifest = self.manifest()
            if change == "clipping":
                manifest["qc"]["decoded_clipping_proxy_fraction"] = 0.02
            elif change == "clips":
                manifest["clips"] = manifest["clips"][:29]
                manifest["usable_samples"] = 60 * 40000
                manifest["usable_seconds"] = 60.0
            else:
                manifest["source"]["decoded_samples"] = 299 * 40000
                manifest["duration_seconds"] = 299.0
                for clip in manifest["clips"]:
                    clip["source_end_sample"] = min(clip["source_end_sample"], 299 * 40000)
            # Pure policy expects a validated manifest; still checks reason bindings.
            manifest["reasons"] = ["dataset.usable_duration.short"] + ([expected] if change != "short_recording" else [])
            with self.subTest(change=change):
                result = evaluate_experimental_source(manifest, opt_in=True)
                self.assertFalse(result["source_preflight_passed"])
                self.assertIn(expected, result["hard_reasons"])
                self.assertFalse(result["training_admitted"])

    def test_missing_explicit_opt_in_fails_closed(self):
        self.assertIsNotNone(evaluate_experimental_source, "Experimental source policy is not implemented")
        with self.assertRaises(CheckpointError) as caught:
            evaluate_experimental_source(self.manifest(), opt_in=False)
        self.assertEqual("experimental.opt_in.required", caught.exception.code)


class ExperimentalPublicationTests(PreparedFixture):
    # Reuse only the owned2second preparation setup, not another test suite.
    def test_immutable_source_decision_keeps_original_and_never_admits_training(self):
        self.assertIsNotNone(create_experimental_source_preflight, "Experimental source artifact is not implemented")
        output = self.root / "decision-output"
        output.mkdir()
        before = (self.dataset / "manifest.json").read_bytes()
        result = create_experimental_source_preflight(self.dataset, self.sha, ["clip-000000"], output, opt_in=True)
        raw = (output / "preflight/decision.json").read_bytes()
        self.assertEqual(hashlib.sha256(raw).hexdigest().encode() + b"\n", (output / "preflight/READY").read_bytes())
        self.assertFalse(result["source_preflight_passed"])
        self.assertFalse(result["training_admitted"])
        self.assertEqual(before, (self.dataset / "manifest.json").read_bytes())
        with self.assertRaises(CheckpointError):
            create_experimental_source_preflight(self.dataset, self.sha, ["clip-000000"], output, opt_in=True)

    def test_changed_expected_input_fails_before_publishing(self):
        self.assertIsNotNone(create_experimental_source_preflight, "Experimental source artifact is not implemented")
        output = self.root / "decision-output"
        output.mkdir()
        with self.assertRaises(CheckpointError):
            create_experimental_source_preflight(self.dataset, "c" * 64, ["clip-000000"], output, opt_in=True)
        self.assertEqual([], list(output.iterdir()))


if __name__ == "__main__":
    unittest.main()
