"""Ordinary CPU/PCM boundaries; never a trained voice or native inference proof."""
import io
import hashlib
import tempfile
import struct
from dataclasses import asdict
from pathlib import Path
import sys
import unittest
import wave
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointError
try:
    from las_rvc.personal_inference import encode_pcm_wav, validate_generator_payload, accept_audio_candidate, require_validation_clip
except ImportError:
    encode_pcm_wav = validate_generator_payload = accept_audio_candidate = require_validation_clip = None


class PersonalInferenceTests(unittest.TestCase):
    def test_candidate_requires_known_ledger_point_hash_and_successful_exit_before_acceptance(self):
        from las_rvc.continuation_ledger import ReceiptRef, create_continuation
        from las_rvc.feature_corpus import CorpusRef
        from las_rvc.feature_models import _write_bytes
        from las_rvc.training_admission import _raw
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-feature-tests"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="personal-audio-", dir=parent) as temporary:
            root = Path(temporary)
            proof = root / "proof2"
            proof.mkdir()
            point = {"phase": "personal_checkpoint_control", "checkpoint_root": str(proof / "checkpoint"),
                "checkpoint_manifest_sha256": "b" * 64, "run_id": "1" * 32, "admission_sha256": "2" * 64,
                "global_step": 2, "completed_epoch": 0, "next_ordinal": 2, "epoch_size": 125}
            receipt_raw = _raw({"format": 1, "purpose": "supervisor-accepted-personal-native-update", "point": point,
                "native_worker_exit_code": 0, "native_worker_timed_out": False, "native_worker_deadline_ms": 60000,
                "accepted_after_native_worker_exit": True, "quality_established": False, "auto_activate_voice": False})
            receipt_path = proof / "accepted-update.json"
            _write_bytes(receipt_path, receipt_raw)
            owned = root / "ledger"
            owned.mkdir()
            ledger = create_continuation(owned, ReceiptRef(receipt_path, hashlib.sha256(receipt_raw).hexdigest()), "1" * 32, "2" * 64,
                [CorpusRef(root / "proof1/checkpoint", "a" * 64), CorpusRef(proof / "checkpoint", "b" * 64)])
            request = {"ledger": {"root": str(ledger.root), "sha256": ledger.sha256}, "clip_id": "clip-000125"}
            def candidate(name, selected, malformed=False):
                out = root / name
                out.mkdir()
                raw_wav = encode_pcm_wav((np.sin(np.arange(12800) / 20) * .25).astype(np.float32), 12800)
                if malformed:
                    junk = len(raw_wav) - 52
                    raw_wav = raw_wav[:36] + b"JUNK" + struct.pack("<I", junk) + bytes(junk) + b"data" + struct.pack("<I", 12800 * 2)
                files = {}
                for filename in ("original.wav", "converted.wav"):
                    _write_bytes(out / filename, raw_wav)
                    files[filename] = {"bytes": len(raw_wav), "sha256": hashlib.sha256(raw_wav).hexdigest()}
                manifest = {"format": 1, "purpose": "personal-saved-generator-validation-reconstruction", "ledger": request["ledger"],
                    "selected_point": selected, "clip_id": request["clip_id"], "split": "validation", "sample_rate": 40000, "frames": 32,
                    "training_updates": selected["global_step"], "inference_seed": 1234, "index_rate": 0, "pitch_shift": 0,
                    "source_mixing": False, "new_text_synthesis": False, "quality_established": False, "auto_activate_voice": False, "files": files}
                raw = _raw(manifest)
                digest = hashlib.sha256(raw).hexdigest()
                _write_bytes(out / "manifest.json", raw)
                _write_bytes(out / "READY", digest.encode() + b"\n")
                return out, {"phase": "personal_audio_control", "candidate_root": str(out), "manifest_sha256": digest}
            out, control = candidate("candidate", point)
            for mutation in ({**control, "manifest_sha256": "f" * 64},):
                with self.assertRaises(CheckpointError):
                    accept_audio_candidate(mutation, out, expected_request=request, worker_exit_code=0, worker_timed_out=False, deadline_ms=60000)
                self.assertFalse((out / "accepted-audio.json").exists())
            bad_out, bad_control = candidate("wrong-point", {**point, "global_step": 3, "next_ordinal": 3})
            with self.assertRaises(CheckpointError):
                accept_audio_candidate(bad_control, bad_out, expected_request=request, worker_exit_code=0, worker_timed_out=False, deadline_ms=60000)
            malformed_out, malformed_control = candidate("malformed-pcm", point, malformed=True)
            with self.assertRaises(CheckpointError):
                accept_audio_candidate(malformed_control, malformed_out, expected_request=request, worker_exit_code=0, worker_timed_out=False, deadline_ms=60000)
            self.assertFalse((malformed_out / "accepted-audio.json").exists())
            accepted = accept_audio_candidate(control, out, expected_request=request, worker_exit_code=0, worker_timed_out=False, deadline_ms=60000)
            self.assertIs(True, accepted["accepted_after_native_worker_exit"])
            self.assertTrue((out / "accepted-audio.json").is_file())

    def test_training_or_foreign_clip_cannot_be_used_as_holdout_proof(self):
        self.assertEqual("clip-000125", require_validation_clip("clip-000125", ["clip-000000"], ["clip-000125"]))
        for clip, train, validation in (("clip-000000", ["clip-000000"], ["clip-000125"]),
                ("clip-000999", ["clip-000000"], ["clip-000125"]), ("clip-000125", ["clip-000125"], ["clip-000125"])):
            with self.assertRaises(CheckpointError): require_validation_clip(clip, train, validation)

    def test_generator_payload_requires_exact_point_header_and_cpu_fp32_finite_weights(self):
        import torch
        from las_rvc.checkpoints import RunIdentity
        from las_rvc.update_checkpoints import UpdateCheckpoint, make_update_position
        identity = RunIdentity("1" * 32, "2" * 64, "3" * 64, "4" * 64,
            "81eed5e8f68b6bed1789f682fe78cdd324495afc", "rvc-cpu-experimental-updates-v1", 1, 40000, True)
        point = UpdateCheckpoint(Path.cwd() / "metadata-only-point", identity,
            make_update_position(0, 1, ["clip-000000", "clip-000001"], 1234, "5" * 64), "6" * 64, {}, ("clip-000000", "clip-000001"))
        payload = {"format": 1, "identity": asdict(identity), "completed_epoch": 0, "next_epoch": 1,
            "global_step": 1, "torch_version": "2.10.0+cpu", "model": {"tiny.weight": torch.ones(2, 3)}, "optimizer": {}}
        self.assertIs(payload["model"], validate_generator_payload(payload, point))
        for mutation in ({"global_step": True}, {"identity": {**asdict(identity), "run_id": "f" * 32}},
                {"model": {"tiny.weight": torch.ones(2, 3, dtype=torch.float16)}}, {"model": {"tiny.weight": torch.full((2, 3), float("nan"))}}):
            with self.assertRaises(CheckpointError): validate_generator_payload({**payload, **mutation}, point)

    def test_pcm_exact_duration_and_invalid_or_silent_output_is_rejected(self):
        self.assertIsNotNone(encode_pcm_wav, "Saved personal model audio is not implemented")
        samples = (np.sin(np.arange(12800) / 20) * .25).astype(np.float32)
        raw = encode_pcm_wav(samples, 12800)
        with wave.open(io.BytesIO(raw)) as wav:
            self.assertEqual((1, 2, 40000, 12800), (wav.getnchannels(), wav.getsampwidth(), wav.getframerate(), wav.getnframes()))
        for invalid in (samples[:-1], samples.astype(np.float64), np.full(12800, np.nan, np.float32),
                np.full(12800, 1.1, np.float32), np.zeros(12800, np.float32)):
            with self.assertRaises(CheckpointError): encode_pcm_wav(invalid, 12800)
        for count in (True, 0, 160001):
            with self.assertRaises(CheckpointError): encode_pcm_wav(samples, count)

    def test_timeout_or_nonzero_exit_cannot_accept_even_before_file_access(self):
        self.assertIsNotNone(accept_audio_candidate)
        for exit_code, timeout in ((0, True), (1, False), (False, False)):
            with self.assertRaises(CheckpointError):
                accept_audio_candidate({}, Path.cwd() / "never-created-inference-fixture",
                    expected_request={}, worker_exit_code=exit_code, worker_timed_out=timeout, deadline_ms=60000)


if __name__ == "__main__": unittest.main()
