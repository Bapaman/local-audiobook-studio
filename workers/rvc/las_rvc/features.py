"""Verified prepared PCM inputs and native RVCv2 frame alignment, no ML imports."""
from contextlib import ExitStack, contextmanager
from dataclasses import dataclass
import hashlib
import json
import math
from pathlib import Path
import re

import numpy as np
from scipy.io import wavfile

from .checkpoints import CheckpointError
from .owned_files import pin_chain, pinned_file, plain_path
from .recording_dataset import PREPARATION_PROFILE


@dataclass(frozen=True)
class PreparedClip:
    id: str
    audio40: np.ndarray
    audio16: np.ndarray


@dataclass(frozen=True)
class PreparedSelection:
    manifest_sha256: str
    dataset_id: str
    dataset_clip_count: int
    training_eligible: bool
    clips: tuple


@dataclass(frozen=True)
class AlignedFeatures:
    phone: np.ndarray
    pitch: np.ndarray
    pitchf: np.ndarray
    wave: np.ndarray


def _read_small(stream, limit):
    stream.seek(0)
    raw = stream.read(limit + 1)
    if len(raw) > limit:
        raise ValueError("size")
    return raw


def _verify_file(stream, expected, limit):
    if (type(expected) is not dict or set(expected) != {"bytes", "sha256"}
            or type(expected["bytes"]) is not int or not 1 <= expected["bytes"] <= limit
            or type(expected["sha256"]) is not str
            or re.fullmatch(r"[0-9a-f]{64}", expected["sha256"]) is None):
        raise ValueError("file metadata")
    stream.seek(0, 2)
    if stream.tell() != expected["bytes"]:
        raise ValueError("size")
    stream.seek(0)
    sha = hashlib.sha256()
    while chunk := stream.read(1024 * 1024):
        sha.update(chunk)
    if sha.hexdigest() != expected["sha256"]:
        raise ValueError("digest")
    stream.seek(0)


@contextmanager
def load_prepared_clips(root: Path, manifest_sha256: str, clip_ids):
    """All inventory handles stay pinned until the consumer leaves this context.

    Extraction can diagnose rejected material. This does NOT grant admission to
    training, and the frozen selection carries the original admission result.
    """
    with ExitStack() as pins:
        try:
            root = plain_path(Path(root))
            pin_chain(pins, root)
            if (type(manifest_sha256) is not str
                    or re.fullmatch(r"[0-9a-f]{64}", manifest_sha256) is None
                    or type(clip_ids) not in (list, tuple) or not 1 <= len(clip_ids) <= 3
                    or any(type(i) is not str or re.fullmatch(r"clip-[0-9]{6}", i) is None for i in clip_ids)
                    or len(set(clip_ids)) != len(clip_ids)):
                raise ValueError("selection")
            manifest_stream = pins.enter_context(pinned_file(root / "manifest.json"))
            ready_stream = pins.enter_context(pinned_file(root / "READY"))
            raw = _read_small(manifest_stream, 2 * 1024 ** 2)
            if (hashlib.sha256(raw).hexdigest() != manifest_sha256
                    or _read_small(ready_stream, 65) != manifest_sha256.encode() + b"\n"):
                raise ValueError("publication")
            manifest = json.loads(raw)
            canonical_profile = json.dumps(PREPARATION_PROFILE, sort_keys=True, separators=(",", ":")).encode()
            source = manifest["source"]
            clips = manifest["clips"]
            qc = manifest["qc"]
            if (manifest["format"] != 1 or manifest["profile"] != PREPARATION_PROFILE
                    or manifest["profile_sha256"] != hashlib.sha256(canonical_profile).hexdigest()
                    or type(manifest["dataset_id"]) is not str
                    or re.fullmatch(r"[0-9a-f]{32}", manifest["dataset_id"]) is None
                    or source["consent_confirmed"] is not True or source["sample_rate"] != 40000
                    or type(source["decoded_samples"]) is not int
                    or not 16000 <= source["decoded_samples"] <= 1200 * 40000
                    or type(clips) is not list or not 1 <= len(clips) <= 2048
                    or type(manifest["usable_samples"]) is not int
                    or not 0 <= manifest["usable_samples"] <= source["decoded_samples"]
                    or manifest["duration_seconds"] != source["decoded_samples"] / 40000
                    or manifest["usable_seconds"] != manifest["usable_samples"] / 40000
                    or qc["usable_duration_method"] != "unique-covered-above-threshold-RMS-samples"
                    or qc["speech_music_other_speakers_verified"] is not False):
                raise ValueError("schema")
            clipping = qc["decoded_clipping_proxy_fraction"]
            if type(clipping) not in (int, float) or not math.isfinite(clipping) or not 0 <= clipping <= 1:
                raise ValueError("clipping")
            reasons = []
            if manifest["usable_samples"] < 300 * 40000:
                reasons.append("dataset.usable_duration.short")
            if len(clips) < 30:
                reasons.append("dataset.clips.insufficient")
            if clipping > 0.01:
                reasons.append("dataset.clipping.excessive")
            if manifest["reasons"] != reasons or manifest["eligible"] is not (not reasons):
                raise ValueError("admission")
            expected = {"master.bin", "decoded40k.wav"}
            by_id = {}
            for index, clip in enumerate(clips):
                name = f"clip-{index:06d}"
                if (clip["id"] != name or clip["wave40k"] != f"clips40k/{name}.wav"
                        or clip["wave16k"] != f"clips16k/{name}.wav"
                        or any(type(clip[key]) is not int for key in ("source_start_sample",
                            "source_end_sample", "samples40k", "samples16k"))
                        or not 0 <= clip["source_start_sample"] < clip["source_end_sample"] <= source["decoded_samples"]
                        or clip["samples40k"] != clip["source_end_sample"] - clip["source_start_sample"]
                        or not 16000 <= clip["samples40k"] <= 148000
                        or clip["samples16k"] != (clip["samples40k"] * 2 + 4) // 5):
                    raise ValueError("clip metadata")
                by_id[name] = clip
                expected.update((clip["wave40k"], clip["wave16k"]))
            files = manifest["files"]
            if type(files) is not dict or set(files) != expected or any(i not in by_id for i in clip_ids):
                raise ValueError("inventory")
            if (files["master.bin"]["sha256"] != source["source_sha256"]
                    or files["master.bin"]["bytes"] != source["source_bytes"]):
                raise ValueError("source binding")
            entries = {entry.name for entry in root.iterdir()}
            if entries != {"master.bin", "decoded40k.wav", "manifest.json", "READY", "clips40k", "clips16k"}:
                raise ValueError("inventory")
            for directory in (root / "clips40k", root / "clips16k"):
                pin_chain(pins, directory)
                observed = {f"{directory.name}/{entry.name}" for entry in directory.iterdir()}
                if observed != {key for key in expected if key.startswith(directory.name + "/")}:
                    raise ValueError("inventory")
            streams = {}
            for relative in sorted(files):
                stream = pins.enter_context(pinned_file(root / relative))
                limit = 1024 ** 3 if relative == "master.bin" else (200 * 1024 ** 2 if relative == "decoded40k.wav" else 2 * 1024 ** 2)
                _verify_file(stream, files[relative], limit)
                streams[relative] = stream
            selected = []
            for name in clip_ids:
                clip = by_id[name]
                audio = []
                for field, rate, size_field in (("wave40k", 40000, "samples40k"), ("wave16k", 16000, "samples16k")):
                    observed_rate, samples = wavfile.read(streams[clip[field]])
                    if (observed_rate != rate or samples.dtype != np.float32 or samples.ndim != 1
                            or samples.size != clip[size_field] or not samples.flags.c_contiguous
                            or not np.isfinite(samples).all() or not 0 < float(np.abs(samples).max()) <= 1.01):
                        raise ValueError("paired PCM")
                    samples.flags.writeable = False
                    audio.append(samples)
                selected.append(PreparedClip(name, *audio))
            selection = PreparedSelection(manifest_sha256, manifest["dataset_id"], len(clips), manifest["eligible"], tuple(selected))
        except Exception:
            raise CheckpointError("features.dataset.invalid") from None
        yield selection


def align_rvc_features(audio40, phone768, f0):
    """Native repeat2, interpolate unvoiced F0, coarse mel indices, no padding."""
    try:
        if (type(audio40) is not np.ndarray or audio40.dtype != np.float32 or audio40.ndim != 1
                or not 16000 <= audio40.size <= 148000 or not np.isfinite(audio40).all()
                or type(phone768) is not np.ndarray or phone768.dtype != np.float32
                or phone768.ndim != 2 or phone768.shape[1] != 768 or not np.isfinite(phone768).all()
                or type(f0) is not np.ndarray or f0.ndim != 1 or f0.dtype.kind != "f"
                or not np.isfinite(f0).all() or not bool(((f0 >= 0) & (f0 <= 1100)).all())):
            raise ValueError("array contract")
        samples16 = (audio40.size * 2 + 4) // 5
        if phone768.shape[0] != (samples16 - 400) // 320 + 1 or f0.size != samples16 // 160 + 1:
            raise ValueError("native frame count")
        voiced = np.flatnonzero(f0 > 0)
        if not voiced.size:
            raise ValueError("all unvoiced")
        pitchf = np.interp(np.arange(f0.size), voiced, f0[voiced]).astype(np.float32)
        mel_min = 1127 * np.log(1 + 50 / 700)
        mel_max = 1127 * np.log(1 + 1100 / 700)
        mel = (1127 * np.log(1 + pitchf / 700) - mel_min) * 254 / (mel_max - mel_min) + 1
        pitch = np.rint(np.clip(mel, 1, 255)).astype(np.int64)
        length = min(2 * phone768.shape[0], f0.size, audio40.size // 400)
        if not 32 <= length <= 400:
            raise ValueError("training frame count")
        return AlignedFeatures(np.repeat(phone768, 2, axis=0)[:length].copy(),
            pitch[:length].copy(), pitchf[:length].copy(), audio40[:length * 400].copy())
    except Exception:
        raise CheckpointError("features.arrays.invalid") from None
