"""Explicit complete-update resume, separate from legacy whole-epoch stores."""
from contextlib import ExitStack, contextmanager
from dataclasses import asdict, dataclass
import hashlib
import os
from pathlib import Path
import random
import re
import uuid

from .checkpoints import (CheckpointError, RunIdentity, PAYLOAD_NAMES, MAX_MANIFEST,
    _PinnedStream, _digest, _json, _load_json, _write_new)
from .owned_files import pin_chain, pinned_file, plain_path
from .training_state import _validate_optimizer, _validate_scheduler


def _sha(value):
    return type(value) is str and re.fullmatch(r"[0-9a-f]{64}", value) is not None


@dataclass(frozen=True)
class UpdatePosition:
    completed_epoch: int
    next_ordinal: int
    epoch_size: int
    global_step: int
    sampler_seed: int
    train_ids_sha256: str
    order_sha256: str
    admission_sha256: str

    def __post_init__(self):
        if (type(self.completed_epoch) is not int or not 0 <= self.completed_epoch <= 200
                or type(self.epoch_size) is not int or not 1 <= self.epoch_size <= 2048
                or type(self.next_ordinal) is not int or not 0 <= self.next_ordinal < self.epoch_size
                or (self.completed_epoch == 200 and self.next_ordinal != 0)
                or type(self.global_step) is not int or self.global_step < 1
                or self.global_step != self.completed_epoch * self.epoch_size + self.next_ordinal
                or type(self.sampler_seed) is not int or not 0 <= self.sampler_seed <= 2 ** 32 - 1
                or not all(_sha(v) for v in (self.train_ids_sha256, self.order_sha256, self.admission_sha256))):
            raise CheckpointError("training.update.position.invalid")


def make_update_position(completed_epoch, next_ordinal, train_ids, sampler_seed, admission_sha256):
    try:
        if (type(train_ids) not in (list, tuple) or not 1 <= len(train_ids) <= 2048
                or any(type(i) is not str or re.fullmatch(r"clip-[0-9]{6}", i) is None for i in train_ids)
                or list(train_ids) != sorted(set(train_ids))
                or type(completed_epoch) is not int or not 0 <= completed_epoch <= 200
                or type(sampler_seed) is not int or not 0 <= sampler_seed <= 2 ** 32 - 1):
            raise ValueError("bound order")
        order = list(train_ids)
        random.Random(sampler_seed + completed_epoch).shuffle(order)
        return UpdatePosition(completed_epoch, next_ordinal, len(train_ids),
            completed_epoch * len(train_ids) + next_ordinal, sampler_seed,
            hashlib.sha256(_json(list(train_ids))).hexdigest(), hashlib.sha256(_json(order)).hexdigest(),
            admission_sha256)
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("training.update.position.invalid") from None


@dataclass(frozen=True)
class UpdateCheckpoint:
    directory: Path
    identity: RunIdentity
    position: UpdatePosition
    manifest_sha256: str
    files: dict
    train_ids: tuple

    @property
    def completed_epoch(self):
        return self.position.completed_epoch

    @property
    def global_step(self):
        return self.position.global_step

    @property
    def next_epoch(self):
        return self.completed_epoch + 1

    def _validate_update_payloads(self, generator, discriminator, training):
        # Core TrainingState already checked finite CPU trees, complete AdamW
        # schemas, scalar integer steps, scheduler and RNG structure. V2 adds
        # equality with the bound seed and complete-update position on RESTORE.
        if training["sampler_seed"] != self.position.sampler_seed:
            raise CheckpointError("training.update.checkpoint.invalid")
        for payload in (generator, discriminator):
            states = payload["optimizer"]["state"].values()
            if max(float(item["step"]) for item in states) != self.global_step:
                raise CheckpointError("training.update.checkpoint.invalid")

    def open_payload(self, name):
        if name not in PAYLOAD_NAMES:
            raise CheckpointError("training.update.checkpoint.invalid")
        stack = ExitStack()
        try:
            streams = stack.enter_context(self.open_payloads())
            stream = streams[name]
            return _PinnedStream(stream, stack)
        except Exception:
            stack.close()
            raise CheckpointError("training.update.checkpoint.invalid") from None

    @contextmanager
    def open_payloads(self):
        """Verify ALL payloads once, retain their exact handles until decode ends."""
        with ExitStack() as pins:
            try:
                streams = {}
                current = _validate(pins, self.directory, self.manifest_sha256, self.identity,
                    self.train_ids, self.position.admission_sha256, self.position.sampler_seed,
                    payload_streams=streams)
                if current.position != self.position or current.files != self.files:
                    raise ValueError("changed point")
                if os.name != "nt":
                    # POSIX read handles cannot deny in-place writes. Snapshot
                    # each verified stream and check the copied bytes again.
                    import io
                    snapshots = {}
                    for name, stream in streams.items():
                        raw = stream.read(self.files[name]["bytes"] + 1)
                        if len(raw) != self.files[name]["bytes"] or hashlib.sha256(raw).hexdigest() != self.files[name]["sha256"]:
                            raise ValueError("changed payload")
                        snapshots[name] = pins.enter_context(io.BytesIO(raw))
                    streams = snapshots
            except Exception:
                raise CheckpointError("training.update.checkpoint.invalid") from None
            yield streams


def _validate(pins, root, manifest_sha256, identity, train_ids, admission_sha256, sampler_seed, *, payload_streams=None):
    root = plain_path(Path(root))
    pin_chain(pins, root)
    if type(identity) is not RunIdentity or identity.batch_size != 1 or not _sha(manifest_sha256):
        raise ValueError("known checkpoint identity")
    if {p.name for p in root.iterdir()} != {*PAYLOAD_NAMES, "manifest.json", "READY"}:
        raise ValueError("checkpoint inventory")
    manifest_stream = pins.enter_context(pinned_file(root / "manifest.json"))
    raw = manifest_stream.read(MAX_MANIFEST + 1)
    if len(raw) > MAX_MANIFEST or hashlib.sha256(raw).hexdigest() != manifest_sha256:
        raise ValueError("known manifest digest")
    ready = pins.enter_context(pinned_file(root / "READY"))
    if ready.read(66) != manifest_sha256.encode() + b"\n":
        raise ValueError("unpublished")
    manifest = _load_json(raw)
    if (type(manifest) is not dict
            or set(manifest) != {"format", "purpose", "identity", "position", "files"}
            or type(manifest["format"]) is not int or manifest["format"] != 2
            or manifest["purpose"] != "complete-update-checkpoint"
            or manifest["identity"] != asdict(identity)):
        raise ValueError("format or foreign run")
    position = UpdatePosition(**manifest["position"])
    expected = make_update_position(position.completed_epoch, position.next_ordinal,
        train_ids, sampler_seed, admission_sha256)
    if position != expected:
        raise ValueError("cursor/order/admission")
    files = manifest["files"]
    if type(files) is not dict or set(files) != set(PAYLOAD_NAMES):
        raise ValueError("payload inventory")
    for name in PAYLOAD_NAMES:
        metadata = files[name]
        if (type(metadata) is not dict or set(metadata) != {"bytes", "sha256"}
                or type(metadata["bytes"]) is not int or not 1 <= metadata["bytes"] <= 2 * 1024 ** 3
                or not _sha(metadata["sha256"])):
            raise ValueError("payload identity")
        stream = pins.enter_context(pinned_file(root / name))
        if _digest(stream) != metadata:
            raise ValueError("payload integrity")
        if payload_streams is not None:
            payload_streams[name] = stream
    return UpdateCheckpoint(root, identity, position, manifest_sha256, files, tuple(train_ids))


def load_update_checkpoint(root, manifest_sha256, identity, train_ids, admission_sha256, sampler_seed):
    """No directory discovery. Validate all bytes BEFORE restricted Torch loads."""
    try:
        with ExitStack() as pins:
            return _validate(pins, root, manifest_sha256, identity, train_ids, admission_sha256, sampler_seed)
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("training.update.checkpoint.invalid") from None


class _UpdateWriter:
    """Only publication adapter; TrainingState supplies complete unchanged payloads."""
    def __init__(self, root, identity, position, train_ids, max_point_bytes=None):
        self.root, self.identity, self.position, self.train_ids = root, identity, position, train_ids
        self.max_point_bytes, self.written_bytes = max_point_bytes, 0

    @contextmanager
    def open_payload_writer(self, path):
        with path.open("xb") as stream:
            if self.max_point_bytes is None:
                yield stream
            else:
                owner = self
                class LimitedWriter:
                    def write(self, data):
                        # Reserve the full allowed manifest plus READY before any
                        # payload bytes reach disk, including incomplete stages.
                        if owner.written_bytes + len(data) > owner.max_point_bytes - MAX_MANIFEST - 65:
                            raise CheckpointError("training.update.point_budget.exceeded")
                        written = stream.write(data)
                        owner.written_bytes += written
                        return written
                    def flush(self): return stream.flush()
                    def tell(self): return stream.tell()
                yield LimitedWriter()

    def publish(self, completed_epoch, global_step, write_payload):
        if (completed_epoch, global_step) != (self.position.completed_epoch, self.position.global_step):
            raise ValueError("payload cursor")
        stage = self.root / ("stage-" + uuid.uuid4().hex)
        stage.mkdir()
        with ExitStack() as pins:
            pin_chain(pins, stage)
            write_payload(stage)
            if {p.name for p in stage.iterdir()} != set(PAYLOAD_NAMES):
                raise ValueError("complete payloads")
            files = {}
            for name in PAYLOAD_NAMES:
                # Flush the closed writer's file, then retain a deny-write/delete
                # pin for all later hashing/publication checks.
                with (stage / name).open("r+b") as flush:
                    flush.flush()
                    os.fsync(flush.fileno())
                stream = pins.enter_context(pinned_file(stage / name))
                files[name] = _digest(stream)
                if not files[name]["bytes"]:
                    raise ValueError("empty payload")
            raw = _json({"format": 2, "purpose": "complete-update-checkpoint", "identity": asdict(self.identity),
                "position": asdict(self.position), "files": files})
            digest = hashlib.sha256(raw).hexdigest()
            if self.max_point_bytes is not None and sum(f["bytes"] for f in files.values()) + len(raw) + 65 > self.max_point_bytes:
                raise CheckpointError("training.update.point_budget.exceeded")
            _write_new(stage / "manifest.json", raw)
            _write_new(stage / "READY", digest.encode() + b"\n")
            _validate(pins, stage, digest, self.identity, self.train_ids,
                self.position.admission_sha256, self.position.sampler_seed)
        stage.rename(self.root / "checkpoint")
        with ExitStack() as pins:
            return _validate(pins, self.root / "checkpoint", digest, self.identity, self.train_ids,
                self.position.admission_sha256, self.position.sampler_seed)


def save_update_checkpoint(state, identity, position, train_ids, output_root, *, max_point_bytes=None):
    """No updates/scheduler steps here; caller completed both G/D updates first.

    Persisting an admission digest does not verify/grant admission. The future
    actual runner must supply a separately verified admitted run contract.
    """
    try:
        with ExitStack() as pins:
            root = plain_path(Path(output_root))
            pin_chain(pins, root)
            if (any(root.iterdir()) or type(identity) is not RunIdentity or identity.batch_size != 1
                    or type(position) is not UpdatePosition or max_point_bytes is not None and
                    (type(max_point_bytes) is not int or not MAX_MANIFEST + 65 < max_point_bytes <= 6 * 1024 ** 3)):
                raise ValueError("new output or types")
            expected = make_update_position(position.completed_epoch, position.next_ordinal,
                train_ids, state.sampler_seed, position.admission_sha256)
            if position != expected:
                raise ValueError("order")
            state._validate_cpu()
            for optimizer, scheduler in ((state.optimizer_g, state.scheduler_g), (state.optimizer_d, state.scheduler_d)):
                saved = optimizer.state_dict()
                _validate_optimizer(saved, optimizer, list(saved["state"]), position.global_step)
                if max(float(item["step"]) for item in saved["state"].values()) != position.global_step:
                    raise ValueError("both optimizer updates required")
                _validate_scheduler(scheduler.state_dict(), scheduler, saved, position.completed_epoch)
            return state.save(_UpdateWriter(root, identity, position, tuple(train_ids), max_point_bytes),
                position.completed_epoch, position.global_step)
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("training.update.checkpoint.invalid") from None
