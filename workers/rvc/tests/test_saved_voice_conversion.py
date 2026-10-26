"""Synthetic saved-voice boundaries: no Torch, model loads or child processes."""
from contextlib import contextmanager
from contextlib import redirect_stdout
import hashlib
import io
import json
import os
from pathlib import Path
import struct
import sys
import runpy
import tempfile
import unittest
from unittest.mock import patch
import wave

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
try:
    from las_rvc.saved_voice_conversion import (ConversionError, align_conversion_features,
        convert_samples, read_request, run_conversion)
except ImportError:
    ConversionError = align_conversion_features = convert_samples = read_request = run_conversion = None


def write_json(path, value):
    raw = json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
    path.write_bytes(raw)
    return hashlib.sha256(raw).hexdigest()


def seal(root, filename, value):
    root.mkdir()
    digest = write_json(root / filename, value)
    (root / "READY").write_bytes(digest.encode() + b"\n")
    return digest


def wav_bytes(samples, rate=48000, channels=1):
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as audio:
        audio.setnchannels(channels)
        audio.setsampwidth(2)
        audio.setframerate(rate)
        audio.writeframes(samples.astype("<i2").tobytes())
    return buffer.getvalue()


class SavedVoiceGeometryTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(convert_samples, "separate saved-voice converter is missing")

    def test_context_windows_are_bounded_and_overlap_joins_constant_without_gain(self):
        sizes = []
        def kernel(audio40):
            sizes.append(len(audio40))
            return np.full(audio40.size, .25, np.float32)
        output = convert_samples(np.full(7 * 48000 + 13, .1, np.float32), kernel)
        self.assertEqual(336013, len(output))
        self.assertEqual(np.float32, output.dtype)
        self.assertEqual([200000, 200000, 128400], sizes)
        # Resampling may ring at the two end points; interior must remain flat.
        np.testing.assert_allclose(output[200:-200], .25, atol=2e-4)
        self.assertLess(float(np.max(np.abs(np.diff(output[200:-200])))), .0005)

    def test_short_tail_single_sample_and_all_silence_keep_exact_length(self):
        for count in (1, 13, 479, 480, 48001, 144001):
            with self.subTest(count=count):
                result = convert_samples(np.full(count, .1, np.float32), lambda a: np.full(a.size, .2, np.float32))
                self.assertEqual(count, result.size)
                self.assertTrue(np.isfinite(result).all())
        def no_silent_kernel(_):
            self.fail("silent source must not create synthesized noise")
        result = convert_samples(np.zeros(48001, np.float32), no_silent_kernel)
        self.assertEqual(48001, result.size)
        self.assertEqual(0, np.count_nonzero(result))

    def test_native_bad_geometry_nonfinite_and_out_of_range_never_get_repaired(self):
        source = np.full(48000, .1, np.float32)
        for kernel in (lambda a: a[:-1], lambda a: a.astype(np.float64),
                lambda a: np.full(a.size, np.nan, np.float32),
                lambda a: np.full(a.size, 1.01, np.float32)):
            with self.assertRaises(ConversionError): convert_samples(source, kernel)
        for source in (np.empty(0, np.float32), np.zeros(120 * 48000 + 1, np.float32),
                np.zeros(13, np.float64), np.full(13, np.inf, np.float32)):
            with self.assertRaises(ConversionError): convert_samples(source, lambda a: a)

    def test_different_window_values_are_crossfaded_not_concatenated(self):
        values = iter((.1, .3))
        result = convert_samples(np.full(4 * 48000, .1, np.float32),
            lambda a: np.full(a.size, next(values), np.float32))
        self.assertAlmostEqual(.1, float(result[137000]), places=3)
        self.assertAlmostEqual(.2, float(result[141600]), places=3)
        self.assertAlmostEqual(.3, float(result[145000]), places=3)
        self.assertLess(float(np.max(np.abs(np.diff(result[139100:144100])))), .001)

    def test_new_profile_preserves_unvoiced_pitch_and_pads_only_native_feature_tail(self):
        phone = np.arange(249 * 768, dtype=np.float32).reshape(249, 768) / np.float32(300000)
        f0 = np.full(501, 100, np.float32)
        f0[100:200] = 0
        aligned_phone, pitch, pitchf = align_conversion_features(200000, phone, f0)
        self.assertEqual((500, 768), aligned_phone.shape)
        np.testing.assert_array_equal(aligned_phone[-2:], np.repeat(phone[-1:], 2, axis=0))
        self.assertEqual(0, np.count_nonzero(pitchf[100:200]))
        self.assertTrue(np.all(pitch[100:200] == 1))
        self.assertTrue(np.all(pitchf[:100] == 100))
        for broken in (f0[:-1], np.full(501, -1, np.float32), np.full(501, np.nan, np.float32)):
            with self.assertRaises(ConversionError): align_conversion_features(200000, phone, broken)
        _, pitch, pitchf = align_conversion_features(200000, phone, np.zeros(501, np.float32))
        self.assertEqual(0, np.count_nonzero(pitchf))
        self.assertTrue(np.all(pitch == 1))
        for broken in (phone[:-1], phone.astype(np.float64), np.full((249, 768), np.inf, np.float32)):
            with self.assertRaises(ConversionError): align_conversion_features(200000, broken, f0)


class SavedVoiceRequestTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(run_conversion, "saved-voice request adapter is missing")
        self.temp = tempfile.TemporaryDirectory(prefix="sv-convert-")
        self.addCleanup(self.temp.cleanup)
        self.project = Path(self.temp.name)
        self.source = self.project / "new-tts.wav"
        self.source.write_bytes(wav_bytes(np.full(48001, 3000, np.int16)))
        self.model_source = self.project / "recording.wav"
        self.model_source.write_bytes(wav_bytes(np.full(48000, 2000, np.int16)))
        self.runtime = {}
        for key in ("source_root", "feature_asset_root", "base_asset_root"):
            directory = self.project / key
            directory.mkdir()
            self.runtime[key] = str(directory)
        admission_sha = "5" * 64
        inputs = {"dataset_root": str(self.project / "dataset"), "dataset_sha256": "1" * 64,
            "source_root": self.runtime["source_root"], "base_asset_root": self.runtime["base_asset_root"]}
        for key in ("preflight", "corpus", "candidate", "admission"):
            inputs[key] = {"root": str(self.project / key), "sha256": admission_sha if key == "admission" else "2" * 64}
        files = {}
        checkpoint = self.project / "checkpoint"
        checkpoint.mkdir()
        for name in ("generator.pt", "discriminator.pt", "training.pt"):
            raw = ("synthetic-" + name).encode()
            (checkpoint / name).write_bytes(raw)
            files[name] = {"bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}
        position = {"global_step": 1, "completed_epoch": 0, "next_ordinal": 1, "epoch_size": 3,
            "admission_sha256": admission_sha}
        manifest = {"format": 2, "purpose": "complete-update-checkpoint", "identity": {"run_id": "a" * 32},
            "position": position, "files": files}
        checkpoint_sha = write_json(checkpoint / "manifest.json", manifest)
        (checkpoint / "READY").write_bytes(checkpoint_sha.encode() + b"\n")
        point = {"run_id": "a" * 32, "checkpoint_root": str(checkpoint), "checkpoint_manifest_sha256": checkpoint_sha, **position}
        self.receipt_path = self.project / "accepted-update.json"
        receipt_sha = write_json(self.receipt_path, {"purpose": "supervisor-accepted-personal-native-update", "point": point,
            "native_worker_exit_code": 0, "native_worker_timed_out": False, "native_worker_deadline_ms": 60000,
            "accepted_after_native_worker_exit": True, "quality_established": False, "auto_activate_voice": False})
        self.model_source_ref = {"path": str(self.model_source), "sha256": hashlib.sha256(self.model_source.read_bytes()).hexdigest(), "consent_confirmed": True}
        self.state = {"format": 1, "purpose": "local-personal-training-workflow-state", "request": {"path": "unused", "sha256": "0" * 64},
            "source": self.model_source_ref, "run_id": "a" * 32, "inputs": inputs,
            "accepted": {"checkpoint": {"root": str(checkpoint), "sha256": checkpoint_sha}, "receipt": {"path": str(self.receipt_path), "sha256": receipt_sha}},
            "global_step": 1, "completed_epoch": 0, "epoch_size": 3, "stop_reason": "initial_checkpoint"}
        state_root = self.project / "saved-state"
        state_sha = seal(state_root, "workflow_state.json", self.state)
        self.output = self.project / "conversion"
        self.output.mkdir()
        self.request = {"format": 1, "purpose": "saved-voice-audio-conversion", "project_root": str(self.project),
            "workflow": {"path": str(state_root / "workflow_state.json"), "sha256": state_sha},
            "source": {"path": str(self.source), "sha256": hashlib.sha256(self.source.read_bytes()).hexdigest()},
            "runtime": {key: self.runtime[key] for key in ("source_root", "feature_asset_root")},
            "output_root": str(self.output), "profile_version": "rvc-saved-voice-cpu-v1"}
        self.request_path = self.project / "convert-request.json"
        self.request_sha = write_json(self.request_path, self.request)
        self.calls = 0

    @contextmanager
    def factory(self, request, state):
        self.calls += 1
        yield lambda a: np.full(a.size, .125, np.float32)

    def run_action(self, events=None):
        return run_conversion(self.request_path, self.request_sha, self.output,
            kernel_factory=self.factory, emit=(events if events is not None else []).append)

    def test_fresh_candidate_binds_new_audio_and_exact_frozen_model_without_acceptance(self):
        events = []
        control = self.run_action(events)
        self.assertEqual("saved_voice_conversion_control", control["phase"])
        candidate = self.output / "candidate"
        self.assertEqual(str(candidate), control["candidate_root"])
        self.assertEqual({"final.wav", "manifest.json", "READY"}, {p.name for p in candidate.iterdir()})
        raw = (candidate / "manifest.json").read_bytes()
        self.assertEqual(hashlib.sha256(raw).hexdigest(), control["manifest_sha256"])
        manifest = json.loads(raw)
        self.assertEqual(self.request["workflow"], manifest["workflow"])
        self.assertEqual(self.request["source"], manifest["source"])
        self.assertEqual(self.request_sha, manifest["request_sha256"])
        self.assertEqual(48001, manifest["sample_count"])
        self.assertEqual(1, manifest["selected_point"]["global_step"])
        self.assertIs(False, manifest["quality_established"])
        self.assertIs(False, manifest["source_mixing"])
        audio = (candidate / "final.wav").read_bytes()
        self.assertEqual({"bytes": 96046, "sha256": hashlib.sha256(audio).hexdigest()}, manifest["files"]["final.wav"])
        with wave.open(io.BytesIO(audio)) as wav:
            self.assertEqual((1, 2, 48000, 48001), (wav.getnchannels(), wav.getsampwidth(), wav.getframerate(), wav.getnframes()))
        self.assertTrue(all("path" not in event and "candidate_root" not in event for event in events))

    def test_request_source_model_or_checkpoint_tamper_rejected_before_native_factory(self):
        for target in (self.source, self.model_source, self.receipt_path, self.project / "checkpoint/generator.pt",
                self.project / "checkpoint/manifest.json", self.project / "checkpoint/READY", Path(self.request["workflow"]["path"])):
            with self.subTest(target=target.name):
                original = target.read_bytes()
                target.write_bytes(original + b"tamper")
                with self.assertRaises(ConversionError): self.run_action()
                self.assertEqual(0, self.calls)
                target.write_bytes(original)
        with self.assertRaises(ConversionError): read_request(self.request_path, "f" * 64, self.output)

    def test_foreign_project_paths_unknown_profile_and_extra_fields_fail_closed(self):
        for field, value in (("project_root", str(self.output)), ("profile_version", "unknown"), ("unknown", True)):
            with self.subTest(field=field):
                request = {**self.request, field: value}
                digest = write_json(self.request_path, request)
                with self.assertRaises(ConversionError): run_conversion(self.request_path, digest, self.output, kernel_factory=self.factory)
                self.assertEqual(0, self.calls)

    def test_source_geometry_and_duration_are_rejected_without_model_load(self):
        for raw in (wav_bytes(np.zeros(10, np.int16), rate=40000), wav_bytes(np.zeros(10, np.int16), channels=2),
                wav_bytes(np.empty(0, np.int16)), wav_bytes(np.zeros(120 * 48000 + 1, np.int16)),
                self.source.read_bytes()[:-1]):
            self.source.write_bytes(raw)
            self.request["source"]["sha256"] = hashlib.sha256(raw).hexdigest()
            digest = write_json(self.request_path, self.request)
            with self.assertRaises(ConversionError): run_conversion(self.request_path, digest, self.output, kernel_factory=self.factory)
            self.assertEqual(0, self.calls)

    def test_session_receipt_is_accepted_without_discovering_latest(self):
        initial = json.loads(self.receipt_path.read_bytes())
        session = self.project / "selection"
        receipt_sha = seal(session, "ledger.json", {**{k: v for k, v in initial.items() if k != "point"},
            "purpose": "supervisor-accepted-personal-native-session", "selected_point": initial["point"], "native_worker_deadline_ms": 3600000})
        self.state["accepted"]["receipt"] = {"path": str(session / "ledger.json"), "sha256": receipt_sha}
        state_path = Path(self.request["workflow"]["path"])
        state_sha = write_json(state_path, self.state)
        (state_path.parent / "READY").write_bytes(state_sha.encode() + b"\n")
        self.request["workflow"]["sha256"] = state_sha
        self.request_sha = write_json(self.request_path, self.request)
        self.assertEqual("saved_voice_conversion_control", self.run_action()["phase"])

    def test_stop_before_start_gate_or_between_windows_never_publishes_ready(self):
        (self.output / "STOP").write_bytes(b"stop")
        with patch.dict(os.environ, {"LAS_CONVERSION_START_GATE": str(self.output / "START")}):
            with self.assertRaises(ConversionError) as error: self.run_action()
        self.assertEqual("conversion.stopped", error.exception.code)
        self.assertEqual(0, self.calls)
        self.assertFalse((self.output / "READY").exists())
        (self.output / "STOP").unlink()
        self.source.write_bytes(wav_bytes(np.full(7 * 48000, 3000, np.int16)))
        self.request["source"]["sha256"] = hashlib.sha256(self.source.read_bytes()).hexdigest()
        self.request_sha = write_json(self.request_path, self.request)
        @contextmanager
        def stop_kernel(request, state):
            def kernel(audio):
                (self.output / "STOP").write_bytes(b"stop")
                return np.full(audio.size, .1, np.float32)
            yield kernel
        with self.assertRaises(ConversionError): run_conversion(self.request_path, self.request_sha, self.output, kernel_factory=stop_kernel)
        self.assertFalse((self.output / "READY").exists())

    def test_wrong_start_gate_nonempty_output_and_invalid_stop_are_not_adopted(self):
        with patch.dict(os.environ, {"LAS_CONVERSION_START_GATE": str(self.project / "foreign-START")}):
            with self.assertRaises(ConversionError): self.run_action()
        (self.output / "unrelated").write_bytes(b"preserve")
        with self.assertRaises(ConversionError): self.run_action()
        self.assertEqual(b"preserve", (self.output / "unrelated").read_bytes())
        (self.output / "unrelated").unlink()
        (self.output / "STOP").write_bytes(bytes(65))
        with self.assertRaises(ConversionError): self.run_action()
        self.assertEqual(0, self.calls)

    def test_owned_request_and_start_are_preserved_beside_exact_candidate(self):
        self.request_path = self.output / "request.json"
        self.request_sha = write_json(self.request_path, self.request)
        (self.output / "START").write_bytes(b"start")
        with patch.dict(os.environ, {"LAS_CONVERSION_START_GATE": str(self.output / "START")}):
            try:
                control = self.run_action()
            except ConversionError as error:
                self.fail("owned request marker rejected: " + error.code)
        self.assertEqual(str(self.output / "candidate"), control["candidate_root"])
        self.assertEqual(b"start", (self.output / "START").read_bytes())
        self.assertEqual(self.request, json.loads(self.request_path.read_bytes()))

    def test_entrypoint_stop_returns_safe_failure_without_torch_import_or_process(self):
        (self.output / "STOP").write_bytes(b"stop")
        script = Path(__file__).resolve().parents[3] / "scripts/training/Convert-RvcSavedVoice.py"
        output = io.StringIO()
        before = "torch" in sys.modules
        with patch.dict(os.environ), patch.object(sys, "argv", [str(script), "--request", str(self.request_path),
                "--request-sha256", self.request_sha, "--output-root", str(self.output)]), redirect_stdout(output):
            entry = runpy.run_path(str(script), run_name="__conversion_test__")
            result = entry["main"]()
        self.assertEqual(1, result)
        self.assertEqual({"phase": "failed", "code": "conversion.stopped"}, json.loads(output.getvalue()))
        self.assertEqual(before, "torch" in sys.modules)
        self.assertFalse((self.output / "candidate").exists())

    def test_missing_gate_times_out_before_source_or_native_access(self):
        # No real wait: the START deadline is the boundary under test.
        self.source.unlink()
        with patch.dict(os.environ, {"LAS_CONVERSION_START_GATE": str(self.output / "START")}), \
                patch("las_rvc.saved_voice_conversion.time.monotonic", side_effect=[0, 16]):
            with self.assertRaises(ConversionError) as error: self.run_action()
        self.assertEqual("conversion.start_gate.timeout", error.exception.code)
        self.assertEqual(0, self.calls)
        self.assertEqual([], list(self.output.iterdir()))

    def test_duplicate_or_boolean_request_fields_are_not_interpreted_as_valid_format(self):
        raw = self.request_path.read_bytes()
        for invalid in (raw[:-1] + b',"format":1}', raw.replace(b'"format":1', b'"format":true')):
            self.request_path.write_bytes(invalid)
            digest = hashlib.sha256(invalid).hexdigest()
            with self.assertRaises(ConversionError): run_conversion(self.request_path, digest, self.output, kernel_factory=self.factory)
            self.assertEqual(0, self.calls)



if __name__ == "__main__":
    unittest.main(verbosity=2)
