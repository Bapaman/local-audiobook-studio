"""Pinned persisted features and cumulative snapshots; never training admission."""
from contextlib import ExitStack, contextmanager
from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import re
import uuid

import numpy as np

from .checkpoints import CheckpointError
from .features import AlignedFeatures, PreparedSelection, align_rvc_features, load_prepared_clips
from .feature_models import ASSET_FILES, FEATURE_PROFILE, RVC_COMMIT, SOURCE_FILES, _verify, _write_bytes
from .owned_files import pin_chain, pinned_file, plain_path


@dataclass(frozen=True)
class PreviewRef:
    root: Path
    sha256: str


@dataclass(frozen=True)
class CorpusRef:
    root: Path
    sha256: str


@dataclass(frozen=True)
class PersistedClip:
    id: str
    phone: np.ndarray
    f0: np.ndarray
    aligned: AlignedFeatures


@dataclass(frozen=True)
class PersistedPreview:
    manifest: dict
    clips: tuple


def _canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()


def _read_seal(pins, ref):
    if (type(ref) not in (PreviewRef, CorpusRef) or type(ref.sha256) is not str
            or re.fullmatch(r"[0-9a-f]{64}", ref.sha256) is None):
        raise ValueError("known reference")
    root = plain_path(Path(ref.root))
    pin_chain(pins, root)
    stream = pins.enter_context(pinned_file(root / "manifest.json"))
    raw = stream.read(2 * 1024 ** 2 + 1)
    if len(raw) > 2 * 1024 ** 2 or hashlib.sha256(raw).hexdigest() != ref.sha256:
        raise ValueError("manifest identity")
    ready = pins.enter_context(pinned_file(root / "READY"))
    if ready.read(66) != ref.sha256.encode() + b"\n":
        raise ValueError("publication")
    return root, json.loads(raw)


def _preview_ids(manifest):
    ids = manifest["selected_clip_ids"]
    if (type(ids) is not list or not 1 <= len(ids) <= 3 or len(set(ids)) != len(ids)
            or any(type(i) is not str or re.fullmatch(r"clip-[0-9]{6}", i) is None for i in ids)):
        raise ValueError("selected clips")
    return ids


def _read_preview(pins, ref, selection):
    root, manifest = _read_seal(pins, ref)
    ids = _preview_ids(manifest)
    if (type(selection) is not PreparedSelection or ids != [c.id for c in selection.clips]
            or set(manifest) != {"format", "purpose", "profile", "input", "selected_clip_ids",
                "full_dataset_extracted", "training_admitted", "rvc_commit", "source_pins",
                "asset_pins", "clips", "files"}
            or type(manifest["format"]) is not int or manifest["format"] != 1
            or manifest["purpose"] != "cpu-feature-preview" or manifest["profile"] != FEATURE_PROFILE
            or manifest["input"] != {"manifest_sha256": selection.manifest_sha256,
                "dataset_id": selection.dataset_id, "dataset_clip_count": selection.dataset_clip_count,
                "training_eligible": selection.training_eligible}
            or manifest["training_admitted"] is not False
            or manifest["full_dataset_extracted"] is not (len(ids) == selection.dataset_clip_count)
            or manifest["rvc_commit"] != RVC_COMMIT
            or _canonical(manifest["source_pins"]) != _canonical(SOURCE_FILES)
            or _canonical(manifest["asset_pins"]) != _canonical(ASSET_FILES)
            or type(manifest["clips"]) is not list or len(manifest["clips"]) != len(ids)
            or {p.name for p in root.iterdir()} != {*ids, "manifest.json", "READY"}):
        raise ValueError("preview contract")
    names = ("hubert", "f0", "phone", "pitch", "pitchf", "wave")
    files = manifest["files"]
    if type(files) is not dict or set(files) != {f"{i}/{name}.npy" for i in ids for name in names}:
        raise ValueError("feature inventory")
    loaded = []
    for prepared, info in zip(selection.clips, manifest["clips"]):
        directory = root / prepared.id
        pin_chain(pins, directory)
        if {p.name for p in directory.iterdir()} != {name + ".npy" for name in names}:
            raise ValueError("feature inventory")
        arrays = {}
        n16 = prepared.audio16.size
        h, f = (n16 - 400) // 320 + 1, n16 // 160 + 1
        length = min(2 * h, f, prepared.audio40.size // 400)
        shapes = {"hubert": [h, 768], "f0": [f], "phone": [length, 768],
            "pitch": [length], "pitchf": [length], "wave": [length * 400]}
        for name in names:
            metadata = files[f"{prepared.id}/{name}.npy"]
            if (type(metadata) is not dict or set(metadata) != {"bytes", "sha256", "shape", "dtype"}
                    or type(metadata["bytes"]) is not int or not 1 <= metadata["bytes"] <= 2 * 1024 ** 2
                    or type(metadata["sha256"]) is not str
                    or re.fullmatch(r"[0-9a-f]{64}", metadata["sha256"]) is None
                    or type(metadata["shape"]) is not list
                    or any(type(n) is not int for n in metadata["shape"])
                    or metadata["shape"] != shapes[name]
                    or metadata["dtype"] not in (("float32", "float64") if name == "f0"
                        else ("int64",) if name == "pitch" else ("float32",))):
                raise ValueError("array metadata")
            stream = pins.enter_context(pinned_file(directory / (name + ".npy")))
            _verify(stream, (metadata["bytes"], metadata["sha256"]))
            # Validate bounded header geometry BEFORE NumPy can allocate from a
            # self-consistent malicious shape. Pickle/object arrays never load.
            npy_version = np.lib.format.read_magic(stream)
            if npy_version == (1, 0):
                shape, fortran, dtype = np.lib.format.read_array_header_1_0(stream, max_header_size=10000)
            elif npy_version == (2, 0):
                shape, fortran, dtype = np.lib.format.read_array_header_2_0(stream, max_header_size=10000)
            else:
                raise ValueError("NPY version")
            if (list(shape) != shapes[name] or fortran is not False or str(dtype) != metadata["dtype"]
                    or dtype.hasobject or metadata["bytes"] != stream.tell() + int(np.prod(shape)) * dtype.itemsize):
                raise ValueError("bounded NPY header")
            stream.seek(0)
            array = np.load(stream, allow_pickle=False, max_header_size=10000)
            if (type(array) is not np.ndarray or str(array.dtype) != metadata["dtype"]
                    or list(array.shape) != metadata["shape"] or not array.flags.c_contiguous
                    or not np.isfinite(array).all() or stream.read(1) != b""):
                raise ValueError("array contract")
            array.flags.writeable = False
            arrays[name] = array
        aligned = align_rvc_features(prepared.audio40, arrays["hubert"], arrays["f0"])
        for name in ("phone", "pitch", "pitchf", "wave"):
            observed, expected = arrays[name], getattr(aligned, name)
            if observed.dtype != expected.dtype or not np.array_equal(observed, expected):
                raise ValueError("persisted native alignment")
            expected.flags.writeable = False
        expected_info = {"id": prepared.id, "source_samples40k": int(prepared.audio40.size),
            "source_samples16k": int(prepared.audio16.size), "hubert_frames": arrays["hubert"].shape[0],
            "f0_frames": int(arrays["f0"].size), "aligned_frames": int(aligned.pitch.size),
            "voiced_f0_frames": int(np.count_nonzero(arrays["f0"] > 0))}
        if type(info) is not dict or info != expected_info:
            raise ValueError("clip evidence")
        loaded.append(PersistedClip(prepared.id, arrays["hubert"], arrays["f0"], aligned))
    return PersistedPreview(manifest, tuple(loaded))


@contextmanager
def load_persisted_preview(ref, selection):
    """Caller retains verified prepared selection; all feature pins stay alive."""
    with ExitStack() as pins:
        try:
            result = _read_preview(pins, ref, selection)
        except CheckpointError:
            raise
        except Exception:
            raise CheckpointError("features.persisted.invalid") from None
        yield result


def _verify_snapshot(directory, raw):
    with ExitStack() as pins:
        pin_chain(pins, directory)
        if {p.name for p in directory.iterdir()} != {"manifest.json", "READY"}:
            raise ValueError("snapshot inventory")
        for name, expected in (("manifest.json", raw), ("READY", hashlib.sha256(raw).hexdigest().encode() + b"\n")):
            stream = pins.enter_context(pinned_file(directory / name))
            if stream.read(len(expected) + 1) != expected:
                raise ValueError("snapshot publication")


def _read_previous(pins, previous, input_identity):
    root, manifest = _read_seal(pins, previous)
    if ({p.name for p in root.iterdir()} != {"manifest.json", "READY"}
            or manifest["format"] != 1 or manifest["purpose"] != "verified-feature-corpus"
            or manifest["input"] != input_identity or manifest["training_admitted"] is not False
            or manifest["profile"] != FEATURE_PROFILE or type(manifest["shards"]) is not list
            or not 1 <= len(manifest["shards"]) <= input_identity["dataset_clip_count"]):
        raise ValueError("previous corpus contract")
    refs = []
    for shard in manifest["shards"]:
        if type(shard) is not dict or set(shard) != {"root", "manifest_sha256", "selected_clip_ids"}:
            raise ValueError("previous reference")
        refs.append(PreviewRef(Path(shard["root"]), shard["manifest_sha256"]))
    return manifest, refs


def publish_corpus_snapshot(dataset_root, manifest_sha256, refs, output_root, *, previous=None):
    """Explicit known refs only. Old snapshots/data unchanged, no ML or admission.

    Snapshot READY does not imply full corpus. Pending IDs are never excluded or
    relabeled processed. Retain source and all feature handles until publication.
    """
    try:
        with ExitStack() as pins:
            root = plain_path(Path(output_root))
            pin_chain(pins, root)
            if any(root.iterdir()) or type(refs) not in (list, tuple):
                raise ValueError("fresh output or references")
            source = pins.enter_context(load_prepared_clips(dataset_root, manifest_sha256, ["clip-000000"]))
            identity = {"manifest_sha256": source.manifest_sha256, "dataset_id": source.dataset_id,
                "dataset_clip_count": source.dataset_clip_count, "training_eligible": source.training_eligible}
            prior, old_refs = (None, []) if previous is None else _read_previous(pins, previous, identity)
            all_refs = [*old_refs, *refs]
            if not 1 <= len(all_refs) <= source.dataset_clip_count or any(type(r) is not PreviewRef for r in all_refs):
                raise ValueError("bounded references")
            seen, shards, frames, prior_frames = set(), [], 0, 0
            for ordinal, ref in enumerate(all_refs):
                _, initial = _read_seal(pins, ref)
                ids = _preview_ids(initial)
                if seen.intersection(ids):
                    raise ValueError("duplicate corpus clips")
                with load_prepared_clips(dataset_root, manifest_sha256, ids) as selection:
                    stored = _read_preview(pins, ref, selection)
                    frames += sum(c.aligned.pitch.size for c in stored.clips)
                    if ordinal < len(old_refs):
                        prior_frames = frames
                    # Pins survive, large NumPy arrays need not survive the shard.
                    del stored
                shards.append({"root": str(plain_path(Path(ref.root))), "manifest_sha256": ref.sha256,
                    "selected_clip_ids": ids})
                seen.update(ids)
            ordered = [f"clip-{i:06d}" for i in range(source.dataset_clip_count)]
            if not seen <= set(ordered):
                raise ValueError("unknown corpus clips")
            accepted = [i for i in ordered if i in seen]
            result = {"format": 1, "purpose": "verified-feature-corpus", "profile": FEATURE_PROFILE,
                "input": identity, "shards": shards, "accepted_clip_ids": accepted,
                "pending_clip_ids": [i for i in ordered if i not in seen],
                "full_dataset_extracted": len(seen) == source.dataset_clip_count,
                "training_admitted": False, "aligned_frame_total": int(frames),
                "frame_total_method": "sum-of-aligned-frames-not-unique-source-or-speech-duration",
                "exclusions": [], "speech_duration_seconds": None, "speech_duration_verified": False}
            if prior is not None:
                # Reverify prior summary from the actual referenced array bytes,
                # not merely a self-consistent READY or claimed accepted count.
                old_ids = {i for shard in shards[:len(old_refs)] for i in shard["selected_clip_ids"]}
                expected_old = {**result, "shards": shards[:len(old_refs)],
                    "accepted_clip_ids": [i for i in ordered if i in old_ids],
                    "pending_clip_ids": [i for i in ordered if i not in old_ids],
                    "full_dataset_extracted": len(old_ids) == source.dataset_clip_count}
                expected_old["aligned_frame_total"] = int(prior_frames)
                if prior != expected_old:
                    raise ValueError("previous summary")
            raw = _canonical(result)
            stage = root / ("stage-" + uuid.uuid4().hex)
            stage.mkdir()
            with ExitStack() as stage_pins:
                pin_chain(stage_pins, stage)
                _write_bytes(stage / "manifest.json", raw)
                _write_bytes(stage / "READY", hashlib.sha256(raw).hexdigest().encode() + b"\n")
                _verify_snapshot(stage, raw)
            stage.rename(root / "corpus")
            _verify_snapshot(root / "corpus", raw)
            return result
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("features.corpus.invalid") from None
