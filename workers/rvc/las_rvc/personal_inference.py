"""Short held-out recording reconstruction with the selected saved personal G."""
from contextlib import ExitStack
from dataclasses import asdict
import hashlib
import io
import json
from pathlib import Path
import re
import wave

import numpy as np

from .checkpoints import CheckpointError
from .personal_session import load_selection as load_ledger
from .feature_corpus import CorpusRef, load_persisted_preview
from .feature_models import _write_bytes
from .features import load_prepared_clips
from .owned_files import pin_chain, pinned_file, plain_path
from .personal_update import verify_admitted_training
from .training_admission import _raw, TRAINING_PRESET


def encode_pcm_wav(samples, expected_samples):
    try:
        if (type(expected_samples) is not int or not 12800 <= expected_samples <= 160000
                or expected_samples % 400 or type(samples) is not np.ndarray or samples.dtype != np.float32
                or samples.shape != (expected_samples,) or not np.isfinite(samples).all()
                or np.max(np.abs(samples)) > 1 or np.sqrt(np.mean(samples.astype(np.float64) ** 2)) < 1e-5):
            raise ValueError("finite audible bounded mono waveform")
        pcm = np.rint(samples * 32767).astype("<i2").tobytes()
        buffer = io.BytesIO()
        with wave.open(buffer, "wb") as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(40000)
            wav.writeframes(pcm)
        return buffer.getvalue()
    except Exception:
        raise CheckpointError("inference.audio.invalid") from None


def validate_generator_payload(payload, checkpoint):
    import torch
    from .training_state import _safe_tree
    from .update_checkpoints import UpdateCheckpoint
    try:
        if type(checkpoint) is not UpdateCheckpoint or not isinstance(payload, dict):
            raise ValueError("known complete point")
        expected = {"format": 1, "identity": asdict(checkpoint.identity), "completed_epoch": checkpoint.completed_epoch,
            "next_epoch": checkpoint.next_epoch, "global_step": checkpoint.global_step, "torch_version": "2.10.0+cpu"}
        if set(payload) != {*expected, "model", "optimizer"} or _raw({key: payload[key] for key in expected}) != _raw(expected):
            raise ValueError("generator point header")
        _safe_tree(payload)
        weights = payload["model"]
        if not isinstance(weights, dict) or not weights:
            raise ValueError("generator state")
        for name, tensor in weights.items():
            if (type(name) is not str or not isinstance(tensor, torch.Tensor) or tensor.device.type != "cpu"
                    or tensor.layout != torch.strided or tensor.dtype != torch.float32 or not torch.isfinite(tensor).all()):
                raise ValueError("exact FP32 generator state")
        return weights
    except Exception:
        raise CheckpointError("inference.generator.invalid") from None


def require_validation_clip(clip_id, train_ids, validation_ids):
    if (type(clip_id) is not str or type(train_ids) not in (list, tuple) or type(validation_ids) not in (list, tuple)
            or not train_ids or not validation_ids or set(train_ids) & set(validation_ids)
            or clip_id not in validation_ids or clip_id in train_ids):
        raise CheckpointError("inference.validation_clip.invalid")
    return clip_id


def _generator(source_root, checkpoint):
    import torch
    from .rvc_kernel import SOURCE_MANIFEST, _verify_stream, _import_verified
    if str(torch.__version__) != "2.10.0+cpu" or torch.version.cuda is not None:
        raise CheckpointError("inference.runtime.incompatible")
    torch.set_num_threads(2)
    with ExitStack() as pins:
        sources = {}
        for relative, expected in SOURCE_MANIFEST.items():
            stream = pins.enter_context(pinned_file(Path(source_root) / relative))
            _verify_stream(stream, expected)
            sources[relative] = stream.read()
        config = json.loads(sources["configs/v1/40k.json"])
        models, _, _, _ = _import_verified(Path(source_root), sources)
        with checkpoint.open_payload("generator.pt") as stream:
            weights = validate_generator_payload(torch.load(stream, map_location="cpu", weights_only=True), checkpoint)
        with torch.device("cpu"):
            model = models.SynthesizerTrnMs768NSFsid(1025, 32, **config["model"], is_half=False, sr=40000).float()
        model.load_state_dict(weights, strict=True)
        model.eval().requires_grad_(False)
        return model


def run_personal_inference(inputs, ledger_ref, clip_id, output_root, *, emit=lambda event: None):
    """No training, TTS, feature re-extraction or stock conversion pipeline."""
    try:
        with ExitStack() as pins:
            root = plain_path(Path(output_root))
            pin_chain(pins, root)
            if any(root.iterdir()): raise ValueError("fresh owned audio candidate")
            ledger, _ = load_ledger(pins, ledger_ref)
            if ledger["admission_sha256"] != inputs.admission.sha256:
                raise ValueError("same admitted recording")
            admitted = pins.enter_context(verify_admitted_training(inputs))
            require_validation_clip(clip_id, admitted.train_ids, admitted.decision["split"]["validation_clip_ids"])
            selected_point = ledger["selected_point"]
            from .update_checkpoints import load_update_checkpoint
            checkpoint = load_update_checkpoint(Path(selected_point["checkpoint_root"]), selected_point["checkpoint_manifest_sha256"],
                admitted.run_identity(ledger["run_id"]), admitted.train_ids, inputs.admission.sha256, TRAINING_PRESET["sampler_seed"])
            if checkpoint.global_step != selected_point["global_step"]:
                raise ValueError("selected complete cursor")
            shard = next(s for s in admitted.corpus["shards"] if clip_id in s["selected_clip_ids"])
            ref = next(r for r in admitted.preview_refs if str(r.root) == shard["root"] and r.sha256 == shard["manifest_sha256"])
            selection = pins.enter_context(load_prepared_clips(inputs.dataset_root, inputs.dataset_sha256, shard["selected_clip_ids"]))
            stored = pins.enter_context(load_persisted_preview(ref, selection))
            aligned = next(c.aligned for c in stored.clips if c.id == clip_id)
            frames = int(aligned.phone.shape[0])
            emit({"phase": "personal_inference_inputs_verified", "training_updates": checkpoint.global_step, "frames": frames})
            import torch
            model = _generator(inputs.source_root, checkpoint)
            torch.manual_seed(1234)
            with torch.inference_mode(), torch.device("cpu"):
                output = model.infer(torch.from_numpy(aligned.phone.copy()).unsqueeze(0), torch.tensor([frames], dtype=torch.int64),
                    torch.from_numpy(aligned.pitch.copy()).unsqueeze(0), torch.from_numpy(aligned.pitchf.copy()).unsqueeze(0),
                    torch.tensor([0], dtype=torch.int64))[0]
                if output.shape != (1, 1, frames * 400) or output.dtype != torch.float32 or output.device.type != "cpu":
                    raise ValueError("native inference waveform geometry")
                samples = output[0, 0].numpy().copy()
            originals = {"original.wav": encode_pcm_wav(aligned.wave, frames * 400),
                "converted.wav": encode_pcm_wav(samples, frames * 400)}
            files = {}
            for name, raw in originals.items():
                _write_bytes(root / name, raw)
                files[name] = {"bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}
            manifest = {"format": 1, "purpose": "personal-saved-generator-validation-reconstruction", "sample_rate": 40000,
                "ledger": {"root": str(ledger_ref.root), "sha256": ledger_ref.sha256}, "selected_point": selected_point,
                "clip_id": clip_id, "split": "validation", "frames": frames, "training_updates": checkpoint.global_step,
                "inference_seed": 1234, "index_rate": 0, "pitch_shift": 0, "source_mixing": False,
                "new_text_synthesis": False, "quality_established": False, "auto_activate_voice": False, "files": files}
            raw = _raw(manifest)
            digest = hashlib.sha256(raw).hexdigest()
            _write_bytes(root / "manifest.json", raw)
            _write_bytes(root / "READY", digest.encode() + b"\n")
            emit({"phase": "personal_inference_candidate_written", "seconds": frames / 100, "quality_established": False})
            return CorpusRef(root, digest)
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("inference.personal.failed") from None


def accept_audio_candidate(control, output_root, *, expected_request, worker_exit_code, worker_timed_out, deadline_ms):
    if (type(worker_exit_code) is not int or worker_exit_code != 0 or worker_timed_out is not False
            or type(deadline_ms) is not int or deadline_ms != 60000):
        raise CheckpointError("inference.supervisor.unaccepted")
    try:
        with ExitStack() as pins:
            root = plain_path(Path(output_root))
            pin_chain(pins, root)
            if (type(control) is not dict or set(control) != {"phase", "candidate_root", "manifest_sha256"}
                    or control["phase"] != "personal_audio_control" or control["candidate_root"] != str(root)
                    or type(control["manifest_sha256"]) is not str or re.fullmatch(r"[0-9a-f]{64}", control["manifest_sha256"]) is None
                    or {p.name for p in root.iterdir()} != {"original.wav", "converted.wav", "manifest.json", "READY"}):
                raise ValueError("returned owned candidate")
            stream = pins.enter_context(pinned_file(root / "manifest.json"))
            raw = stream.read(65537)
            ready = pins.enter_context(pinned_file(root / "READY"))
            if len(raw) > 65536 or hashlib.sha256(raw).hexdigest() != control["manifest_sha256"] or ready.read(66) != control["manifest_sha256"].encode() + b"\n":
                raise ValueError("returned manifest identity")
            manifest = json.loads(raw)
            known_ledger, _ = load_ledger(pins, CorpusRef(Path(expected_request["ledger"]["root"]), expected_request["ledger"]["sha256"]))
            if (type(manifest["format"]) is not int or manifest["format"] != 1
                    or manifest["purpose"] != "personal-saved-generator-validation-reconstruction"
                    or manifest["ledger"] != expected_request["ledger"] or manifest["clip_id"] != expected_request["clip_id"]
                    or manifest["split"] != "validation" or manifest["source_mixing"] is not False
                    or manifest["new_text_synthesis"] is not False or manifest["quality_established"] is not False
                    or manifest["auto_activate_voice"] is not False or type(manifest["frames"]) is not int
                    or not 32 <= manifest["frames"] <= 400 or manifest["sample_rate"] != 40000
                    or _raw(manifest["selected_point"]) != _raw(known_ledger["selected_point"])
                    or type(manifest["training_updates"]) is not int or manifest["training_updates"] != known_ledger["selected_point"]["global_step"]
                    or _raw([manifest["inference_seed"], manifest["index_rate"], manifest["pitch_shift"]]) != _raw([1234, 0, 0])
                    or set(manifest["files"]) != {"original.wav", "converted.wav"}):
                raise ValueError("request-bound reconstruction manifest")
            expected_samples = manifest["frames"] * 400
            for name, metadata in manifest["files"].items():
                stream = pins.enter_context(pinned_file(root / name))
                wav_raw = stream.read(320045)
                if metadata != {"bytes": len(wav_raw), "sha256": hashlib.sha256(wav_raw).hexdigest()} or len(wav_raw) != 44 + expected_samples * 2:
                    raise ValueError("serialized waveform identity")
                with wave.open(io.BytesIO(wav_raw), "rb") as wav:
                    if (wav.getnchannels(), wav.getsampwidth(), wav.getframerate(), wav.getnframes(), wav.getcomptype()) != (1, 2, 40000, expected_samples, "NONE"):
                        raise ValueError("PCM WAV geometry")
                    pcm_raw = wav.readframes(expected_samples)
                    if len(pcm_raw) != expected_samples * 2: raise ValueError("incomplete decoded PCM")
                    pcm = np.frombuffer(pcm_raw, dtype="<i2").astype(np.float64) / 32767
                    rms = np.sqrt(np.mean(pcm ** 2))
                    if not np.isfinite(rms) or rms < 1e-5: raise ValueError("silent or invalid encoded audio")
            receipt = {"format": 1, "purpose": "supervisor-accepted-personal-audio", "candidate": control,
                "native_worker_exit_code": 0, "native_worker_timed_out": False, "native_worker_deadline_ms": 60000,
                "accepted_after_native_worker_exit": True, "quality_established": False, "auto_activate_voice": False}
            _write_bytes(root / "accepted-audio.json", _raw(receipt))
            return receipt
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("inference.supervisor.invalid") from None
