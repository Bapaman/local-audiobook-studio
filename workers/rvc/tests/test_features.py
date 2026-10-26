"""Small owned PCM fixtures, no private voice or model execution."""
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
    from las_rvc.features import load_prepared_clips, align_rvc_features
except ImportError:
    load_prepared_clips = align_rvc_features = None


class PreparedFixture(unittest.TestCase):
    def setUp(self):
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-feature-tests"
        parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="paired-", dir=parent)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "original.wav"
        self.source.write_bytes(b"RIFF\x18\x00\x00\x00WAVE" + b"\0" * 20)
        audio = (0.07 * np.sin(np.arange(80000) * (2 * np.pi * 220 / 40000))).astype(np.float32)
        metadata = {"source_sha256": hashlib.sha256(self.source.read_bytes()).hexdigest(),
            "source_bytes": 32, "consent_confirmed": True, "sample_rate": 40000,
            "decoded_samples": 80000, "pcm_sha256": hashlib.sha256(audio.tobytes()).hexdigest()}
        output = self.root / "prepared"
        output.mkdir()
        self.manifest = prepare_pcm_dataset(DecodedRecording(audio, metadata), self.source, output)
        self.dataset = output / "dataset"
        self.sha = hashlib.sha256((self.dataset / "manifest.json").read_bytes()).hexdigest()


class FeatureTests(PreparedFixture):
    def test_paired_input_preserves_failed_training_admission(self):
        self.assertIsNotNone(load_prepared_clips, "Verified prepared-clip loading is not implemented")
        with load_prepared_clips(self.dataset, self.sha, ["clip-000000"]) as selection:
            self.assertFalse(selection.training_eligible)
            self.assertEqual(self.sha, selection.manifest_sha256)
            self.assertEqual(1, len(selection.clips))
            clip = selection.clips[0]
            self.assertEqual("clip-000000", clip.id)
            self.assertEqual((80000, 32000), (clip.audio40.size, clip.audio16.size))
            self.assertFalse(clip.audio40.flags.writeable or clip.audio16.flags.writeable)

    def test_changed_selected_clip_is_rejected(self):
        self.assertIsNotNone(load_prepared_clips, "Verified prepared-clip loading is not implemented")
        wave = self.dataset / "clips16k/clip-000000.wav"
        wave.write_bytes(wave.read_bytes() + b"changed")
        with self.assertRaises(CheckpointError) as caught:
            with load_prepared_clips(self.dataset, self.sha, ["clip-000000"]):
                self.fail("Changed clip was accepted")
        self.assertEqual("features.dataset.invalid", caught.exception.code)

    def test_manifest_and_ready_must_agree_with_expected_digest(self):
        self.assertIsNotNone(load_prepared_clips, "Verified prepared-clip loading is not implemented")
        (self.dataset / "READY").write_bytes(b"a" * 64 + b"\n")
        with self.assertRaises(CheckpointError) as caught:
            with load_prepared_clips(self.dataset, self.sha, ["clip-000000"]):
                self.fail("Invalid READY was accepted")
        self.assertEqual("features.dataset.invalid", caught.exception.code)

    def test_self_consistent_republication_rejects_original_known_digest(self):
        altered = json.loads((self.dataset / "manifest.json").read_bytes())
        altered["dataset_id"] = "c" * 32
        raw = json.dumps(altered, sort_keys=True, separators=(",", ":")).encode()
        (self.dataset / "manifest.json").write_bytes(raw)
        (self.dataset / "READY").write_bytes(hashlib.sha256(raw).hexdigest().encode() + b"\n")
        with self.assertRaises(CheckpointError):
            with load_prepared_clips(self.dataset, self.sha, ["clip-000000"]):
                self.fail("Self-consistent replacement accepted against original digest")

    def test_duplicate_unknown_and_escaping_clip_ids_are_rejected(self):
        self.assertIsNotNone(load_prepared_clips, "Verified prepared-clip loading is not implemented")
        for ids in ([], ["clip-000000"] * 2, ["../master.bin"], ["clip-000099"], [True]):
            with self.subTest(ids=ids), self.assertRaises(CheckpointError):
                with load_prepared_clips(self.dataset, self.sha, ids):
                    self.fail("Invalid selection was accepted")

    def test_native_repeat_trim_and_pitch_interpolation(self):
        self.assertIsNotNone(align_rvc_features, "Native feature alignment is not implemented")
        wave = np.full(16000, 0.05, dtype=np.float32)
        phone = np.zeros((19, 768), dtype=np.float32)
        phone[:, 0] = np.arange(19)
        f0 = np.full(41, 220, dtype=np.float32)
        f0[:3] = [0, 50, 1100]
        result = align_rvc_features(wave, phone, f0)
        self.assertEqual((38, 768), result.phone.shape)
        self.assertEqual(15200, result.wave.size)
        np.testing.assert_array_equal(result.phone[:4, 0], [0, 0, 1, 1])
        np.testing.assert_array_equal(result.pitch[:3], [1, 1, 255])
        np.testing.assert_array_equal(result.pitchf[:3], [50, 50, 1100])
        self.assertEqual((np.float32, np.int64), (result.phone.dtype, result.pitch.dtype))

    def test_invalid_and_all_unvoiced_features_fail_without_padding(self):
        self.assertIsNotNone(align_rvc_features, "Native feature alignment is not implemented")
        wave = np.full(16000, 0.05, dtype=np.float32)
        phone = np.zeros((19, 768), dtype=np.float32)
        f0 = np.full(41, 220, dtype=np.float32)
        cases = [(wave, phone[:, :256], f0), (wave, phone[:10], f0),
            (wave, phone, f0[:10]), (wave, phone, np.zeros(41, dtype=np.float32)),
            (wave, phone + np.nan, f0), (wave, phone, f0 + np.inf),
            (wave, phone, np.full(41, 1101, dtype=np.float32))]
        for args in cases:
            with self.subTest(shapes=[x.shape for x in args]), self.assertRaises(CheckpointError):
                align_rvc_features(*args)


if __name__ == "__main__":
    unittest.main()
