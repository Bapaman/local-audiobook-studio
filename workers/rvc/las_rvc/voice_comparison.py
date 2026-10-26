"""Controlled short saved/base/context comparison; never a voice quality gate."""
from contextlib import ExitStack
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
from .feature_models import create_feature_models, _write_bytes
from .features import AlignedFeatures, PreparedClip, load_prepared_clips
from .owned_files import pin_chain, pinned_file, plain_path
from .personal_inference import _generator, encode_pcm_wav, require_validation_clip
from .personal_update import verify_admitted_training
from .training_admission import _raw, TRAINING_PRESET

WAV_NAMES = ("original.wav", "saved-direct.wav", "saved-context.wav", "base-direct.wav")
PROFILE = {"version": "rvc-context-comparison-cpu-v1", "context_seconds": 1,
    "padding": "reflect", "additional_filter": False, "feature_sample_rate": 16000,
    "sample_rate": 40000, "seed": 1234, "pitch_shift": 0, "index_rate": 0,
    "source_mixing": False, "new_text_synthesis": False, "quality_established": False,
    "auto_activate_voice": False}


def prepare_context_audio(audio16):
    try:
        if (type(audio16) is not np.ndarray or audio16.dtype != np.float32 or audio16.ndim != 1
                or not 6400 <= audio16.size <= 59200 or not np.isfinite(audio16).all()
                or np.max(np.abs(audio16)) > 1.01):
            raise ValueError("bounded mono source")
        return np.pad(audio16, (16000, 16000), mode="reflect")
    except Exception:
        raise CheckpointError("inference.context.invalid") from None


def align_context_features(padded16, phone768, f0):
    """Same repeat2/interpolated F0 as the admitted profile, with context only."""
    try:
        if (type(padded16) is not np.ndarray or padded16.dtype != np.float32 or padded16.ndim != 1
                or not 38400 <= padded16.size <= 91200 or not np.isfinite(padded16).all()
                or type(phone768) is not np.ndarray or phone768.dtype != np.float32
                or phone768.shape != ((padded16.size - 400) // 320 + 1, 768)
                or not np.isfinite(phone768).all() or type(f0) is not np.ndarray or f0.ndim != 1
                or f0.dtype.kind != "f" or f0.size != padded16.size // 160 + 1
                or not np.isfinite(f0).all() or not ((f0 >= 0) & (f0 <= 1100)).all()):
            raise ValueError("bounded context features")
        voiced = np.flatnonzero(f0 > 0)
        if not voiced.size: raise ValueError("unvoiced source")
        pitchf = np.interp(np.arange(f0.size), voiced, f0[voiced]).astype(np.float32)
        mel_min, mel_max = 1127 * np.log(1 + 50 / 700), 1127 * np.log(1 + 1100 / 700)
        mel = (1127 * np.log(1 + pitchf / 700) - mel_min) * 254 / (mel_max - mel_min) + 1
        pitch = np.rint(np.clip(mel, 1, 255)).astype(np.int64)
        length = min(2 * phone768.shape[0], f0.size, padded16.size // 160)
        return AlignedFeatures(np.repeat(phone768, 2, axis=0)[:length].copy(),
            pitch[:length].copy(), pitchf[:length].copy(), np.empty(0, np.float32))
    except Exception:
        raise CheckpointError("inference.context.invalid") from None


def trim_context_audio(samples, frames, expected_samples):
    try:
        if (type(frames) is not int or not 238 <= frames <= 570
                or type(expected_samples) is not int or not 15200 <= expected_samples <= 148000
                or expected_samples != frames * 400 - 80000
                or type(samples) is not np.ndarray or samples.dtype != np.float32
                or samples.shape != (frames * 400,) or not np.isfinite(samples).all()):
            raise ValueError("exact central waveform")
        return samples[40000:-40000].copy()
    except Exception:
        raise CheckpointError("inference.context.invalid") from None


def _infer(model, aligned):
    import torch
    frames = int(aligned.phone.shape[0])
    torch.manual_seed(1234)  # Identical latent/NSF RNG; feature extraction cannot perturb it.
    with torch.inference_mode(), torch.device("cpu"):
        output = model.infer(torch.from_numpy(aligned.phone.copy()).unsqueeze(0),
            torch.tensor([frames], dtype=torch.int64), torch.from_numpy(aligned.pitch.copy()).unsqueeze(0),
            torch.from_numpy(aligned.pitchf.copy()).unsqueeze(0), torch.tensor([0], dtype=torch.int64))[0]
        if output.shape != (1, 1, frames * 400) or output.dtype != torch.float32 or output.device.type != "cpu":
            raise CheckpointError("inference.audio.invalid")
        return output[0, 0].numpy().copy()


def run_voice_comparison(inputs, ledger_ref, clip_id, feature_source_root, feature_asset_root, output_root, *, emit=lambda event: None):
    """Same held-out clip; saved/base share exact features, context changes padding only."""
    try:
        with ExitStack() as pins:
            root = plain_path(Path(output_root))
            pin_chain(pins, root)
            if any(root.iterdir()): raise ValueError("fresh owned output")
            ledger, _ = load_ledger(pins, ledger_ref)
            if ledger["admission_sha256"] != inputs.admission.sha256: raise ValueError("same admitted source")
            admitted = pins.enter_context(verify_admitted_training(inputs))
            require_validation_clip(clip_id, admitted.train_ids, admitted.decision["split"]["validation_clip_ids"])
            from .update_checkpoints import load_update_checkpoint
            selected = ledger["selected_point"]
            checkpoint = load_update_checkpoint(Path(selected["checkpoint_root"]), selected["checkpoint_manifest_sha256"],
                admitted.run_identity(ledger["run_id"]), admitted.train_ids, inputs.admission.sha256, TRAINING_PRESET["sampler_seed"])
            if checkpoint.global_step != selected["global_step"]: raise ValueError("known selected cursor")
            shard = next(s for s in admitted.corpus["shards"] if clip_id in s["selected_clip_ids"])
            ref = next(r for r in admitted.preview_refs if str(r.root) == shard["root"] and r.sha256 == shard["manifest_sha256"])
            selection = pins.enter_context(load_prepared_clips(inputs.dataset_root, inputs.dataset_sha256, shard["selected_clip_ids"]))
            stored = pins.enter_context(load_persisted_preview(ref, selection))
            aligned = next(c.aligned for c in stored.clips if c.id == clip_id)
            prepared = next(c for c in selection.clips if c.id == clip_id)
            expected_samples = int(aligned.wave.size)
            emit({"phase": "comparison_inputs_verified", "training_updates": checkpoint.global_step,
                "seconds": expected_samples / 40000})
            model = _generator(inputs.source_root, checkpoint)
            saved_direct = _infer(model, aligned)
            emit({"phase": "comparison_saved_direct_complete"})
            padded = prepare_context_audio(prepared.audio16)
            with create_feature_models(feature_source_root, feature_asset_root) as feature_models:
                phone, f0 = feature_models.extract(PreparedClip(clip_id, prepared.audio40, padded))
            context = align_context_features(padded, phone, f0)
            # Feature models are not needed for G inference or the baseline.
            del feature_models
            context_frames = int(context.phone.shape[0])
            saved_context = trim_context_audio(_infer(model, context), context_frames, expected_samples)
            emit({"phase": "comparison_context_complete", "additional_filter": False})
            from .rvc_kernel import BASE_GENERATOR, _model_weights, _verify_stream
            stream = pins.enter_context(pinned_file(Path(inputs.base_asset_root) / "f0G40k.pth"))
            _verify_stream(stream, BASE_GENERATOR)
            model.load_state_dict(_model_weights(stream), strict=True)
            base_direct = _infer(model, aligned)
            emit({"phase": "comparison_base_complete"})
            arrays = dict(zip(WAV_NAMES, (aligned.wave, saved_direct, saved_context, base_direct)))
            files = {}
            for name, samples in arrays.items():
                raw = encode_pcm_wav(samples, expected_samples)
                _write_bytes(root / name, raw)
                files[name] = {"bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}
            manifest = {"format": 1, "purpose": "held-out-personal-base-context-comparison", "profile": PROFILE,
                "ledger": {"root": str(ledger_ref.root), "sha256": ledger_ref.sha256}, "selected_point": selected,
                "clip_id": clip_id, "split": "validation", "frames": expected_samples // 400,
                "context_frames": context_frames, "files": files}
            raw = _raw(manifest)
            digest = hashlib.sha256(raw).hexdigest()
            _write_bytes(root / "manifest.json", raw)
            _write_bytes(root / "READY", digest.encode() + b"\n")
            return CorpusRef(root, digest)
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("inference.comparison.failed") from None


def accept_voice_comparison(control, output_root, *, request, worker_exit_code, worker_timed_out, deadline_ms):
    if type(worker_exit_code) is not int or worker_exit_code != 0 or worker_timed_out is not False or deadline_ms != 60000:
        raise CheckpointError("inference.supervisor.unaccepted")
    try:
        with ExitStack() as pins:
            root = plain_path(Path(output_root))
            pin_chain(pins, root)
            if (type(control) is not dict or set(control) != {"phase", "candidate_root", "manifest_sha256"}
                    or control["phase"] != "voice_comparison_control" or control["candidate_root"] != str(root)
                    or type(control["manifest_sha256"]) is not str
                    or re.fullmatch(r"[0-9a-f]{64}", control["manifest_sha256"]) is None
                    or {p.name for p in root.iterdir()} != {*WAV_NAMES, "manifest.json", "READY"}):
                raise ValueError("returned comparison inventory")
            raw = pins.enter_context(pinned_file(root / "manifest.json")).read(65537)
            ready = pins.enter_context(pinned_file(root / "READY")).read(66)
            if len(raw) > 65536 or hashlib.sha256(raw).hexdigest() != control["manifest_sha256"] or ready != control["manifest_sha256"].encode() + b"\n":
                raise ValueError("returned known identity")
            manifest = json.loads(raw)
            ledger, _ = load_ledger(pins, CorpusRef(Path(request["ledger"]["root"]), request["ledger"]["sha256"]))
            if (set(manifest) != {"format", "purpose", "profile", "ledger", "selected_point", "clip_id", "split", "frames", "context_frames", "files"}
                    or type(manifest["format"]) is not int or manifest["format"] != 1
                    or manifest["purpose"] != "held-out-personal-base-context-comparison" or _raw(manifest["profile"]) != _raw(PROFILE)
                    or manifest["ledger"] != request["ledger"] or _raw(manifest["selected_point"]) != _raw(ledger["selected_point"])
                    or manifest["clip_id"] != request["clip_id"] or manifest["split"] != "validation"
                    or type(manifest["frames"]) is not int or not 38 <= manifest["frames"] <= 370
                    or type(manifest["context_frames"]) is not int or manifest["context_frames"] != manifest["frames"] + 200
                    or set(manifest["files"]) != set(WAV_NAMES)):
                raise ValueError("request-bound comparison")
            sample_count = manifest["frames"] * 400
            for name, metadata in manifest["files"].items():
                wav_raw = pins.enter_context(pinned_file(root / name)).read(320045)
                if metadata != {"bytes": len(wav_raw), "sha256": hashlib.sha256(wav_raw).hexdigest()} or len(wav_raw) != 44 + sample_count * 2:
                    raise ValueError("serialized PCM identity")
                with wave.open(io.BytesIO(wav_raw), "rb") as wav:
                    if (wav.getnchannels(), wav.getsampwidth(), wav.getframerate(), wav.getnframes(), wav.getcomptype()) != (1, 2, 40000, sample_count, "NONE"):
                        raise ValueError("PCM geometry")
                    pcm = wav.readframes(sample_count)
                    if len(pcm) != sample_count * 2: raise ValueError("truncated PCM")
                    values = np.frombuffer(pcm, dtype="<i2").astype(np.float64) / 32767
                    rms = np.sqrt(np.mean(values ** 2))
                    if not np.isfinite(rms) or rms < 1e-5: raise ValueError("silent PCM")
            receipt = {"format": 1, "purpose": "supervisor-accepted-voice-comparison", "candidate": control,
                "native_worker_exit_code": 0, "native_worker_timed_out": False, "native_worker_deadline_ms": 60000,
                "accepted_after_native_worker_exit": True, "quality_established": False, "auto_activate_voice": False}
            _write_bytes(root / "accepted-comparison.json", _raw(receipt))
            return receipt
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("inference.supervisor.invalid") from None
