"""Immutable, caller-known selection ledger. No process launch or file retirement."""
from contextlib import ExitStack
from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import re
import shutil

from .checkpoints import CheckpointError
from .feature_corpus import CorpusRef
from .feature_models import _write_bytes
from .owned_files import pin_chain, pinned_file, plain_path
from .training_admission import _raw

SLOT_BYTES = 3 * 1024 ** 3
MAX_SLOTS = 2
FREE_GUARD_BYTES = 8 * 1024 ** 3


@dataclass(frozen=True)
class ReceiptRef:
    path: Path
    sha256: str


def _digest(value):
    if type(value) is not str or re.fullmatch(r"[0-9a-f]{64}", value) is None:
        raise ValueError("known digest required")
    return value


def _read_json(pins, path, digest):
    stream = pins.enter_context(pinned_file(plain_path(Path(path))))
    raw = stream.read(65537)
    if len(raw) > 65536 or hashlib.sha256(raw).hexdigest() != _digest(digest):
        raise ValueError("known metadata identity")
    return json.loads(raw)


def _ref(value, key):
    if type(value) is not dict or set(value) != {key, "sha256"}:
        raise ValueError("explicit reference")
    return plain_path(Path(value[key])), _digest(value["sha256"])


def _point(point, run_id, admission_sha256):
    base = {"phase", "checkpoint_root", "checkpoint_manifest_sha256", "run_id", "admission_sha256",
        "global_step", "completed_epoch", "next_ordinal", "epoch_size"}
    if type(point) is not dict or set(point) not in (base, base | {"start_global_step", "completed_updates"}):
        raise ValueError("point schema")
    plain_path(Path(point["checkpoint_root"]))
    _digest(point["checkpoint_manifest_sha256"])
    if (point["phase"] != "personal_checkpoint_control" or point["run_id"] != run_id
            or point["admission_sha256"] != admission_sha256
            or type(point["global_step"]) is not int or point["global_step"] < 1
            or type(point["epoch_size"]) is not int or not 1 <= point["epoch_size"] <= 2048
            or type(point["completed_epoch"]) is not int or not 0 <= point["completed_epoch"] <= 200
            or type(point["next_ordinal"]) is not int or not 0 <= point["next_ordinal"] < point["epoch_size"]
            or point["global_step"] != point["completed_epoch"] * point["epoch_size"] + point["next_ordinal"]
            or point["completed_epoch"] == 200 and point["next_ordinal"] != 0):
        raise ValueError("point cursor identity")
    if "completed_updates" in point and (type(point["completed_updates"]) is not int
            or not 1 <= point["completed_updates"] <= 2 or type(point["start_global_step"]) is not int
            or point["start_global_step"] < 0
            or point["start_global_step"] + point["completed_updates"] != point["global_step"]):
        raise ValueError("point actual delta")
    return point


def _receipt(pins, ref, run_id, admission_sha256):
    if type(ref) is not ReceiptRef:
        raise ValueError("known receipt reference")
    value = _read_json(pins, ref.path, ref.sha256)
    if type(value["format"]) is not int or value["format"] not in (1, 2):
        raise ValueError("receipt format")
    expected = {"format": value["format"], "purpose": "supervisor-accepted-personal-native-update", "point": value["point"],
        "native_worker_exit_code": 0, "native_worker_timed_out": False, "native_worker_deadline_ms": 60000,
        "accepted_after_native_worker_exit": True, "quality_established": False, "auto_activate_voice": False}
    if value["format"] == 2:
        _digest(value["request_sha256"])
        if value["previous_checkpoint"] is not None:
            _ref(value["previous_checkpoint"], "root")
        expected.update(request_sha256=value["request_sha256"], previous_checkpoint=value["previous_checkpoint"])
    # Canonical bytes also distinguish bool/int at the supervisor boundary.
    if _raw(value) != _raw(expected):
        raise ValueError("timely supervisor receipt")
    _point(value["point"], run_id, admission_sha256)
    return value


def _receipt_dict(ref):
    return {"path": str(ref.path), "sha256": ref.sha256}


def _protected(values, owned, selected):
    if type(values) is not list or len(values) != 2:
        raise ValueError("two protected proof points required")
    refs = [_ref(v, "root") for v in values]
    if len({root for root, _ in refs}) != 2 or any(root.is_relative_to(owned) or owned.is_relative_to(root) for root, _ in refs):
        raise ValueError("protected proofs outside candidate ownership")
    if (Path(selected["checkpoint_root"]), selected["checkpoint_manifest_sha256"]) not in refs:
        raise ValueError("initial selected proof not protected")


def _load(pins, ref):
    if type(ref) is not CorpusRef:
        raise ValueError("known predecessor ledger")
    root = plain_path(Path(ref.root))
    pin_chain(pins, root)
    if {p.name for p in root.iterdir()} != {"ledger.json", "READY"}:
        raise ValueError("ledger inventory")
    value = _read_json(pins, root / "ledger.json", ref.sha256)
    ready = pins.enter_context(pinned_file(root / "READY"))
    if ready.read(66) != ref.sha256.encode() + b"\n":
        raise ValueError("ledger commit marker")
    expected_keys = {"format", "purpose", "owned_root", "version", "previous_sha256", "run_id", "admission_sha256",
        "initial_receipt", "protected_points", "selected_point", "windows", "reserved_bytes", "max_slots", "budget_bytes"}
    if (type(value) is not dict or set(value) != expected_keys or type(value["format"]) is not int or value["format"] != 1
            or value["purpose"] != "bounded-personal-training-continuation"
            or type(value["version"]) is not int or not 0 <= value["version"] <= 4
            or type(value["run_id"]) is not str or re.fullmatch(r"[0-9a-f]{32}", value["run_id"]) is None
            or type(value["max_slots"]) is not int or value["max_slots"] != MAX_SLOTS
            or type(value["budget_bytes"]) is not int or value["budget_bytes"] != SLOT_BYTES * MAX_SLOTS):
        raise ValueError("ledger schema")
    _digest(value["admission_sha256"])
    if value["version"] == 0:
        if value["previous_sha256"] is not None: raise ValueError("initial predecessor")
    else:
        _digest(value["previous_sha256"])
    owned = plain_path(Path(value["owned_root"]))
    pin_chain(pins, owned)
    if root != owned / f"ledger-{value['version']:04d}":
        raise ValueError("owned ledger version")
    path, sha = _ref(value["initial_receipt"], "path")
    selected = _receipt(pins, ReceiptRef(path, sha), value["run_id"], value["admission_sha256"])["point"]
    _protected(value["protected_points"], owned, selected)
    windows = value["windows"]
    if (type(windows) is not list or len(windows) > MAX_SLOTS or type(value["reserved_bytes"]) is not int
            or value["reserved_bytes"] != len(windows) * SLOT_BYTES):
        raise ValueError("reservation budget")
    transitions = 0
    for index, window in enumerate(windows, 1):
        if (type(window) is not dict or set(window) != {"slot", "output_root", "state", "max_updates", "reserved_bytes",
                "predecessor", "receipt"} or type(window["slot"]) is not int or window["slot"] != index
                or window["output_root"] != str(owned / f"slot-{index:02d}")
                or type(window["max_updates"]) is not int or window["max_updates"] != 2
                or type(window["reserved_bytes"]) is not int or window["reserved_bytes"] != SLOT_BYTES
                or _raw(window["predecessor"]) != _raw(selected)):
            raise ValueError("reserved predecessor identity")
        transitions += 1
        if window["state"] == "accepted":
            path, sha = _ref(window["receipt"], "path")
            if path != Path(window["output_root"]) / "accepted-update.json":
                raise ValueError("receipt outside reserved output")
            receipt = _receipt(pins, ReceiptRef(path, sha), value["run_id"], value["admission_sha256"])
            _next_point(receipt, selected, window)
            selected = receipt["point"]
            transitions += 1
        elif window["state"] == "failed" and window["receipt"] is None:
            transitions += 1
        elif window["state"] == "reserved" and window["receipt"] is None and index == len(windows):
            pass
        else:
            raise ValueError("window state")
    if value["version"] != transitions or _raw(value["selected_point"]) != _raw(selected):
        raise ValueError("durable selected point replay")
    return value, owned


def _next_point(receipt, previous, window):
    point = receipt["point"]
    predecessor = {"root": previous["checkpoint_root"], "sha256": previous["checkpoint_manifest_sha256"]}
    if (receipt["format"] != 2 or _raw(receipt.get("previous_checkpoint")) != _raw(predecessor)
            or point["checkpoint_root"] != str(Path(window["output_root"]) / "checkpoint")
            or point.get("start_global_step") != previous["global_step"]
            or type(point.get("completed_updates")) is not int or not 1 <= point["completed_updates"] <= window["max_updates"]
            or point["epoch_size"] != previous["epoch_size"]
            or point["global_step"] - previous["global_step"] != point["completed_updates"]):
        raise ValueError("contiguous reserved update")


def _publish(pins, owned, value):
    root = owned / f"ledger-{value['version']:04d}"
    root.mkdir()  # Exclusive deterministic version: stale callers cannot fork selection.
    pin_chain(pins, root)
    raw = _raw(value)
    digest = hashlib.sha256(raw).hexdigest()
    _write_bytes(root / "ledger.json", raw)
    _write_bytes(root / "READY", digest.encode() + b"\n")
    ref = CorpusRef(root, digest)
    _load(pins, ref)
    return ref


def _guarded(action):
    try:
        with ExitStack() as pins:
            return action(pins)
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("training.continuation.invalid") from None


def create_continuation(owned_root, initial_receipt, run_id, admission_sha256, protected_points):
    def create(pins):
        owned = plain_path(Path(owned_root))
        pin_chain(pins, owned)
        if any(owned.iterdir()) or type(run_id) is not str or re.fullmatch(r"[0-9a-f]{32}", run_id) is None:
            raise ValueError("fresh owned continuation")
        _digest(admission_sha256)
        selected = _receipt(pins, initial_receipt, run_id, admission_sha256)["point"]
        if type(protected_points) not in (list, tuple) or any(type(ref) is not CorpusRef for ref in protected_points):
            raise ValueError("protected explicit points")
        protected = [{"root": str(ref.root), "sha256": ref.sha256} for ref in protected_points]
        _protected(protected, owned, selected)
        value = {"format": 1, "purpose": "bounded-personal-training-continuation", "owned_root": str(owned),
            "version": 0, "previous_sha256": None, "run_id": run_id, "admission_sha256": admission_sha256,
            "initial_receipt": _receipt_dict(initial_receipt), "protected_points": protected, "selected_point": selected,
            "windows": [], "reserved_bytes": 0, "max_slots": MAX_SLOTS, "budget_bytes": SLOT_BYTES * MAX_SLOTS}
        return _publish(pins, owned, value)
    return _guarded(create)


def reserve_window(previous):
    def reserve(pins):
        value, owned = _load(pins, previous)
        if (len(value["windows"]) >= MAX_SLOTS or any(w["state"] == "reserved" for w in value["windows"])
                or shutil.disk_usage(owned).free < FREE_GUARD_BYTES):
            raise ValueError("window budget or active reservation")
        slot = len(value["windows"]) + 1
        out = owned / f"slot-{slot:02d}"
        out.mkdir()  # Failed/stale reservations remain owned diagnostics, never overwritten.
        pin_chain(pins, out)
        window = {"slot": slot, "output_root": str(out), "state": "reserved", "max_updates": 2,
            "reserved_bytes": SLOT_BYTES, "predecessor": value["selected_point"], "receipt": None}
        value.update(version=value["version"] + 1, previous_sha256=previous.sha256,
            windows=value["windows"] + [window], reserved_bytes=slot * SLOT_BYTES)
        return _publish(pins, owned, value)
    return _guarded(reserve)


def _window_inventory(pins, window, point):
    out = plain_path(Path(window["output_root"]))
    pin_chain(pins, out)
    checkpoint = out / "checkpoint"
    pin_chain(pins, checkpoint)
    names = {"generator.pt", "discriminator.pt", "training.pt", "manifest.json", "READY"}
    if {p.name for p in out.iterdir()} != {"checkpoint", "accepted-update.json"} or {p.name for p in checkpoint.iterdir()} != names:
        raise ValueError("owned window inventory")
    total = 0
    for path in [out / "accepted-update.json", *(checkpoint / name for name in sorted(names))]:
        stream = pins.enter_context(pinned_file(path))
        total += stream.seek(0, 2)
    if total > window["reserved_bytes"]:
        raise ValueError("window exceeds reserved bytes")
    manifest = _read_json(pins, checkpoint / "manifest.json", point["checkpoint_manifest_sha256"])
    ready = pins.enter_context(pinned_file(checkpoint / "READY"))
    position = manifest["position"]
    if (ready.read(66) != point["checkpoint_manifest_sha256"].encode() + b"\n"
            or manifest["format"] != 2 or manifest["purpose"] != "complete-update-checkpoint"
            or manifest["identity"]["run_id"] != point["run_id"]
            or any(position[key] != point[key] for key in ("global_step", "completed_epoch", "next_ordinal", "epoch_size", "admission_sha256"))):
        raise ValueError("accepted window manifest")


def accept_window(previous, receipt):
    def accept(pins):
        value, owned = _load(pins, previous)
        if not value["windows"] or value["windows"][-1]["state"] != "reserved":
            raise ValueError("no reserved window")
        window = value["windows"][-1]
        if type(receipt) is not ReceiptRef or Path(receipt.path) != Path(window["output_root"]) / "accepted-update.json":
            raise ValueError("known reserved receipt")
        accepted = _receipt(pins, receipt, value["run_id"], value["admission_sha256"])
        point = accepted["point"]
        _next_point(accepted, value["selected_point"], window)
        _window_inventory(pins, window, point)
        window.update(state="accepted", receipt=_receipt_dict(receipt))
        value.update(version=value["version"] + 1, previous_sha256=previous.sha256, selected_point=point)
        return _publish(pins, owned, value)
    return _guarded(accept)


def fail_window(previous):
    """Explicit caller settlement after failure. Consumes its slot; never retries."""
    def fail(pins):
        value, owned = _load(pins, previous)
        if not value["windows"] or value["windows"][-1]["state"] != "reserved":
            raise ValueError("no reserved window")
        value["windows"][-1]["state"] = "failed"
        value.update(version=value["version"] + 1, previous_sha256=previous.sha256)
        return _publish(pins, owned, value)
    return _guarded(fail)
