"""Synthetic structural tensors are not native speech-extraction proof."""
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import numpy as np
import torch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointError
from las_rvc.features import align_rvc_features
try:
    from las_rvc.feature_batch import build_native_feature_batch, _assemble_batch
except ImportError:
    build_native_feature_batch = _assemble_batch = None


class FeatureBatchTests(unittest.TestCase):
    def setUp(self):
        self.wave = np.full(16000, 0.05, dtype=np.float32)
        self.phone = np.zeros((19, 768), dtype=np.float32)
        self.phone[:, 0] = np.arange(19)
        self.f0 = np.full(41, 50, dtype=np.float32)
        self.aligned = align_rvc_features(self.wave, self.phone, self.f0)

    def test_batch_uses_native_shared_frames_and_normal_training_tensors(self):
        self.assertIsNotNone(_assemble_batch, "Native batch assembly is not implemented")
        spectrum = torch.ones((1, 1025, 40), dtype=torch.float32)
        batch = _assemble_batch(self.aligned, spectrum, 16000)
        self.assertEqual((1, 38, 768), tuple(batch.phone.shape))
        self.assertEqual((1, 1025, 38), tuple(batch.spec.shape))
        self.assertEqual((1, 1, 15200), tuple(batch.wave.shape))
        self.assertEqual([38], batch.phone_lengths.tolist())
        self.assertEqual([38], batch.spec_lengths.tolist())
        self.assertEqual([15200], batch.wave_lengths.tolist())
        self.assertEqual([0], batch.speaker_ids.tolist())
        self.assertEqual([0, 0, 1, 1], batch.phone[0, :4, 0].tolist())
        self.assertEqual(torch.int64, batch.pitch.dtype)
        for tensor in vars(batch).values():
            self.assertFalse(torch.is_inference(tensor) or tensor.requires_grad)
            self.assertEqual("cpu", tensor.device.type)

    def test_invalid_full_spectrum_fails_without_padding(self):
        self.assertIsNotNone(_assemble_batch, "Native batch assembly is not implemented")
        for spectrum in (torch.ones((1, 1025, 37)), torch.ones((1, 1025, 38)),
                torch.ones((1, 256, 40)), torch.ones((1, 1025, 40), dtype=torch.float64),
                torch.full((1, 1025, 40), float("nan")), torch.full((1, 1025, 40), -1.0)):
            with self.subTest(shape=tuple(spectrum.shape)), self.assertRaises(CheckpointError):
                _assemble_batch(self.aligned, spectrum, 16000)

    def test_invalid_source_fails_before_audited_kernel_import(self):
        self.assertIsNotNone(build_native_feature_batch, "Native feature batch builder is not implemented")
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-feature-tests"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="batch-", dir=parent) as directory:
            with patch("las_rvc.feature_batch._import_verified") as imported:
                with self.assertRaises(CheckpointError) as caught:
                    build_native_feature_batch(Path(directory), self.wave, self.phone, self.f0)
                self.assertEqual("features.batch.source_invalid", caught.exception.code)
                imported.assert_not_called()


if __name__ == "__main__":
    unittest.main()
