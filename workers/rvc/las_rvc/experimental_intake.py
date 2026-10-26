"""Versioned experimental SOURCE preflight, never permission to train."""
from contextlib import ExitStack
import hashlib
import json
import math
from pathlib import Path
import re
import uuid

from .checkpoints import CheckpointError
from .features import load_prepared_clips
from .owned_files import pin_chain, pinned_file, plain_path
from .feature_models import _write_bytes

EXPERIMENTAL_SOURCE_POLICY = {
    "id": "rvc-six-minute-experimental-source-v1", "version": 1,
    "purpose": "experimental-source-preflight", "recording_min_seconds": 300,
    "recording_max_seconds": 1200, "minimum_prepared_clips": 30,
    "maximum_clipping_proxy_fraction": 0.01,
    "low_activity": "warning-only-not-verified-speech",
    "requires_explicit_opt_in": True, "grants_training_admission": False,
}


def evaluate_experimental_source(manifest, *, opt_in):
    """Pure policy over a verified manifest; caller must verify provenance first.

    This evaluator does not read files or claim verification of a raw dict.
    Native/full-corpus checks and actual training admission remain mandatory.
    """
    if opt_in is not True:
        raise CheckpointError("experimental.opt_in.required")
    try:
        samples = manifest["source"]["decoded_samples"]
        activity = manifest["usable_samples"]
        qc = manifest["qc"]
        clipping = qc["decoded_clipping_proxy_fraction"]
        clips = manifest["clips"]
        if (type(samples) is not int or not 16000 <= samples <= 1200 * 40000
                or type(activity) is not int or not 0 <= activity <= samples
                or manifest["duration_seconds"] != samples / 40000
                or manifest["usable_seconds"] != activity / 40000
                or manifest["source"]["sample_rate"] != 40000
                or manifest["source"]["consent_confirmed"] is not True
                or qc["usable_duration_method"] != "unique-covered-above-threshold-RMS-samples"
                or qc["speech_music_other_speakers_verified"] is not False
                or type(clipping) not in (int, float) or not math.isfinite(clipping) or not 0 <= clipping <= 1
                or type(clips) is not list or not 1 <= len(clips) <= 2048):
            raise ValueError("metrics")
        original_reasons = []
        if activity < 300 * 40000:
            original_reasons.append("dataset.usable_duration.short")
        if len(clips) < 30:
            original_reasons.append("dataset.clips.insufficient")
        if clipping > 0.01:
            original_reasons.append("dataset.clipping.excessive")
        if manifest["reasons"] != original_reasons or manifest["eligible"] is not (not original_reasons):
            raise ValueError("legacy decision")
        intervals = []
        for clip in clips:
            start, end = clip["source_start_sample"], clip["source_end_sample"]
            if type(start) is not int or type(end) is not int or not 0 <= start < end <= samples:
                raise ValueError("source interval")
            intervals.append((start, end))
        coverage, cursor = 0, 0
        for start, end in sorted(intervals):
            coverage += max(0, end - max(start, cursor))
            cursor = max(cursor, end)
        if activity > coverage:
            raise ValueError("activity coverage")
        hard_reasons = [reason for reason in original_reasons if reason != "dataset.usable_duration.short"]
        if not 300 * 40000 <= samples <= 1200 * 40000:
            hard_reasons.append("experimental.recording_duration.invalid")
        warnings = ["experimental.quality_not_established", "experimental.speech_unverified"]
        if "dataset.usable_duration.short" in original_reasons:
            warnings.append("experimental.low_signal_activity")
        policy_raw = json.dumps(EXPERIMENTAL_SOURCE_POLICY, sort_keys=True, separators=(",", ":")).encode()
        return {"format": 1, "purpose": "experimental-source-preflight", "experimental_opt_in": True,
            "policy": EXPERIMENTAL_SOURCE_POLICY.copy(), "policy_sha256": hashlib.sha256(policy_raw).hexdigest(),
            "source_preflight_passed": not hard_reasons, "training_admitted": False,
            "original_preparation": {"eligible": manifest["eligible"], "reasons": original_reasons},
            "hard_reasons": hard_reasons, "warnings": warnings,
            "required_next_gates": ["full_verified_feature_corpus", "native_validation", "separate_admitted_training_run"],
            "metrics": {"recording_samples": samples, "recording_seconds": samples / 40000,
                "recording_duration_method": "decoded40k-sample-count",
                "signal_activity_samples": activity, "signal_activity_seconds": activity / 40000,
                "signal_activity_method": qc["usable_duration_method"],
                "unique_clip_coverage_samples": coverage, "unique_clip_coverage_seconds": coverage / 40000,
                "coverage_duration_method": "unique-accepted-clip-source-intervals-including-context",
                "prepared_clip_count": len(clips), "decoded_clipping_proxy_fraction": clipping,
                "speech_duration_seconds": None, "speech_duration_verified": False,
                "speech_music_other_speakers_verified": False}}
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("experimental.source.invalid") from None


def _verify_decision(directory, raw):
    with ExitStack() as pins:
        pin_chain(pins, directory)
        if {entry.name for entry in directory.iterdir()} != {"decision.json", "READY"}:
            raise ValueError("inventory")
        digest = hashlib.sha256(raw).hexdigest()
        for name, expected in (("decision.json", raw), ("READY", digest.encode() + b"\n")):
            stream = pins.enter_context(pinned_file(directory / name))
            if stream.read(len(expected) + 1) != expected:
                raise ValueError("publication")


def create_experimental_source_preflight(dataset_root, manifest_sha256, clip_ids, output_root, *, opt_in):
    if opt_in is not True:
        raise CheckpointError("experimental.opt_in.required")
    try:
        with ExitStack() as pins:
            root = plain_path(Path(output_root))
            pin_chain(pins, root)
            if any(root.iterdir()):
                raise ValueError("output not empty")
            with load_prepared_clips(dataset_root, manifest_sha256, clip_ids) as selection:
                stream = pins.enter_context(pinned_file(Path(dataset_root) / "manifest.json"))
                raw_input = stream.read(2 * 1024 ** 2 + 1)
                if len(raw_input) > 2 * 1024 ** 2 or hashlib.sha256(raw_input).hexdigest() != manifest_sha256:
                    raise ValueError("identity")
                result = evaluate_experimental_source(json.loads(raw_input), opt_in=True)
                result["input"] = {"prepared_manifest_sha256": selection.manifest_sha256,
                    "dataset_id": selection.dataset_id, "diagnostic_clip_ids": [clip.id for clip in selection.clips]}
                result["feature_corpus_manifest_sha256"] = None
                raw = json.dumps(result, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()
                stage = root / ("stage-" + uuid.uuid4().hex)
                stage.mkdir()
                with ExitStack() as stage_pins:
                    pin_chain(stage_pins, stage)
                    _write_bytes(stage / "decision.json", raw)
                    _write_bytes(stage / "READY", hashlib.sha256(raw).hexdigest().encode() + b"\n")
                    _verify_decision(stage, raw)
                stage.rename(root / "preflight")
                _verify_decision(root / "preflight", raw)
                return result
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("experimental.preflight.failed") from None
