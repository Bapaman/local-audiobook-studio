"""Owned synthetic fixtures only; never reads the user's voice recording."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import numpy as np
from scipy.io import wavfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointError
try:
    from las_rvc.recording_dataset import decode_recording
except ModuleNotFoundError:
    decode_recording = None
try:
    from las_rvc.recording_dataset import DecodedRecording, prepare_pcm_dataset
except ImportError:
    DecodedRecording = prepare_pcm_dataset = None


class RecordingDatasetTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(decode_recording, "Explicit verified decoder is not implemented")
        parent = Path(__file__).resolve().parents[3] / ".artifacts" / "rvc-dataset-tests"
        parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="prep-", dir=parent)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        # Public contract tests use inert files and controlled native responses.
        self.source = self.root / "recording.wav"
        self.source.write_bytes(b"RIFF\x18\x00\x00\x00WAVE" + b"\0" * 20)
        self.ffmpeg = self.root / "ffmpeg.exe"
        self.ffprobe = self.root / "ffprobe.exe"
        self.ffmpeg.write_bytes(b"owned-inert-decoder")
        self.ffprobe.write_bytes(b"owned-inert-probe")
        self.options = dict(source_path=self.source, source_sha256=self.digest(self.source),
            ffmpeg_path=self.ffmpeg, ffmpeg_sha256=self.digest(self.ffmpeg),
            ffprobe_path=self.ffprobe, ffprobe_sha256=self.digest(self.ffprobe), consent=True)

    def digest(self, path):
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def test_consent_required_before_native_invocation(self):
        options = {**self.options, "consent": False}
        with patch("subprocess.run") as run, self.assertRaises(CheckpointError) as caught:
            decode_recording(**options)
        self.assertEqual("recording.consent.required", caught.exception.code)
        run.assert_not_called()

    def test_changed_master_rejected_before_native_invocation(self):
        options = {**self.options, "source_sha256": "a" * 64}
        with patch("subprocess.run") as run, self.assertRaises(CheckpointError) as caught:
            decode_recording(**options)
        self.assertEqual("recording.source.invalid", caught.exception.code)
        run.assert_not_called()

    def test_changed_decoder_rejected_before_native_invocation(self):
        self.ffmpeg.write_bytes(b"changed")
        with patch("subprocess.run") as run, self.assertRaises(CheckpointError) as caught:
            decode_recording(**self.options)
        self.assertEqual("recording.decoder.invalid", caught.exception.code)
        run.assert_not_called()

    def test_probe_duration_and_container_constraints(self):
        probe = {"streams": [{"channels": 1, "codec_type": "audio"}], "format": {"duration": "10"}}
        response = subprocess.CompletedProcess([], 0, json.dumps(probe).encode(), b"")
        with patch("subprocess.run", return_value=response) as run, self.assertRaises(CheckpointError) as caught:
            decode_recording(**self.options)
        self.assertEqual("recording.duration.invalid", caught.exception.code)
        self.assertEqual(1, run.call_count)
        command = run.call_args.args[0]
        self.assertEqual("wav", command[command.index("-f") + 1])
        self.assertEqual("file,pipe", command[command.index("-protocol_whitelist") + 1])

    def test_nonfinite_decoded_pcm_rejected(self):
        probe = {"streams": [{"channels": 1, "codec_type": "audio"}], "format": {"duration": "360"}}
        responses = [subprocess.CompletedProcess([], 0, json.dumps(probe).encode(), b""),
                     subprocess.CompletedProcess([], 0, np.array([float("nan")], dtype="<f4").tobytes(), b"")]
        with patch("subprocess.run", side_effect=responses), self.assertRaises(CheckpointError) as caught:
            decode_recording(**self.options)
        self.assertEqual("recording.decode.invalid", caught.exception.code)

    def prepared(self, samples, output=None):
        self.assertIsNotNone(prepare_pcm_dataset, "Owned PCM dataset preparation is not implemented")
        if output is None:
            output = self.root / "output"
            output.mkdir()
        metadata = {"source_sha256": self.digest(self.source), "source_bytes": self.source.stat().st_size,
            "consent_confirmed": True, "sample_rate": 40000,
            "decoded_samples": int(samples.size), "pcm_sha256": hashlib.sha256(samples.tobytes()).hexdigest()}
        return prepare_pcm_dataset(DecodedRecording(samples, metadata), self.source, output)

    def test_silence_is_ineligible_without_artificial_clips(self):
        result = self.prepared(np.zeros(400000, dtype=np.float32))
        self.assertFalse(result["eligible"])
        self.assertEqual(0, result["usable_samples"])
        self.assertEqual([], result["clips"])
        self.assertTrue((self.root / "output/dataset/READY").is_file())
        self.assertEqual(0, result["qc"].get("signal_activity_samples"))
        self.assertEqual(0, result["qc"].get("unique_clip_coverage_samples"))
        self.assertIsNone(result["qc"].get("speech_duration_seconds"))
        self.assertIs(result["qc"].get("speech_duration_verified"), False)

    def test_finite_paired_clips_preserve_master_and_count_overlap_once(self):
        samples = (0.07 * np.sin(np.arange(400000) * (2 * np.pi * 220 / 40000))).astype(np.float32)
        before = self.source.read_bytes()
        result = self.prepared(samples)
        dataset = self.root / "output/dataset"
        self.assertEqual(before, (dataset / "master.bin").read_bytes())
        self.assertEqual(before, self.source.read_bytes())
        self.assertGreaterEqual(len(result["clips"]), 3)
        total = 0
        for clip in result["clips"]:
            rate40, audio40 = wavfile.read(dataset / clip["wave40k"])
            rate16, audio16 = wavfile.read(dataset / clip["wave16k"])
            self.assertEqual((40000, 16000), (rate40, rate16))
            self.assertEqual(np.float32, audio40.dtype)
            self.assertEqual(np.float32, audio16.dtype)
            self.assertTrue(np.isfinite(audio40).all() and np.isfinite(audio16).all())
            self.assertLessEqual(audio40.size, 148000)
            self.assertLessEqual(float(np.abs(audio40).max()), 0.90001)
            total += audio40.size
        self.assertGreater(total, samples.size)
        self.assertLessEqual(result["usable_samples"], samples.size)
        self.assertLess(result["usable_samples"], total)
        # A synthetic tone is active signal, never established human speech.
        self.assertEqual(400000, result["qc"].get("signal_activity_samples"))
        self.assertEqual(400000, result["qc"].get("unique_clip_coverage_samples"))
        self.assertIs(result["qc"].get("speech_duration_verified"), False)
        self.assertIsNone(result["qc"].get("speech_duration_seconds"))
        manifest_bytes = (dataset / "manifest.json").read_bytes()
        self.assertEqual(hashlib.sha256(manifest_bytes).hexdigest(), (dataset / "READY").read_text().strip())
        for relative, info in result["files"].items():
            self.assertEqual(info["sha256"], self.digest(dataset / relative))

    def test_nonfinite_pcm_does_not_publish(self):
        samples = np.zeros(400000, dtype=np.float32)
        samples[4] = float("nan")
        with self.assertRaises(CheckpointError) as caught:
            self.prepared(samples)
        self.assertEqual("dataset.pcm.invalid", caught.exception.code)
        self.assertEqual([], list((self.root / "output").iterdir()))

    def test_nonempty_root_is_never_adopted_or_overwritten(self):
        output = self.root / "existing"
        output.mkdir()
        marker = output / "foreign.txt"
        marker.write_bytes(b"preserve-this")
        with self.assertRaises(CheckpointError) as caught:
            self.prepared(np.zeros(400000, dtype=np.float32), output)
        self.assertEqual("dataset.root.invalid", caught.exception.code)
        self.assertEqual(b"preserve-this", marker.read_bytes())
        self.assertEqual([marker], list(output.iterdir()))


if __name__ == "__main__":
    unittest.main(verbosity=2)
