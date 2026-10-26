"""Atomic complete checkpoints. Integrity is not permission to import pickle."""
from contextlib import ExitStack
from dataclasses import asdict, dataclass
import hashlib
import io
import json
import os
from pathlib import Path
import re
from typing import BinaryIO, Callable
import uuid

from .owned_files import pin_chain, pinned_file, plain_path, reject_link

PAYLOAD_NAMES = ("generator.pt", "discriminator.pt", "training.pt")
POINT_NAME = r"epoch-(\d{6})(?:-[0-9a-f]{32})?"
MAX_PAYLOAD = 2 * 1024 ** 3
MAX_MANIFEST = 65536


class CheckpointError(Exception):
    def __init__(self, code: str):
        self.code = code
        super().__init__(code)


def _hash(value, length):
    return isinstance(value, str) and re.fullmatch(f"[0-9a-f]{{{length}}}", value) is not None


@dataclass(frozen=True)
class RunIdentity:
    run_id: str
    dataset_sha256: str
    runtime_sha256: str
    config_sha256: str
    rvc_commit: str
    preset_id: str
    batch_size: int
    sample_rate: int
    f0: bool

    def __post_init__(self):
        if (not _hash(self.run_id, 32)
                or not all(_hash(v, 64) for v in (self.dataset_sha256, self.runtime_sha256, self.config_sha256))
                or not _hash(self.rvc_commit, 40)
                or not isinstance(self.preset_id, str) or re.fullmatch("[a-z0-9-]{1,64}", self.preset_id) is None
                or type(self.batch_size) is not int or self.batch_size not in (1, 2, 4)
                or type(self.sample_rate) is not int or self.sample_rate != 40000
                or type(self.f0) is not bool or not self.f0):
            raise CheckpointError("training.identity.invalid")


def _json(data):
    return json.dumps(data, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def _load_json(data):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate key")
            result[key] = value
        return result
    return json.loads(data, object_pairs_hook=unique,
                      parse_constant=lambda _: (_ for _ in ()).throw(ValueError("nonfinite")))


def _write_new(path, data):
    with path.open("xb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())


def _digest(stream):
    stream.seek(0)
    digest = hashlib.sha256()
    size = 0
    while chunk := stream.read(1024 * 1024):
        size += len(chunk)
        if size > MAX_PAYLOAD:
            raise CheckpointError("training.checkpoint.invalid")
        digest.update(chunk)
    stream.seek(0)
    return {"sha256": digest.hexdigest(), "bytes": size}


@dataclass(frozen=True)
class Checkpoint:
    directory: Path
    identity: RunIdentity
    completed_epoch: int
    global_step: int
    manifest_sha256: str
    files: dict

    @property
    def next_epoch(self):
        return self.completed_epoch + 1

    def open_payload(self, name: str) -> BinaryIO:
        if name not in PAYLOAD_NAMES:
            raise CheckpointError("training.checkpoint.invalid")
        stack = ExitStack()
        try:
            # Revalidate the publication manifest too, not just a stale file hash.
            current = _validate(self.directory, self.identity)
            if current.manifest_sha256 != self.manifest_sha256:
                raise CheckpointError("training.checkpoint.invalid")
            stream = stack.enter_context(pinned_file(self.directory / name))
            if _digest(stream) != self.files[name]:
                raise CheckpointError("training.checkpoint.invalid")
            if os.name != "nt":
                # POSIX advisory locks cannot deny same-user writers like Windows
                # share modes. Materialize verified bytes for developer portability.
                data = stream.read()
                if hashlib.sha256(data).hexdigest() != self.files[name]["sha256"]:
                    raise CheckpointError("training.checkpoint.invalid")
                stack.close()
                return io.BytesIO(data)
            return _PinnedStream(stream, stack)
        except CheckpointError:
            stack.close()
            raise
        except (OSError, ValueError):
            stack.close()
            raise CheckpointError("training.checkpoint.invalid") from None


class _PinnedStream:
    def __init__(self, stream, stack):
        self._stream, self._stack = stream, stack

    def __getattr__(self, name):
        return getattr(self._stream, name)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()

    def close(self):
        self._stack.close()


def _validate(directory: Path, identity: RunIdentity) -> Checkpoint:
    try:
        with ExitStack() as pins:
            pin_chain(pins, directory)
            if {p.name for p in directory.iterdir()} != set(PAYLOAD_NAMES) | {"manifest.json", "READY"}:
                raise ValueError("unexpected files")
            with pinned_file(directory / "manifest.json") as stream:
                raw = stream.read(MAX_MANIFEST + 1)
            if len(raw) > MAX_MANIFEST:
                raise ValueError("large manifest")
            manifest_hash = hashlib.sha256(raw).hexdigest()
            with pinned_file(directory / "READY") as stream:
                if stream.read(65) != manifest_hash.encode("ascii"):
                    raise ValueError("unpublished")
            manifest = _load_json(raw)
            if (not isinstance(manifest, dict)
                    or set(manifest) != {"format", "identity", "completed_epoch", "next_epoch", "global_step", "files"}
                    or type(manifest["format"]) is not int or manifest["format"] != 1):
                raise ValueError("unknown format")
            claimed_identity = RunIdentity(**manifest["identity"])
            if claimed_identity != identity:
                raise CheckpointError("training.resume.incompatible")
            epoch, step = manifest["completed_epoch"], manifest["global_step"]
            if (type(epoch) is not int or not 1 <= epoch <= 200 or type(step) is not int or step < 1
                    or type(manifest["next_epoch"]) is not int or manifest["next_epoch"] != epoch + 1
                    or (match := re.fullmatch(POINT_NAME, directory.name)) is None
                    or int(match.group(1)) != epoch):
                raise ValueError("invalid counters")
            files = manifest["files"]
            if not isinstance(files, dict) or set(files) != set(PAYLOAD_NAMES):
                raise ValueError("invalid inventory")
            for name in PAYLOAD_NAMES:
                entry = files[name]
                if (not isinstance(entry, dict) or set(entry) != {"bytes", "sha256"}
                        or type(entry["bytes"]) is not int or not 0 < entry["bytes"] <= MAX_PAYLOAD
                        or not _hash(entry["sha256"], 64)):
                    raise ValueError("invalid hash")
                with pinned_file(directory / name) as stream:
                    if _digest(stream) != entry:
                        raise ValueError("invalid payload")
            return Checkpoint(directory, identity, epoch, step, manifest_hash, files)
    except CheckpointError:
        raise
    except (OSError, ValueError, TypeError, KeyError, RecursionError):
        raise CheckpointError("training.checkpoint.invalid") from None


class CheckpointStore:
    def __init__(self, root: Path, identity: RunIdentity):
        self.identity = identity
        self._pins = ExitStack()
        self._closed = False
        try:
            self.root = plain_path(root)
            pin_chain(self._pins, self.root)
            for name in ("checkpoints", "staging"):
                path = self.root / name
                if not path.exists():
                    path.mkdir()
                reject_link(path, True)
                pin_chain(self._pins, path)
        except (OSError, ValueError):
            self._pins.close()
            raise CheckpointError("training.path.invalid") from None

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()

    def close(self):
        self._pins.close()
        self._closed = True

    def latest(self) -> Checkpoint | None:
        if self._closed:
            raise CheckpointError("training.store.closed")
        valid = []
        try:
            entries = list((self.root / "checkpoints").iterdir())
            if len(entries) > 1024:
                raise CheckpointError("training.checkpoint.invalid")
            for path in entries:
                if re.fullmatch(POINT_NAME, path.name) is None:
                    continue
                try:
                    valid.append(_validate(path, self.identity))
                except CheckpointError as error:
                    if error.code == "training.resume.incompatible":
                        raise
            return max(valid, key=lambda x: x.completed_epoch, default=None)
        except OSError:
            raise CheckpointError("training.checkpoint.invalid") from None

    def publish(self, completed_epoch: int, global_step: int,
                write_payload: Callable[[Path], None]) -> Checkpoint:
        if self._closed:
            raise CheckpointError("training.store.closed")
        if (type(completed_epoch) is not int or not 1 <= completed_epoch <= 200
                or type(global_step) is not int or global_step < 1):
            raise CheckpointError("training.checkpoint.invalid")
        self.latest()  # reject a foreign run before creating new state
        # A corrupt logical epoch is retained for diagnostics, but must not
        # prevent replay from the previous whole checkpoint.
        for candidate in (self.root / "checkpoints").iterdir():
            match = re.fullmatch(POINT_NAME, candidate.name)
            if match is not None and int(match.group(1)) == completed_epoch:
                try:
                    _validate(candidate, self.identity)
                except CheckpointError as error:
                    if error.code == "training.resume.incompatible":
                        raise
                else:
                    raise CheckpointError("training.checkpoint.exists")
        target = self.root / "checkpoints" / f"epoch-{completed_epoch:06d}"
        if os.path.lexists(target):
            target = target.with_name(target.name + "-" + uuid.uuid4().hex)
        stage = self.root / "staging" / uuid.uuid4().hex
        files = {}
        try:
            stage.mkdir()
            with ExitStack() as pins:
                # Deny Windows staging rename/reparse substitution BEFORE the
                # callback opens any output, not just during later validation.
                pin_chain(pins, stage)
                try:
                    write_payload(stage)
                except Exception:
                    raise CheckpointError("training.checkpoint.write_failed") from None
                if {p.name for p in stage.iterdir()} != set(PAYLOAD_NAMES):
                    raise ValueError("incomplete checkpoint")
                for name in PAYLOAD_NAMES:
                    # Writer has returned and must have closed every output.
                    with (stage / name).open("r+b") as stream:
                        reject_link(stage / name, False)
                        stream.flush()
                        os.fsync(stream.fileno())
                    with pinned_file(stage / name) as stream:
                        files[name] = _digest(stream)
                    if files[name]["bytes"] == 0:
                        raise ValueError("empty payload")
                manifest = _json({"format": 1, "identity": asdict(self.identity),
                                  "completed_epoch": completed_epoch, "next_epoch": completed_epoch + 1,
                                  "global_step": global_step, "files": files})
                _write_new(stage / "manifest.json", manifest)
                _write_new(stage / "READY", hashlib.sha256(manifest).hexdigest().encode("ascii"))
            # On Windows os.rename never replaces an existing directory. The
            # random suffix allows replay without removing a corrupt old point.
            if target.exists():
                raise CheckpointError("training.checkpoint.exists")
            stage.rename(target)
            return _validate(target, self.identity)
        except CheckpointError:
            raise
        except (OSError, ValueError):
            raise CheckpointError("training.checkpoint.invalid") from None
