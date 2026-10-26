"""Small synthetic quality fixtures; never reads a personal recording."""
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointError
from las_rvc.recording_dataset import DecodedRecording, prepare_pcm_dataset
try:
    from las_rvc.recording_quality import assess_source_clip, publish_source_quality_audit
except ImportError:
    assess_source_clip = publish_source_quality_audit = None


class RecordingQualityTests(unittest.TestCase):
    def test_clean_signal_not_flagged_but_never_claimed_verified_speech(self):
        self.assertIsNotNone(assess_source_clip)
        audio = (.4 * np.sin(np.arange(80000) * 2 * np.pi * 220 / 40000)).astype(np.float32)
        before = audio.copy()
        result = assess_source_clip(audio, audio)
        self.assertEqual([], result["review_reasons"])
        self.assertFalse(result["requires_review"])
        self.assertFalse(result["speech_verified"])
        self.assertAlmostEqual(20 * np.log10(2.25), result["normalization_gain_db"], places=4)
        np.testing.assert_array_equal(before, audio)

    def test_short_quiet_signal_is_flagged_before_normalization_can_hide_it(self):
        self.assertIsNotNone(assess_source_clip)
        audio = (.003 * np.sin(np.arange(20600) * 2 * np.pi * 220 / 40000)).astype(np.float32)
        result = assess_source_clip(audio, audio)
        self.assertTrue(result["requires_review"])
        self.assertEqual(["clip.short", "clip.source_quiet", "clip.excessive_normalization"], result["review_reasons"])
        self.assertLess(result["source_rms_dbfs"], -40)
        self.assertGreater(result["normalization_gain_db"], 20)

    def test_silence_serializes_finitely_and_invalid_pcm_is_rejected(self):
        self.assertIsNotNone(assess_source_clip)
        silent = np.zeros(40000, np.float32)
        result = assess_source_clip(silent, silent)
        self.assertIsNone(result["source_rms_dbfs"])
        self.assertIsNone(result["normalization_gain_db"])
        self.assertIn("clip.source_silent", result["review_reasons"])
        json.dumps(result, allow_nan=False)
        bad = silent.copy(); bad[5] = np.nan
        with self.assertRaises(CheckpointError):
            assess_source_clip(bad, silent)

    def test_pinned_report_does_not_change_dataset_or_grant_training(self):
        self.assertIsNotNone(publish_source_quality_audit)
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-quality-tests"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="audit-", dir=parent) as directory:
            root = Path(directory)
            master = root / "owned.bin"; master.write_bytes(b"synthetic-owned-master")
            audio = (.1 * np.sin(np.arange(400000) * 2 * np.pi * 220 / 40000)).astype(np.float32)
            prep = root / "prep"; prep.mkdir()
            metadata = {"source_sha256": hashlib.sha256(master.read_bytes()).hexdigest(),
                "source_bytes": master.stat().st_size, "consent_confirmed": True, "sample_rate": 40000,
                "decoded_samples": audio.size, "pcm_sha256": hashlib.sha256(audio.tobytes()).hexdigest()}
            prepare_pcm_dataset(DecodedRecording(audio, metadata), master, prep)
            dataset = prep / "dataset"
            before = (dataset / "manifest.json").read_bytes()
            digest = hashlib.sha256(before).hexdigest()
            output = root / "audit"; output.mkdir()
            result = publish_source_quality_audit(dataset, digest, output)
            self.assertEqual(digest, result["dataset_sha256"])
            self.assertFalse(result["training_admitted"])
            self.assertFalse(result["speech_verified"])
            self.assertTrue(result["all_prepared_clips_reproduced_exactly"])
            self.assertEqual(before, (dataset / "manifest.json").read_bytes())
            payload = (output / "report.json").read_bytes()
            self.assertEqual(hashlib.sha256(payload).hexdigest(), (output / "READY").read_text().strip())
            untouched = root / "wrong"; untouched.mkdir()
            with self.assertRaises(CheckpointError):
                publish_source_quality_audit(dataset, "a" * 64, untouched)
            self.assertEqual([], list(untouched.iterdir()))
            with self.assertRaises(CheckpointError):
                publish_source_quality_audit(dataset, digest, output)
            self.assertEqual(payload, (output / "report.json").read_bytes())


if __name__ == "__main__":
    unittest.main(verbosity=2)
