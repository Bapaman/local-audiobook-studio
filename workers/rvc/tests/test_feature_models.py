"""Trust/publication boundaries; synthetic arrays here are NOT native ML proof."""
from pathlib import Path
import hashlib
import json
import sys
import tempfile
import unittest
from unittest.mock import patch
import io

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointError
from las_rvc.features import PreparedClip, PreparedSelection
try:
    from las_rvc.feature_models import create_feature_models, publish_feature_preview
except ImportError:
    create_feature_models = publish_feature_preview = None


class FeatureModelTests(unittest.TestCase):
    def setUp(self):
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-feature-tests"
        parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="models-", dir=parent)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "source"
        self.assets = self.root / "assets"
        self.output = self.root / "output"
        for directory in (self.source, self.assets, self.output):
            directory.mkdir()
        wave = np.full(16000, 0.05, dtype=np.float32)
        wave16 = np.full(6400, 0.05, dtype=np.float32)
        self.selection = PreparedSelection("a" * 64, "b" * 32, 137, False,
            (PreparedClip("clip-000000", wave, wave16),))

    @staticmethod
    def synthetic_extract(clip):
        # Only exercise the real array publisher/alignment, not voice inference.
        return np.full((19, 768), 0.5, dtype=np.float32), np.full(41, 50, dtype=np.float64)

    def test_invalid_source_rejected_before_native_construction(self):
        self.assertIsNotNone(create_feature_models, "Verified CPU feature models are not implemented")
        with patch("las_rvc.feature_models._build_models") as build:
            with self.assertRaises(CheckpointError) as caught:
                with create_feature_models(self.source, self.assets):
                    self.fail("Unverified source accepted")
            self.assertEqual("features.models.invalid", caught.exception.code)
            build.assert_not_called()

    def test_last_asset_mismatch_rejected_before_native_construction(self):
        self.assertIsNotNone(create_feature_models, "Verified CPU feature models are not implemented")
        source_pins, asset_pins = {}, {}
        for root, pins, names in ((self.source, source_pins, ("one.py", "two.py")),
                (self.assets, asset_pins, ("config.json", "weights.bin"))):
            for name in names:
                content = name.encode()
                (root / name).write_bytes(content)
                pins[name] = (len(content), hashlib.sha256(content).hexdigest())
        (self.assets / "weights.bin").write_bytes(b"bad")
        with patch("las_rvc.feature_models.SOURCE_FILES", source_pins), \
                patch("las_rvc.feature_models.ASSET_FILES", asset_pins), \
                patch("las_rvc.feature_models._build_models") as build:
            with self.assertRaises(CheckpointError):
                with create_feature_models(self.source, self.assets):
                    self.fail("Unverified asset accepted")
            build.assert_not_called()

    def test_only_known_eval_batchnorm_overflow_counter_can_be_reset(self):
        self.assertIsNotNone(create_feature_models, "Verified CPU feature models are not implemented")
        import torch
        from las_rvc.feature_models import _weights
        def serialized(value):
            stream = io.BytesIO()
            torch.save(value, stream)
            stream.seek(0)
            return stream
        # The pinned RMVPE distributor cast integer BN counters to half, causing
        # +inf overflow. They are unused in eval; parameters must remain finite.
        payload = {"unet.encoder.bn.num_batches_tracked": torch.tensor(float("inf"), dtype=torch.float16)}
        result = _weights(serialized(payload), torch, reset_batchnorm_overflow=True)
        counter = result["unet.encoder.bn.num_batches_tracked"]
        self.assertEqual(torch.int64, counter.dtype)
        self.assertEqual(0, counter.item())
        for bad in ({"unet.encoder.bn.weight": torch.tensor(float("inf"), dtype=torch.float16)},
                {"unet.encoder.bn.num_batches_tracked": torch.tensor(float("nan"), dtype=torch.float16)},
                {"unet.encoder.bn.num_batches_tracked": torch.tensor(float("-inf"), dtype=torch.float16)},
                {"unet.encoder.bn.num_batches_tracked": torch.tensor([float("inf")], dtype=torch.float16)}):
            with self.subTest(name=next(iter(bad))), self.assertRaises(ValueError):
                _weights(serialized(bad), torch, reset_batchnorm_overflow=True)
        with self.assertRaises(ValueError):
            _weights(serialized(payload), torch)

    def test_publication_keeps_partial_coverage_and_failed_admission(self):
        self.assertIsNotNone(publish_feature_preview, "Immutable feature publication is not implemented")
        report = publish_feature_preview(self.selection, self.output, self.synthetic_extract)
        bundle = self.output / "features"
        raw = (bundle / "manifest.json").read_bytes()
        manifest = json.loads(raw)
        self.assertEqual(hashlib.sha256(raw).hexdigest().encode() + b"\n", (bundle / "READY").read_bytes())
        self.assertFalse(manifest["input"]["training_eligible"])
        self.assertFalse(manifest["training_admitted"])
        self.assertFalse(manifest["full_dataset_extracted"])
        self.assertEqual(137, manifest["input"]["dataset_clip_count"])
        self.assertEqual(["clip-000000"], manifest["selected_clip_ids"])
        self.assertEqual(38, report["clips"][0]["aligned_frames"])
        np.testing.assert_array_equal(np.load(bundle / "clip-000000/pitch.npy", allow_pickle=False),
            np.full(38, 1, dtype=np.int64))
        for relative, info in manifest["files"].items():
            data = (bundle / relative).read_bytes()
            self.assertEqual((len(data), hashlib.sha256(data).hexdigest()), (info["bytes"], info["sha256"]))

    def test_nonempty_output_rejected_before_extracting(self):
        self.assertIsNotNone(publish_feature_preview, "Immutable feature publication is not implemented")
        (self.output / "belongs-to-user.txt").write_bytes(b"preserve")
        called = []
        with self.assertRaises(CheckpointError):
            publish_feature_preview(self.selection, self.output, lambda clip: called.append(clip))
        self.assertEqual([], called)
        self.assertEqual(b"preserve", (self.output / "belongs-to-user.txt").read_bytes())

    def test_earlier_array_cannot_change_before_publication(self):
        self.assertIsNotNone(publish_feature_preview, "Immutable feature publication is not implemented")
        second = PreparedClip("clip-000001", self.selection.clips[0].audio40, self.selection.clips[0].audio16)
        selection = PreparedSelection("a" * 64, "b" * 32, 137, False,
            (self.selection.clips[0], second))
        def attempt_change(clip):
            if clip.id == "clip-000001":
                earlier = next(self.output.glob("stage-*/clip-000000/phone.npy"))
                earlier.write_bytes(b"tamper-before-ready")
            return self.synthetic_extract(clip)
        with self.assertRaises(CheckpointError):
            publish_feature_preview(selection, self.output, attempt_change)
        self.assertFalse((self.output / "features").exists())

    def test_wrapper_requires_caller_digest_and_short_bounded_cache(self):
        # Script contract regression; bounded real native execution is separate.
        text = (Path(__file__).resolve().parents[3] / "scripts/training/Extract-RvcFeatures.ps1").read_text()
        self.assertIn("[string]$ManifestSha256", text)
        self.assertIn("'--manifest-sha256', $ManifestSha256", text)
        self.assertNotIn("Get-FileHash", text)
        self.assertIn("'.artifacts/rf'", text)
        self.assertIn("$ownedCache.Length -gt 128", text)

    def test_failed_extraction_never_publishes_ready(self):
        self.assertIsNotNone(publish_feature_preview, "Immutable feature publication is not implemented")
        def fail(clip):
            raise ValueError("not private details")
        with self.assertRaises(CheckpointError) as caught:
            publish_feature_preview(self.selection, self.output, fail)
        self.assertEqual("features.preview.failed", caught.exception.code)
        self.assertFalse((self.output / "features").exists())
        self.assertEqual([], list(self.output.rglob("READY")))


if __name__ == "__main__":
    unittest.main()
