"""Source-level review warnings; no denoising, exclusion, ML or admission."""
from contextlib import ExitStack
import hashlib
import json
from pathlib import Path

import numpy as np
from scipy import signal
from scipy.io import wavfile

from .checkpoints import CheckpointError
from .features import load_prepared_clips, _verify_file
from .feature_models import _write_bytes
from .owned_files import pin_chain, pinned_file, plain_path
from .recording_dataset import PREPARATION_PROFILE

SOURCE_QUALITY_POLICY = {"version": "source-level-review-v1", "sample_rate": 40000,
    "minimum_clip_seconds": 1.0, "quiet_source_rms_dbfs": -40.0,
    "maximum_normalization_gain_db": 12.0, "automatic_exclusion": False,
    "speech_detection": False, "grants_training_admission": False}


def assess_source_clip(source40, filtered40):
    """Warnings from BEFORE normalization; numbers do not certify clean speech."""
    try:
        for audio in (source40, filtered40):
            if (type(audio) is not np.ndarray or audio.dtype != np.float32 or audio.ndim != 1
                    or not 16000 <= audio.size <= 148000 or not np.isfinite(audio).all()):
                raise ValueError("bounded source PCM")
        if source40.shape != filtered40.shape:
            raise ValueError("same source interval")
        rms = float(np.sqrt(np.mean(source40.astype(np.float64) ** 2)))
        peak = float(np.max(np.abs(filtered40)))
        source_db = float(20 * np.log10(rms)) if rms > 0 else None
        gain_db = float(20 * np.log10(PREPARATION_PROFILE["normalization_peak"] / peak)) if peak > 0 else None
        reasons = []
        if source40.size / 40000 < SOURCE_QUALITY_POLICY["minimum_clip_seconds"]:
            reasons.append("clip.short")
        if source_db is None:
            reasons.append("clip.source_silent")
        elif source_db < SOURCE_QUALITY_POLICY["quiet_source_rms_dbfs"]:
            reasons.append("clip.source_quiet")
        if gain_db is None:
            reasons.append("clip.filtered_silent")
        elif gain_db > SOURCE_QUALITY_POLICY["maximum_normalization_gain_db"]:
            reasons.append("clip.excessive_normalization")
        return {"seconds": source40.size / 40000, "source_rms_dbfs": source_db,
            "source_peak": float(np.max(np.abs(source40))), "filtered_peak": peak,
            "normalization_gain_db": gain_db, "review_reasons": reasons,
            "requires_review": bool(reasons), "speech_verified": False}
    except Exception:
        raise CheckpointError("quality.source_pcm.invalid") from None


def publish_source_quality_audit(dataset_root, manifest_sha256, output_root):
    """Read exact immutable source/clip inventory; publish a separate report only."""
    try:
        with ExitStack() as pins:
            root = plain_path(Path(output_root))
            pin_chain(pins, root)
            if any(root.iterdir()):
                raise ValueError("fresh report root")
            dataset = plain_path(Path(dataset_root))
            # This verifies all source/clip identities and retains their Windows
            # read pins, not just clip000000, throughout the entire audit.
            pins.enter_context(load_prepared_clips(dataset, manifest_sha256, ["clip-000000"]))
            manifest_stream = pins.enter_context(pinned_file(dataset / "manifest.json"))
            raw = manifest_stream.read(2 * 1024**2 + 1)
            if hashlib.sha256(raw).hexdigest() != manifest_sha256:
                raise ValueError("known manifest")
            manifest = json.loads(raw)
            decoded_stream = pins.enter_context(pinned_file(dataset / "decoded40k.wav"))
            _verify_file(decoded_stream, manifest["files"]["decoded40k.wav"], 200 * 1024**2)
            rate, decoded = wavfile.read(decoded_stream)
            if (rate != 40000 or decoded.dtype != np.float32 or decoded.ndim != 1
                    or decoded.size != manifest["source"]["decoded_samples"]
                    or not np.isfinite(decoded).all()
                    or hashlib.sha256(decoded.tobytes()).hexdigest() != manifest["source"]["pcm_sha256"]):
                raise ValueError("decoded source binding")
            b, a = signal.butter(PREPARATION_PROFILE["highpass_order"],
                PREPARATION_PROFILE["highpass_hz"], btype="high", fs=40000)
            filtered = signal.lfilter(b, a, decoded).astype(np.float32)
            clips = []
            for clip in manifest["clips"]:
                start, end = clip["source_start_sample"], clip["source_end_sample"]
                source40, filtered40 = decoded[start:end], filtered[start:end]
                metrics = assess_source_clip(source40, filtered40)
                stream = pins.enter_context(pinned_file(dataset / clip["wave40k"]))
                observed_rate, prepared = wavfile.read(stream)
                if metrics["filtered_peak"] <= 0:
                    raise ValueError("published clip cannot be silent")
                expected = (filtered40 * (PREPARATION_PROFILE["normalization_peak"] / metrics["filtered_peak"])).astype(np.float32)
                if observed_rate != 40000 or not np.array_equal(prepared, expected):
                    raise ValueError("source preparation mismatch")
                clips.append({"id": clip["id"], "source_start_sample": start,
                    "source_end_sample": end, **metrics})
            reasons = sorted({reason for clip in clips for reason in clip["review_reasons"]})
            result = {"format": 1, "purpose": "source-level-quality-review-only",
                "dataset_sha256": manifest_sha256, "source_sha256": manifest["source"]["source_sha256"],
                "policy": SOURCE_QUALITY_POLICY.copy(), "recording_seconds": decoded.size / 40000,
                "clip_count": len(clips), "review_clip_ids": [c["id"] for c in clips if c["requires_review"]],
                "reason_counts": {reason: sum(reason in c["review_reasons"] for c in clips) for reason in reasons},
                "all_prepared_clips_reproduced_exactly": True,
                "speech_verified": False, "training_admitted": False,
                "dataset_changed": False, "automatic_exclusion": False, "clips": clips}
            payload = json.dumps(result, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()
            digest = hashlib.sha256(payload).hexdigest()
            _write_bytes(root / "report.json", payload)
            _write_bytes(root / "READY", digest.encode() + b"\n")
            return result
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("quality.audit.failed") from None
