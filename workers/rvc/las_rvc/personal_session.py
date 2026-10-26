"""Lightweight explicit session protocol. No Torch, NumPy or model discovery."""
from contextlib import ExitStack
from dataclasses import asdict
import hashlib
import math
from pathlib import Path
import random
import re

from .checkpoints import CheckpointError, RunIdentity, PAYLOAD_NAMES, _json, _load_json, _write_new, _digest
from .owned_files import pin_chain, pinned_file, plain_path

DEADLINE_MS = 3600000
LAST_UPDATE_START_SECONDS = 3300
CHECKPOINT_INTERVAL = 40
MAX_POINTS = 4
POINT_BYTES = 1536 * 1024 ** 2
FREE_GUARD_BYTES = 8 * 1024 ** 3
STOP_REASONS = {"target_epoch", "time_budget", "user_stop", "checkpoint_budget"}


def _sha(value):
    if type(value) is not str or re.fullmatch(r"[0-9a-f]{64}", value) is None:
        raise ValueError("known digest")
    return value


def _read(pins, path, digest):
    stream = pins.enter_context(pinned_file(plain_path(Path(path))))
    raw = stream.read(65537)
    if len(raw) > 65536 or hashlib.sha256(raw).hexdigest() != _sha(digest):
        raise ValueError("known metadata")
    return _load_json(raw)


def _ref(value, field="root"):
    if type(value) is not dict or set(value) != {field, "sha256"}:
        raise ValueError("explicit reference")
    return plain_path(Path(value[field])), _sha(value["sha256"])


def expected_position(step, ids, admission_sha256):
    if (type(step) is not int or step < 1 or type(ids) not in (tuple, list) or not 1 <= len(ids) <= 2048
            or list(ids) != sorted(set(ids))
            or any(type(i) is not str or re.fullmatch(r"clip-[0-9]{6}", i) is None for i in ids)):
        raise CheckpointError("training.session.cursor.invalid")
    epoch, ordinal = divmod(step, len(ids))
    if epoch > 200 or epoch == 200 and ordinal:
        raise CheckpointError("training.session.cursor.invalid")
    order = list(ids)
    random.Random(1234 + epoch).shuffle(order)
    return {"completed_epoch": epoch, "next_ordinal": ordinal, "epoch_size": len(ids), "global_step": step,
        "sampler_seed": 1234, "train_ids_sha256": hashlib.sha256(_json(list(ids))).hexdigest(),
        "order_sha256": hashlib.sha256(_json(order)).hexdigest(), "admission_sha256": _sha(admission_sha256)}


def session_stop_reason(step, target_step, elapsed, stop_requested, publications):
    if (type(step) is not int or step < 1 or type(target_step) is not int or target_step < step
            or type(elapsed) not in (int, float) or not math.isfinite(elapsed) or elapsed < 0
            or type(stop_requested) is not bool or type(publications) is not int or not 0 <= publications <= MAX_POINTS):
        raise CheckpointError("training.session.budget.invalid")
    if step == target_step: return "target_epoch"
    if stop_requested: return "user_stop"
    if elapsed >= LAST_UPDATE_START_SECONDS: return "time_budget"
    if publications == MAX_POINTS: return "checkpoint_budget"
    return None


def _manifest(pins, ref, identity, ids, admission_sha, step, *, payloads=False):
    root, sha = _ref(ref)
    pin_chain(pins, root)
    if {p.name for p in root.iterdir()} != {*PAYLOAD_NAMES, "manifest.json", "READY"}:
        raise ValueError("complete point inventory")
    value = _read(pins, root / "manifest.json", sha)
    ready = pins.enter_context(pinned_file(root / "READY"))
    if ready.read(66) != sha.encode() + b"\n": raise ValueError("complete commit marker")
    expected = {"format": 2, "purpose": "complete-update-checkpoint", "identity": identity,
        "position": expected_position(step, ids, admission_sha), "files": value["files"]}
    if _json(value) != _json(expected) or set(value["files"]) != set(PAYLOAD_NAMES):
        raise ValueError("full point identity/cursor")
    total = len(_json(value)) + 65
    for name in PAYLOAD_NAMES:
        metadata = value["files"][name]
        if (type(metadata) is not dict or set(metadata) != {"bytes", "sha256"}
                or type(metadata["bytes"]) is not int or not 1 <= metadata["bytes"] <= POINT_BYTES):
            raise ValueError("payload bounds")
        _sha(metadata["sha256"])
        stream = pins.enter_context(pinned_file(root / name))
        if stream.seek(0, 2) != metadata["bytes"] or payloads and _digest(stream) != metadata:
            raise ValueError("payload integrity")
        total += metadata["bytes"]
    if total > POINT_BYTES: raise ValueError("point byte budget")
    return value, total


def _point_control(ref, identity, position):
    return {"phase": "personal_checkpoint_control", "checkpoint_root": ref["root"],
        "checkpoint_manifest_sha256": ref["sha256"], "run_id": identity["run_id"],
        **{key: position[key] for key in ("global_step", "completed_epoch", "next_ordinal", "epoch_size", "admission_sha256")}}


def _previous_receipt(pins, request, manifest):
    path, sha = _ref(request["previous_receipt"], "path")
    value = _read(pins, path, sha)
    if (type(value.get("native_worker_exit_code")) is not int or value["native_worker_exit_code"] != 0
            or value.get("native_worker_timed_out") is not False
            or value.get("accepted_after_native_worker_exit") is not True
            or value.get("quality_established") is not False or value.get("auto_activate_voice") is not False):
        raise ValueError("accepted predecessor required")
    if value.get("purpose") == "supervisor-accepted-personal-native-update":
        if type(value["format"]) is not int or value["format"] != 2 or value["native_worker_deadline_ms"] != 60000:
            raise ValueError("old receipt")
        selected = value["point"]
    elif value.get("purpose") == "supervisor-accepted-personal-native-session":
        _validate_selection(value)
        selected = value["selected_point"]
    else:
        raise ValueError("unknown receipt purpose")
    expected = _point_control(request["previous"], manifest["identity"], manifest["position"])
    if _json({k: selected[k] for k in expected}) != _json(expected):
        raise ValueError("known accepted predecessor binding")


def _request(pins, path, sha, output):
    request = _read(pins, path, sha)
    if (type(request) is not dict or set(request) != {"format", "purpose", "run_id", "inputs", "output_root",
            "previous", "previous_receipt", "target_completed_epoch"}
            or type(request["format"]) is not int or request["format"] != 1
            or request["purpose"] != "persistent-personal-native-session"
            or plain_path(Path(request["output_root"])) != plain_path(Path(output))):
        raise ValueError("session request")
    fields = request["inputs"]
    if type(fields) is not dict or set(fields) != {"dataset_root", "dataset_sha256", "preflight", "corpus",
            "candidate", "admission", "source_root", "base_asset_root"}:
        raise ValueError("training input schema")
    for name in ("dataset_root", "source_root", "base_asset_root"): plain_path(Path(fields[name]))
    _sha(fields["dataset_sha256"])
    for name in ("preflight", "corpus", "candidate", "admission"): _ref(fields[name])
    admission_root, admission_sha = _ref(fields["admission"])
    admission = _read(pins, admission_root / "decision.json", admission_sha)
    ready = pins.enter_context(pinned_file(admission_root / "READY"))
    if (ready.read(66) != admission_sha.encode() + b"\n" or admission["training_admitted"] is not True
            or admission["purpose"] != "experimental-native-rvc-training-admission"
            or admission["preset"]["id"] != "rvc-cpu-experimental-updates-v1"
            or type(admission["preset"]["sampler_seed"]) is not int or admission["preset"]["sampler_seed"] != 1234):
        raise ValueError("admitted session")
    ids = admission["split"]["train_clip_ids"]
    identity = asdict(RunIdentity(request["run_id"], admission["input"]["corpus_manifest_sha256"],
        admission["runtime_sha256"], admission["config_sha256"], admission["runtime_identity"]["rvc_commit"],
        admission["preset"]["id"], 1, 40000, True))
    previous_root, previous_sha = _ref(request["previous"])
    previous = _read(pins, previous_root / "manifest.json", previous_sha)
    step = previous["position"]["global_step"]
    _manifest(pins, request["previous"], identity, ids, admission_sha, step)
    _previous_receipt(pins, request, previous)
    target = request["target_completed_epoch"]
    if (type(target) is not int or target != step // len(ids) + 1 or target > 200
            or previous_root.is_relative_to(Path(output)) or Path(output).is_relative_to(previous_root)):
        raise ValueError("next epoch and protected predecessor")
    return request, step, ids, identity


def read_session_request(path, sha, output):
    try:
        with ExitStack() as pins: return _request(pins, path, sha, output)
    except CheckpointError: raise
    except Exception: raise CheckpointError("training.session.request.invalid") from None


def _validate_selection(value):
    keys = {"format", "purpose", "request", "previous_checkpoint", "entries", "control", "selected_point",
        "run_id", "admission_sha256", "native_worker_exit_code", "native_worker_timed_out", "native_worker_deadline_ms",
        "accepted_after_native_worker_exit", "quality_established", "auto_activate_voice"}
    if (type(value) is not dict or set(value) != keys or type(value["format"]) is not int or value["format"] != 1
            or value["purpose"] != "supervisor-accepted-personal-native-session"
            or type(value["native_worker_exit_code"]) is not int or value["native_worker_exit_code"] != 0
            or value["native_worker_timed_out"] is not False or value["native_worker_deadline_ms"] != DEADLINE_MS
            or value["accepted_after_native_worker_exit"] is not True or value["quality_established"] is not False
            or value["auto_activate_voice"] is not False):
        raise ValueError("accepted session receipt")
    _ref(value["request"], "path")
    _ref(value["previous_checkpoint"])
    entries = value["entries"]
    if type(entries) is not list or not 1 <= len(entries) <= MAX_POINTS: raise ValueError("accepted entries")
    selected = value["selected_point"]
    control = value["control"]
    if (control["request_sha256"] != value["request"]["sha256"] or control["point_count"] != len(entries)
            or control["global_step"] != selected["global_step"] or control["stop_reason"] not in STOP_REASONS
            or entries[-1]["point"] != {"root": selected["checkpoint_root"], "sha256": selected["checkpoint_manifest_sha256"]}
            or selected["run_id"] != value["run_id"] or selected["admission_sha256"] != value["admission_sha256"]):
        raise ValueError("accepted selection binding")


def load_selection(pins, ref):
    """Read only the caller-known selection; legacy ledgers remain unchanged."""
    root = plain_path(Path(ref.root))
    value = _read(pins, root / "ledger.json", ref.sha256)
    if value.get("purpose") != "supervisor-accepted-personal-native-session":
        from .continuation_ledger import _load
        return _load(pins, ref)
    pin_chain(pins, root)
    if {p.name for p in root.iterdir()} != {"ledger.json", "READY"}: raise ValueError("selection inventory")
    ready = pins.enter_context(pinned_file(root / "READY"))
    if ready.read(66) != ref.sha256.encode() + b"\n": raise ValueError("selection READY")
    _validate_selection(value)
    return value, root.parent


def accept_session(path, sha, output, control, *, worker_exit_code, worker_timed_out, deadline_ms):
    if (type(worker_exit_code) is not int or worker_exit_code != 0 or worker_timed_out is not False
            or type(deadline_ms) is not int or deadline_ms != DEADLINE_MS):
        raise CheckpointError("training.session.supervisor.unaccepted")
    try:
        with ExitStack() as pins:
            request, start, ids, identity = _request(pins, path, sha, output)
            root = plain_path(Path(output))
            pin_chain(pins, root)
            if (type(control) is not dict or set(control) != {"phase", "request_sha256", "journal_sha256", "global_step",
                    "completed_updates", "point_count", "stop_reason"}
                    or control["phase"] != "personal_session_control" or control["request_sha256"] != sha):
                raise ValueError("final control")
            journal = _read(pins, root / "journal.json", control["journal_sha256"])
            if (type(journal) is not dict or set(journal) != {"format", "purpose", "request_sha256", "start_global_step", "entries", "stop_reason"}
                    or type(journal["format"]) is not int or journal["format"] != 1
                    or journal["purpose"] != "personal-session-candidate-journal" or journal["request_sha256"] != sha
                    or type(journal["start_global_step"]) is not int or journal["start_global_step"] != start
                    or journal["stop_reason"] not in STOP_REASONS):
                raise ValueError("final journal")
            entries, prior, step, total = journal["entries"], request["previous"], start, 0
            if type(entries) is not list or not 1 <= len(entries) <= MAX_POINTS: raise ValueError("point cap")
            points_root = root / "points"
            pin_chain(pins, points_root)
            if {p.name for p in points_root.iterdir()} != {f"point-{i:03d}" for i in range(1, len(entries) + 1)}:
                raise ValueError("owned points inventory")
            for index, entry in enumerate(entries, 1):
                if (type(entry) is not dict or set(entry) != {"point", "previous", "global_step"}
                        or entry["previous"] != prior or type(entry["global_step"]) is not int
                        or not 1 <= entry["global_step"] - step <= CHECKPOINT_INTERVAL
                        or entry["point"]["root"] != str(points_root / f"point-{index:03d}/checkpoint")
                        or {p.name for p in (points_root / f"point-{index:03d}").iterdir()} != {"checkpoint"}):
                    raise ValueError("owned contiguous points")
                manifest, size = _manifest(pins, entry["point"], identity, ids, request["inputs"]["admission"]["sha256"],
                    entry["global_step"], payloads=True)
                total += size
                step, prior = entry["global_step"], entry["point"]
            if total > MAX_POINTS * POINT_BYTES: raise ValueError("session bytes")
            target = request["target_completed_epoch"] * len(ids)
            reason = journal["stop_reason"]
            if (step > target or reason == "target_epoch" and step != target
                    or reason != "target_epoch" and step == target
                    or reason == "checkpoint_budget" and len(entries) != MAX_POINTS):
                raise ValueError("actual stop reason")
            expected = {"phase": "personal_session_control", "request_sha256": sha,
                "journal_sha256": control["journal_sha256"], "global_step": step, "completed_updates": step - start,
                "point_count": len(entries), "stop_reason": reason}
            if _json(control) != _json(expected): raise ValueError("actual session delta")
            receipt = {"format": 1, "purpose": "supervisor-accepted-personal-native-session",
                "request": {"path": str(path), "sha256": sha}, "previous_checkpoint": request["previous"],
                "entries": entries, "control": control, "selected_point": _point_control(prior, identity, manifest["position"]),
                "run_id": request["run_id"], "admission_sha256": request["inputs"]["admission"]["sha256"],
                "native_worker_exit_code": 0, "native_worker_timed_out": False, "native_worker_deadline_ms": DEADLINE_MS,
                "accepted_after_native_worker_exit": True, "quality_established": False, "auto_activate_voice": False}
            _validate_selection(receipt)
            selection = root / "selection"
            selection.mkdir()
            raw = _json(receipt)
            digest = hashlib.sha256(raw).hexdigest()
            _write_new(selection / "ledger.json", raw)
            _write_new(selection / "READY", digest.encode() + b"\n")
            return receipt, digest
    except CheckpointError: raise
    except Exception: raise CheckpointError("training.session.supervisor.invalid") from None
