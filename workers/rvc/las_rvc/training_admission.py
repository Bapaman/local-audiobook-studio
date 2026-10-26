"""Explicit experimental full-corpus/native admission; never automatic activation."""
from contextlib import ExitStack
import hashlib
from importlib.metadata import version
import json
from pathlib import Path
import re
import uuid

from .checkpoints import CheckpointError
from .experimental_intake import evaluate_experimental_source, _verify_decision
from .feature_corpus import CorpusRef, _read_previous, _read_preview
from .feature_models import FEATURE_PROFILE, _write_bytes
from .features import load_prepared_clips
from .owned_files import pin_chain, pinned_file, plain_path

SPLIT_POLICY = {"id": "rvc-source-time-holdout-v1", "version": 1,
    "holdout_source_fraction": "1/10", "boundary": "exclude-crossing-clips-from-split",
    "minimum_train_clips": 30, "minimum_validation_clips": 3,
    "coverage": "unique-source-intervals-including-context-not-speech"}
TRAINING_PRESET = {"id": "rvc-cpu-experimental-updates-v1", "version": 1,
    "rvc": "v2", "sample_rate": 40000, "f0": True, "batch_size": 1,
    "device": "cpu", "dtype": "float32", "threads": 2, "maximum_epochs": 200,
    "segment_frames": 32, "learning_rate": 0.0001, "sampler_seed": 1234,
    "checkpoint_format": "complete-update-v2", "auto_activate_voice": False}


def _raw(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()


def _union(intervals):
    total, cursor = 0, 0
    for start, end in sorted(intervals):
        total += max(0, end - max(start, cursor))
        cursor = max(cursor, end)
    return total


def make_training_split(manifest, *, opt_in):
    """Pure split over caller-verified prepared metadata; no proof from raw dict."""
    if opt_in is not True:
        raise CheckpointError("experimental.opt_in.required")
    try:
        samples, clips = manifest["source"]["decoded_samples"], manifest["clips"]
        if (type(samples) is not int or not 300 * 40000 <= samples <= 1200 * 40000
                or type(clips) is not list or not 30 <= len(clips) <= 2048):
            raise ValueError("source contract")
        cut = samples * 9 // 10
        groups = {"train": [], "validation": [], "boundary": []}
        intervals = {"train": [], "validation": []}
        for index, clip in enumerate(clips):
            start, end = clip["source_start_sample"], clip["source_end_sample"]
            if (clip["id"] != f"clip-{index:06d}" or type(start) is not int or type(end) is not int
                    or not 0 <= start < end <= samples):
                raise ValueError("unique ordered source intervals")
            group = "train" if end <= cut else "validation" if start >= cut else "boundary"
            groups[group].append(clip["id"])
            if group != "boundary":
                intervals[group].append((start, end))
        if len(groups["train"]) < 30 or len(groups["validation"]) < 3:
            raise CheckpointError("training.split.insufficient")
        if max(end for start, end in intervals["train"]) > min(start for start, end in intervals["validation"]):
            raise ValueError("source leakage")
        return {"policy": SPLIT_POLICY.copy(), "policy_sha256": hashlib.sha256(_raw(SPLIT_POLICY)).hexdigest(),
            "cut_source_sample": cut, "train_clip_ids": groups["train"],
            "validation_clip_ids": groups["validation"], "boundary_excluded_clip_ids": groups["boundary"],
            "train_unique_context_samples": _union(intervals["train"]),
            "validation_unique_context_samples": _union(intervals["validation"]),
            "coverage_method": SPLIT_POLICY["coverage"], "source_intervals_disjoint": True}
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("training.split.invalid") from None


def _read_preflight(pins, ref, expected, identity, ordered):
    if (type(ref) is not CorpusRef or type(ref.sha256) is not str
            or re.fullmatch(r"[0-9a-f]{64}", ref.sha256) is None):
        raise ValueError("known preflight")
    root = plain_path(Path(ref.root))
    pin_chain(pins, root)
    if {p.name for p in root.iterdir()} != {"decision.json", "READY"}:
        raise ValueError("preflight inventory")
    stream = pins.enter_context(pinned_file(root / "decision.json"))
    raw = stream.read(2 * 1024 ** 2 + 1)
    ready = pins.enter_context(pinned_file(root / "READY"))
    if (len(raw) > 2 * 1024 ** 2 or hashlib.sha256(raw).hexdigest() != ref.sha256
            or ready.read(66) != ref.sha256.encode() + b"\n"):
        raise ValueError("preflight identity")
    preflight = json.loads(raw)
    ids = preflight["input"]["diagnostic_clip_ids"]
    if (type(ids) is not list or not 1 <= len(ids) <= 3 or len(set(ids)) != len(ids)
            or any(i not in ordered for i in ids)):
        raise ValueError("diagnostic selection")
    expected = {**expected, "input": {"prepared_manifest_sha256": identity["manifest_sha256"],
        "dataset_id": identity["dataset_id"], "diagnostic_clip_ids": ids}, "feature_corpus_manifest_sha256": None}
    if preflight != expected or preflight["source_preflight_passed"] is not True:
        raise ValueError("preflight replay")
    return preflight


def create_experimental_training_candidate(dataset_root, manifest_sha256, preflight_ref, corpus_ref,
        source_root, base_asset_root, output_root, *, opt_in):
    """Native proof only. Even a sealed candidate NEVER grants training admission."""
    if opt_in is not True:
        raise CheckpointError("experimental.opt_in.required")
    try:
        with ExitStack() as pins:
            root = plain_path(Path(output_root))
            pin_chain(pins, root)
            if any(root.iterdir()) or type(corpus_ref) is not CorpusRef:
                raise ValueError("new output or corpus")
            source = pins.enter_context(load_prepared_clips(dataset_root, manifest_sha256, ["clip-000000"]))
            stream = pins.enter_context(pinned_file(Path(dataset_root) / "manifest.json"))
            raw_source = stream.read(2 * 1024 ** 2 + 1)
            if len(raw_source) > 2 * 1024 ** 2 or hashlib.sha256(raw_source).hexdigest() != manifest_sha256:
                raise ValueError("prepared identity")
            prepared_manifest = json.loads(raw_source)
            source_policy = evaluate_experimental_source(prepared_manifest, opt_in=True)
            split = make_training_split(prepared_manifest, opt_in=True)
            ordered = [f"clip-{i:06d}" for i in range(source.dataset_clip_count)]
            identity = {"manifest_sha256": source.manifest_sha256, "dataset_id": source.dataset_id,
                "dataset_clip_count": source.dataset_clip_count, "training_eligible": source.training_eligible}
            preflight = _read_preflight(pins, preflight_ref, source_policy, identity, ordered)
            corpus, refs = _read_previous(pins, corpus_ref, identity)
            if (corpus["full_dataset_extracted"] is not True or corpus["accepted_clip_ids"] != ordered
                    or corpus["pending_clip_ids"] != [] or corpus["exclusions"] != []):
                raise ValueError("complete accepted corpus required")

            # Only now import native CPU code. No stock device/config/CLI path.
            from .rvc_kernel import (SOURCE_MANIFEST, BASE_GENERATOR, BASE_DISCRIMINATOR,
                RVC_COMMIT, _verify_stream)
            from .feature_batch import build_native_feature_batch
            runtime = {"torch": version("torch"), "numpy": version("numpy"),
                "transformers": version("transformers"), "device": "cpu", "dtype": "float32", "threads": 2}
            if (runtime["torch"] != "2.10.0+cpu" or runtime["numpy"] != "1.26.4"
                    or runtime["transformers"] != "4.49.0"):
                raise CheckpointError("training.runtime.incompatible")
            kernel = plain_path(Path(source_root))
            assets = plain_path(Path(base_asset_root))
            pin_chain(pins, kernel)
            pin_chain(pins, assets)
            for relative, expected in SOURCE_MANIFEST.items():
                _verify_stream(pins.enter_context(pinned_file(kernel / relative)), expected)
            base_pins = {"f0G40k.pth": BASE_GENERATOR, "f0D40k.pth": BASE_DISCRIMINATOR}
            for name, expected in base_pins.items():
                _verify_stream(pins.enter_context(pinned_file(assets / name)), expected)

            seen, frames, shards, native = set(), 0, [], []
            for ref in refs:
                # IDs are still verified against the pinned prepared inventory;
                # shard summary alone cannot invent membership or array geometry.
                shard_claim = next(s for s in corpus["shards"] if s["root"] == str(ref.root)
                    and s["manifest_sha256"] == ref.sha256)
                ids = shard_claim["selected_clip_ids"]
                if seen.intersection(ids):
                    raise ValueError("duplicate corpus membership")
                with load_prepared_clips(dataset_root, manifest_sha256, ids) as selected:
                    stored = _read_preview(pins, ref, selected)
                    for prepared, clip in zip(selected.clips, stored.clips):
                        batch = build_native_feature_batch(kernel, prepared.audio40, clip.phone, clip.f0)
                        native.append({"id": clip.id, "frames": int(batch.phone_lengths[0]),
                            "phone_shape": list(batch.phone.shape), "spec_shape": list(batch.spec.shape),
                            "wave_shape": list(batch.wave.shape)})
                        frames += int(clip.aligned.pitch.size)
                        del batch
                    del stored
                seen.update(ids)
                shards.append({"root": str(plain_path(Path(ref.root))), "manifest_sha256": ref.sha256,
                    "selected_clip_ids": ids})
            expected_corpus = {"format": 1, "purpose": "verified-feature-corpus", "profile": FEATURE_PROFILE,
                "input": identity, "shards": shards, "accepted_clip_ids": ordered, "pending_clip_ids": [],
                "full_dataset_extracted": True, "training_admitted": False, "aligned_frame_total": frames,
                "frame_total_method": "sum-of-aligned-frames-not-unique-source-or-speech-duration",
                "exclusions": [], "speech_duration_seconds": None, "speech_duration_verified": False}
            if seen != set(ordered) or corpus != expected_corpus or len(native) != len(ordered):
                raise ValueError("full corpus replay")
            runtime_identity = {"runtime": runtime, "kernel_source_pins": SOURCE_MANIFEST,
                "base_model_pins": base_pins, "rvc_commit": RVC_COMMIT}
            result = {"format": 1, "purpose": "experimental-native-rvc-training-candidate",
                "experimental_opt_in": True, "training_admitted": False, "auto_activate_voice": False,
                "input": {"prepared_manifest_sha256": manifest_sha256, "dataset_id": source.dataset_id,
                    "source_preflight_sha256": preflight_ref.sha256, "corpus_manifest_sha256": corpus_ref.sha256},
                "original_preparation": preflight["original_preparation"], "warnings": preflight["warnings"],
                "source_metrics": preflight["metrics"], "split": split, "preset": TRAINING_PRESET.copy(),
                "preset_sha256": hashlib.sha256(_raw(TRAINING_PRESET)).hexdigest(),
                "runtime_identity": runtime_identity, "runtime_sha256": hashlib.sha256(_raw(runtime_identity)).hexdigest(),
                "config_sha256": SOURCE_MANIFEST["configs/v1/40k.json"][1],
                "native_validation": {"method": "real-native-cpu-batch-for-every-accepted-clip",
                    "verified_clips": len(native), "clips": native},
                "quality_established": False, "speech_duration_verified": False}
            raw = _raw(result)
            stage = root / ("stage-" + uuid.uuid4().hex)
            stage.mkdir()
            with ExitStack() as stage_pins:
                pin_chain(stage_pins, stage)
                _write_bytes(stage / "decision.json", raw)
                _write_bytes(stage / "READY", hashlib.sha256(raw).hexdigest().encode() + b"\n")
                _verify_decision(stage, raw)
            stage.rename(root / "candidate")
            _verify_decision(root / "candidate", raw)
            return result
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("training.admission.invalid") from None


def finalize_experimental_training_admission(candidate_ref, output_root, *, worker_exit_code,
        worker_timed_out, deadline_ms):
    """Metadata-only supervisor acceptance, AFTER its bounded native child exits.

    The caller must own the child and obtain the candidate digest from that
    child's control result, never by adopting an arbitrary file's current hash.
    Direct candidate generation cannot call this or authorize itself.
    """
    if (type(worker_exit_code) is not int or worker_exit_code != 0
            or worker_timed_out is not False or type(deadline_ms) is not int or deadline_ms != 60000):
        raise CheckpointError("training.supervisor.unaccepted")
    try:
        with ExitStack() as pins:
            if (type(candidate_ref) is not CorpusRef or type(candidate_ref.sha256) is not str
                    or re.fullmatch(r"[0-9a-f]{64}", candidate_ref.sha256) is None):
                raise ValueError("known candidate")
            root = plain_path(Path(output_root))
            candidate = plain_path(Path(candidate_ref.root))
            pin_chain(pins, root)
            pin_chain(pins, candidate)
            if any(root.iterdir()) or {p.name for p in candidate.iterdir()} != {"decision.json", "READY"}:
                raise ValueError("publication inventory")
            stream = pins.enter_context(pinned_file(candidate / "decision.json"))
            raw = stream.read(2 * 1024 ** 2 + 1)
            ready = pins.enter_context(pinned_file(candidate / "READY"))
            if (len(raw) > 2 * 1024 ** 2 or hashlib.sha256(raw).hexdigest() != candidate_ref.sha256
                    or ready.read(66) != candidate_ref.sha256.encode() + b"\n"):
                raise ValueError("candidate identity")
            result = json.loads(raw)
            if (result["format"] != 1 or result["purpose"] != "experimental-native-rvc-training-candidate"
                    or result["training_admitted"] is not False or result["experimental_opt_in"] is not True
                    or result["auto_activate_voice"] is not False or result["quality_established"] is not False
                    or result["speech_duration_verified"] is not False):
                raise ValueError("non-admitting candidate required")
            result = {**result, "purpose": "experimental-native-rvc-training-admission", "training_admitted": True,
                "supervisor_acceptance": {"candidate_sha256": candidate_ref.sha256,
                    "native_worker_exit_code": 0, "native_worker_timed_out": False,
                    "native_worker_deadline_ms": 60000, "accepted_after_native_worker_exit": True}}
            raw = _raw(result)
            stage = root / ("stage-" + uuid.uuid4().hex)
            stage.mkdir()
            with ExitStack() as stage_pins:
                pin_chain(stage_pins, stage)
                _write_bytes(stage / "decision.json", raw)
                _write_bytes(stage / "READY", hashlib.sha256(raw).hexdigest().encode() + b"\n")
                _verify_decision(stage, raw)
            stage.rename(root / "admission")
            _verify_decision(root / "admission", raw)
            return result
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("training.supervisor.invalid") from None
