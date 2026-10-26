"""Owned offline workflow orchestration. This module imports no ML libraries."""
from contextlib import ExitStack
from dataclasses import dataclass
import hashlib
import json
import math
import os
from pathlib import Path
import queue
import re
import signal
import subprocess
import tempfile
import threading
import time
import uuid
import wave

from .owned_files import pin_chain, pinned_file, plain_path

MAX_JSON = 65536
STAGES = {"Prepare-RvcDataset.py", "Audit-RvcSourceQuality.py", "Check-RvcIntake.py",
    "Extract-RvcFeatures.py", "Manage-RvcCorpus.py", "Admit-RvcTraining.py",
    "Finalize-RvcAdmission.py", "Update-RvcPersonal.py", "Accept-RvcUpdate.py",
    "Train-RvcSession.py", "Accept-RvcSession.py"}
RUNTIME_FIELDS = {"python_path", "powershell_path", "scripts_root", "decoder_root",
    "source_root", "base_asset_root", "feature_asset_root"}
INPUT_FIELDS = {"dataset_root", "dataset_sha256", "preflight", "corpus", "candidate",
    "admission", "source_root", "base_asset_root"}


class WorkflowError(Exception):
    def __init__(self, code, native_code=None):
        self.code = code
        self.native_code = native_code
        super().__init__(code)


@dataclass(frozen=True)
class StageResult:
    events: list


def _json(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def _sha(value):
    if type(value) is not str or re.fullmatch("[0-9a-f]{64}", value) is None:
        raise ValueError("known digest")
    return value


def _load(raw):
    def unique(pairs):
        value = {}
        for key, item in pairs:
            if key in value: raise ValueError("duplicate JSON field")
            value[key] = item
        return value
    return json.loads(raw, object_pairs_hook=unique,
        parse_constant=lambda _: (_ for _ in ()).throw(ValueError("nonfinite JSON")))


def _read(path, sha=None, limit=MAX_JSON):
    with pinned_file(plain_path(Path(path))) as stream:
        raw = stream.read(limit + 1)
    if len(raw) > limit or sha is not None and hashlib.sha256(raw).hexdigest() != _sha(sha):
        raise ValueError("known bounded JSON")
    return _load(raw), hashlib.sha256(raw).hexdigest()


def _write(path, payload):
    with path.open("xb") as stream:
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())


def _ref(root, name="manifest.json", expected=None):
    root = plain_path(Path(root))
    _, sha = _read(root / name, expected, limit=2 * 1024 ** 2)
    with pinned_file(root / "READY") as stream:
        if stream.read(66) != sha.encode() + b"\n": raise ValueError("publication marker")
    return {"root": str(root), "sha256": sha}


def _source(pins, value):
    if type(value) is not dict or set(value) != {"path", "sha256", "consent_confirmed"} or value["consent_confirmed"] is not True:
        raise ValueError("source consent")
    path = plain_path(Path(value["path"]))
    stream = pins.enter_context(pinned_file(path))
    digest = hashlib.sha256()
    size = 0
    while data := stream.read(1024 * 1024):
        size += len(data)
        if size > 1024 ** 3: raise ValueError("source byte limit")
        digest.update(data)
    if digest.hexdigest() != _sha(value["sha256"]): raise ValueError("changed recording")
    stream.seek(0)
    with wave.open(stream, "rb") as audio:
        if (audio.getnchannels() != 1 or audio.getsampwidth() != 2 or audio.getframerate() != 48000
                or audio.getcomptype() != "NONE" or not 300 * 48000 <= audio.getnframes() <= 1200 * 48000
                or size < audio.getnframes() * 2 + 44):
            raise ValueError("saved mono48k PCM16 recording")


def _request(pins, path, sha, output):
    value, _ = _read(path, sha)
    if (type(value) is not dict or set(value) != {"format", "purpose", "source", "runtime", "previous", "output_root"}
            or type(value["format"]) is not int or value["format"] != 1
            or value["purpose"] != "local-personal-training-workflow"
            or plain_path(Path(value["output_root"])) != plain_path(Path(output))):
        raise ValueError("workflow request")
    runtime = value["runtime"]
    if type(runtime) is not dict or set(runtime) != RUNTIME_FIELDS: raise ValueError("explicit runtime")
    for name, item in runtime.items():
        target = plain_path(Path(item))
        if name.endswith("_path"):
            pins.enter_context(pinned_file(target))
        else:
            pin_chain(pins, target)
    previous = value["previous"]
    if previous is not None:
        if type(previous) is not dict or set(previous) != {"path", "sha256"}: raise ValueError("previous state ref")
        plain_path(Path(previous["path"]))
        _sha(previous["sha256"])
    _source(pins, value["source"])
    return value


def read_request(path, sha, output):
    try:
        with ExitStack() as pins: return _request(pins, path, sha, output)
    except Exception:
        raise WorkflowError("workflow.request.invalid") from None


def _accepted(state):
    accepted, inputs = state["accepted"], state["inputs"]
    if accepted is None:
        if any(state[k] != 0 for k in ("global_step", "completed_epoch", "epoch_size")):
            raise ValueError("no model cursor")
        return
    if (type(accepted) is not dict or set(accepted) != {"checkpoint", "receipt"}
            or type(inputs) is not dict or set(inputs) != INPUT_FIELDS):
        raise ValueError("accepted input binding")
    checkpoint, receipt = accepted["checkpoint"], accepted["receipt"]
    if (type(checkpoint) is not dict or set(checkpoint) != {"root", "sha256"}
            or type(receipt) is not dict or set(receipt) != {"path", "sha256"}):
        raise ValueError("accepted references")
    _ref(checkpoint["root"], expected=checkpoint["sha256"])
    manifest, _ = _read(Path(checkpoint["root"]) / "manifest.json", checkpoint["sha256"])
    proof, _ = _read(receipt["path"], receipt["sha256"])
    if proof.get("purpose") == "supervisor-accepted-personal-native-update":
        point, deadline = proof["point"], 60000
    elif proof.get("purpose") == "supervisor-accepted-personal-native-session":
        point, deadline = proof["selected_point"], 3600000
        with pinned_file(Path(receipt["path"]).parent / "READY") as stream:
            if stream.read(66) != receipt["sha256"].encode() + b"\n": raise ValueError("accepted session marker")
    else:
        raise ValueError("accepted native receipt")
    if (type(proof.get("native_worker_exit_code")) is not int or proof["native_worker_exit_code"] != 0
            or proof.get("native_worker_timed_out") is not False
            or proof.get("native_worker_deadline_ms") != deadline
            or proof.get("accepted_after_native_worker_exit") is not True
            or proof.get("quality_established") is not False or proof.get("auto_activate_voice") is not False
            or manifest.get("format") != 2 or manifest.get("purpose") != "complete-update-checkpoint"
            or manifest["identity"]["run_id"] != state["run_id"]
            or point["run_id"] != state["run_id"] or point["checkpoint_root"] != checkpoint["root"]
            or point["checkpoint_manifest_sha256"] != checkpoint["sha256"]):
        raise ValueError("accepted native proof")
    position = manifest["position"]
    for key in ("global_step", "completed_epoch", "epoch_size"):
        if type(state[key]) is not int or state[key] != position[key] or state[key] != point[key]:
            raise ValueError("accepted cursor")
    if (not 1 <= state["epoch_size"] <= 2048 or not 0 <= state["completed_epoch"] <= 200
            or type(position["next_ordinal"]) is not int or not 0 <= position["next_ordinal"] < state["epoch_size"]
            or point["next_ordinal"] != position["next_ordinal"]
            or state["global_step"] < 1
            or state["global_step"] != state["completed_epoch"] * state["epoch_size"] + position["next_ordinal"]
            or position["admission_sha256"] != inputs["admission"]["sha256"]
            or point["admission_sha256"] != inputs["admission"]["sha256"]):
        raise ValueError("admission cursor binding")


def _previous(ref, source):
    state, _ = _read(ref["path"], ref["sha256"])
    with pinned_file(Path(ref["path"]).parent / "READY") as stream:
        if stream.read(66) != ref["sha256"].encode() + b"\n": raise ValueError("known state marker")
    keys = {"format", "purpose", "request", "source", "run_id", "inputs", "accepted",
        "global_step", "completed_epoch", "epoch_size", "stop_reason"}
    if (type(state) is not dict or set(state) != keys or type(state["format"]) is not int or state["format"] != 1
            or state["purpose"] != "local-personal-training-workflow-state" or state["source"] != source
            or type(state["run_id"]) is not str or re.fullmatch("[0-9a-f]{32}", state["run_id"]) is None
            or state["accepted"] is None):
        raise ValueError("same source accepted continuation")
    _accepted(state)
    return state


def _publish(root, name, state, phase):
    _accepted(state)
    directory = root / name
    directory.mkdir()
    raw = _json(state)
    if len(raw) > MAX_JSON: raise ValueError("bounded workflow state")
    digest = hashlib.sha256(raw).hexdigest()
    _write(directory / "workflow_state.json", raw)
    _write(directory / "READY", digest.encode() + b"\n")
    _read(directory / "workflow_state.json", digest)
    return {"phase": phase, "state_path": str(directory / "workflow_state.json"), "state_sha256": digest,
        **{k: state[k] for k in ("global_step", "completed_epoch", "epoch_size", "stop_reason")}}


def _stopped(root):
    path = root / "STOP"
    if not path.exists(): return False
    with pinned_file(path) as stream:
        if stream.seek(0, 2) > 64: raise WorkflowError("workflow.stop.invalid")
    return True


def _start_gate(root):
    value = os.environ.get("LAS_WORKFLOW_START_GATE")
    if value is None: return
    gate = plain_path(Path(value))
    if gate != root / "START": raise WorkflowError("workflow.start_gate.invalid")
    until = time.monotonic() + 15
    while not gate.exists():
        if _stopped(root): return
        if time.monotonic() >= until: raise WorkflowError("workflow.start_gate.timeout")
        time.sleep(.05)
    with pinned_file(gate) as stream:
        if stream.seek(0, 2) > 64: raise WorkflowError("workflow.start_gate.invalid")


def _control(result, phase):
    found = [e for e in result.events if e.get("phase") == phase]
    if len(found) != 1: raise WorkflowError("workflow.stage.control.invalid")
    return found[0]


class _WindowsJob:
    """Assign a suspended owned child before any descendant can start."""
    def __init__(self):
        import ctypes
        from ctypes import wintypes
        self.ctypes = ctypes
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.ntdll = ctypes.WinDLL("ntdll")
        class Basic(ctypes.Structure):
            _fields_ = [("process_time", ctypes.c_int64), ("job_time", ctypes.c_int64), ("flags", wintypes.DWORD),
                ("min_working_set", ctypes.c_size_t), ("max_working_set", ctypes.c_size_t),
                ("active_limit", wintypes.DWORD), ("affinity", ctypes.c_size_t), ("priority", wintypes.DWORD), ("scheduling", wintypes.DWORD)]
        class Io(ctypes.Structure):
            _fields_ = [(name, ctypes.c_uint64) for name in ("read_count", "write_count", "other_count", "read_bytes", "write_bytes", "other_bytes")]
        class Extended(ctypes.Structure):
            _fields_ = [("basic", Basic), ("io", Io), ("process_memory", ctypes.c_size_t), ("job_memory", ctypes.c_size_t),
                ("peak_process_memory", ctypes.c_size_t), ("peak_job_memory", ctypes.c_size_t)]
        class Accounting(ctypes.Structure):
            _fields_ = [("user_time", ctypes.c_int64), ("kernel_time", ctypes.c_int64), ("period_user", ctypes.c_int64),
                ("period_kernel", ctypes.c_int64), ("page_faults", wintypes.DWORD), ("total", wintypes.DWORD),
                ("active", wintypes.DWORD), ("terminated", wintypes.DWORD)]
        self.Accounting = Accounting
        self.kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
        self.kernel.CreateJobObjectW.restype = wintypes.HANDLE
        for name, arguments in {
            "SetInformationJobObject": [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD],
            "QueryInformationJobObject": [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p],
            "AssignProcessToJobObject": [wintypes.HANDLE, wintypes.HANDLE],
            "TerminateJobObject": [wintypes.HANDLE, wintypes.UINT], "CloseHandle": [wintypes.HANDLE]}.items():
            getattr(self.kernel, name).argtypes = arguments
            getattr(self.kernel, name).restype = wintypes.BOOL
        self.ntdll.NtResumeProcess.argtypes = [wintypes.HANDLE]
        self.ntdll.NtResumeProcess.restype = wintypes.LONG
        self.handle = self.kernel.CreateJobObjectW(None, None)
        self.assigned = False
        if not self.handle: raise WorkflowError("workflow.job.failed")
        limits = Extended(); limits.basic.flags = 0x2000
        if not self.kernel.SetInformationJobObject(self.handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
            self.close(); raise WorkflowError("workflow.job.failed")

    def assign_and_resume(self, child):
        if not self.kernel.AssignProcessToJobObject(self.handle, int(child._handle)):
            raise WorkflowError("workflow.job.assign.failed")
        self.assigned = True
        if self.ntdll.NtResumeProcess(int(child._handle)) != 0:
            raise WorkflowError("workflow.job.resume.failed")

    def terminate(self):
        if not self.kernel.TerminateJobObject(self.handle, 1):
            raise WorkflowError("workflow.cleanup.unconfirmed")

    def empty(self):
        counts = self.Accounting()
        if not self.kernel.QueryInformationJobObject(self.handle, 1, self.ctypes.byref(counts), self.ctypes.sizeof(counts), None):
            raise WorkflowError("workflow.cleanup.unconfirmed")
        return counts.active == 0

    def close(self):
        if self.handle:
            if not self.kernel.CloseHandle(self.handle): raise WorkflowError("workflow.cleanup.unconfirmed")
            self.handle = None


class _SessionProgress:
    """Only valid finite state transitions renew the persistent-session watchdog."""
    def __init__(self):
        self.mode = "launch"
        self.batch_count = self.epoch_size = self.step = self.saves = self.last_saved = self.start = 0

    def accept(self, event):
        try:
            phase = event["phase"]
            def integer(name, low=0, high=409600):
                value = event[name]
                if type(value) is not int or not low <= value <= high: raise ValueError("bounded progress integer")
                return value
            if any(type(value) is float and not math.isfinite(value) for value in event.values()):
                raise ValueError("finite progress")
            if phase == "session_preparing" and self.mode == "launch":
                self.mode = "preparing"
            elif phase == "session_batch_ready" and self.mode == "preparing":
                count, size = integer("batch_count", 1, 2048), integer("total_batches", 1, 2048)
                if count != self.batch_count + 1 or count > size or self.epoch_size not in (0, size): raise ValueError("batch progress")
                self.batch_count, self.epoch_size = count, size
            elif phase == "session_state_ready" and self.mode == "preparing":
                if self.batch_count != self.epoch_size or self.epoch_size < 1 or integer("epoch_size", 1, 2048) != self.epoch_size:
                    raise ValueError("all batches ready")
                self.step = self.start = self.last_saved = integer("global_step", 1)
                self.mode = "ready"
            elif phase == "session_update_started" and self.mode == "ready":
                if integer("global_step", 1) != self.step + 1 or re.fullmatch("clip-[0-9]{6}", event["clip_id"]) is None:
                    raise ValueError("next update")
                self.mode = "updating"
            elif phase == "session_update_complete" and self.mode == "updating":
                step = integer("global_step", 1)
                epoch, ordinal = integer("completed_epoch", 0, 200), integer("next_ordinal", 0, self.epoch_size - 1)
                if step != self.step + 1 or epoch * self.epoch_size + ordinal != step: raise ValueError("update cursor")
                self.step, self.mode = step, "ready"
            elif phase == "session_save_started" and self.mode == "ready":
                if integer("global_step", 1) != self.step or not 1 <= self.step - self.last_saved <= 40 or integer("point_count", 1, 4) != self.saves + 1:
                    raise ValueError("save progress")
                self.mode = "saving"
            elif phase == "session_save_complete" and self.mode == "saving":
                if integer("global_step", 1) != self.step or integer("point_count", 1, 4) != self.saves + 1: raise ValueError("save completion")
                self.saves += 1; self.last_saved, self.mode = self.step, "ready"
            elif phase == "personal_session_control" and self.mode == "ready":
                _sha(event["request_sha256"]); _sha(event["journal_sha256"])
                if (integer("global_step", 1) != self.step or self.last_saved != self.step
                        or integer("point_count", 1, 4) != self.saves or integer("completed_updates", 1, 160) != self.step - self.start
                        or event["stop_reason"] not in {"target_epoch", "time_budget", "user_stop", "checkpoint_budget"}):
                    raise ValueError("final control")
                self.mode = "ended"
            else:
                raise ValueError("invalid transition")
        except Exception:
            raise WorkflowError("workflow.session.progress.invalid") from None


class OwnedStageRunner:
    def __init__(self, runtime, emit=lambda event: None):
        self.runtime, self.emit = runtime, emit
        self.cache = None

    def run(self, script, arguments, *, stdin=None, deadline_ms=60000, stop_root=None, session_root=None):
        if script not in STAGES: raise WorkflowError("workflow.stage.invalid")
        events, child, job = [], None, None
        failure = None
        progress = _SessionProgress() if session_root is not None else None
        try:
            with ExitStack() as pins:
                stage_path = plain_path(Path(self.runtime["scripts_root"]) / script)
                pins.enter_context(pinned_file(stage_path))
                env = os.environ.copy()
                for name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMBA_NUM_THREADS"): env[name] = "2"
                env.update(CUDA_VISIBLE_DEVICES="", RVC_CUDA_GRAPH="0", HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", PYTHONUTF8="1")
                env.pop("LIBROSA_CACHE_DIR", None)
                if self.cache is None:
                    # A fresh explicit cache owned by this invocation; paths under
                    # project/model folders can exceed Numba's Windows limit.
                    self.cache = plain_path(Path(tempfile.mkdtemp(prefix="las-rvc-")))
                cache = self.cache
                pin_chain(pins, cache)
                env["NUMBA_CACHE_DIR"] = str(cache)
                env["HF_HOME"] = str(cache / "hf")
                if len(str(cache)) > 128: raise WorkflowError("workflow.cache_path_too_long")
                if os.name == "nt": job = _WindowsJob()
                child = subprocess.Popen([self.runtime["python_path"], "-I", str(stage_path), *arguments],
                    stdin=subprocess.PIPE if stdin is not None else subprocess.DEVNULL, stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE, env=env, cwd=self.runtime["scripts_root"],
                    creationflags=(0x00000004 | 0x08000000) if os.name == "nt" else 0,
                    start_new_session=os.name != "nt")
                if job is not None: job.assign_and_resume(child)
                messages = queue.Queue(maxsize=128)
                failed = threading.Event()
                def output_reader():
                    try:
                        total = 0
                        while line := child.stdout.readline(16385):
                            total += len(line)
                            if len(line) > 16384 or total > 8 * 1024 ** 2 or not line.endswith(b"\n"):
                                failed.set(); break
                            messages.put(line, timeout=2)
                    except Exception: failed.set()
                    finally:
                        try: messages.put(None, timeout=2)
                        except queue.Full: failed.set()
                def error_reader():
                    try:
                        total = 0
                        while data := child.stderr.read(65536):
                            total += len(data)
                            if total > 8 * 1024 ** 2: failed.set()
                    except Exception: failed.set()
                stdout_thread = threading.Thread(target=output_reader, daemon=True)
                stderr_thread = threading.Thread(target=error_reader, daemon=True)
                stdout_thread.start(); stderr_thread.start()
                if stdin is not None:
                    child.stdin.write(stdin); child.stdin.close()
                started = idle = time.monotonic()
                eof = False
                while not eof or child.poll() is None:
                    now = time.monotonic()
                    if failed.is_set(): raise WorkflowError("workflow.stage.output.invalid")
                    if (now - started) * 1000 >= deadline_ms or session_root is not None and now - idle >= 120:
                        raise WorkflowError("workflow.stage.timeout")
                    if session_root is not None and _stopped(stop_root) and not (session_root / "STOP").exists():
                        _write(session_root / "STOP", b"stop\n")
                    try: line = messages.get(timeout=.1)
                    except queue.Empty: continue
                    if line is None:
                        eof = True; continue
                    event = _load(line)
                    if type(event) is not dict or type(event.get("phase")) is not str: raise ValueError("stage event")
                    if failure is not None: raise WorkflowError("workflow.stage.output.invalid")
                    if event["phase"] == "failed":
                        failure = WorkflowError("workflow.stage.failed", native_code=event.get("code"))
                        continue
                    if progress is not None: progress.accept(event)
                    events.append(event)
                    if len(events) > 10000: raise ValueError("bounded events")
                    public = {"phase": event["phase"]}
                    for key in ("global_step", "completed_epoch", "epoch_size"):
                        if key in event:
                            if type(event[key]) is not int or not 0 <= event[key] <= 409600: raise ValueError("progress cursor")
                            public[key] = event[key]
                    if event["phase"] not in {"personal_checkpoint_control", "personal_session_control"}:
                        self.emit(public)
                    idle = now
                exit_code = child.wait(timeout=5)
                stdout_thread.join(5); stderr_thread.join(5)
                if stdout_thread.is_alive() or stderr_thread.is_alive() or failed.is_set():
                    raise WorkflowError("workflow.cleanup.unconfirmed")
                if job is not None and not job.empty(): raise WorkflowError("workflow.cleanup.unconfirmed")
                if failure is not None: raise failure
                if exit_code != 0: raise WorkflowError("workflow.stage.failed")
                if progress is not None and progress.mode != "ended": raise WorkflowError("workflow.session.progress.invalid")
                return StageResult(events)
        except WorkflowError:
            raise
        except Exception:
            raise WorkflowError("workflow.stage.failed") from None
        finally:
            if child is not None:
                if child.poll() is None or job is not None and not job.empty():
                    if job is not None and job.assigned: job.terminate()
                    elif job is not None: child.kill()  # Still suspended; assignment never succeeded.
                    elif os.name != "nt": os.killpg(child.pid, signal.SIGKILL)
                    else: child.kill()
                    try: child.wait(timeout=5)
                    except Exception: raise WorkflowError("workflow.cleanup.unconfirmed") from None
                if job is not None:
                    until = time.monotonic() + 5
                    while not job.empty() and time.monotonic() < until: time.sleep(.05)
                    if not job.empty(): raise WorkflowError("workflow.cleanup.unconfirmed")
                for stream in (child.stdin, child.stdout, child.stderr):
                    if stream is not None: stream.close()
            if job is not None: job.close()


def run_workflow(path, sha, output, *, runner=None, emit=lambda event: None):
    """One next-epoch action; only known accepted refs can survive failure/resume."""
    try:
        with ExitStack() as pins:
            request = _request(pins, path, sha, output)
            root = plain_path(Path(output)); pin_chain(pins, root)
            allowed = {"STOP", "START"}
            if Path(path).parent == root: allowed.add(Path(path).name)
            if any(item.name not in allowed for item in root.iterdir()): raise ValueError("fresh action output")
            previous = None if request["previous"] is None else _previous(request["previous"], request["source"])
            state = {"format": 1, "purpose": "local-personal-training-workflow-state",
                "request": {"path": str(path), "sha256": sha}, "source": request["source"],
                "run_id": uuid.uuid4().hex if previous is None else previous["run_id"],
                "inputs": None if previous is None else previous["inputs"],
                "accepted": None if previous is None else previous["accepted"],
                "global_step": 0 if previous is None else previous["global_step"],
                "completed_epoch": 0 if previous is None else previous["completed_epoch"],
                "epoch_size": 0 if previous is None else previous["epoch_size"], "stop_reason": "user_stop"}
            _start_gate(root)
            runtime = request["runtime"]
            runner = OwnedStageRunner(runtime, emit) if runner is None else runner
            def finish(reason):
                state["stop_reason"] = reason
                return _publish(root, "state", state, "workflow_control")
            def stopped(): return _stopped(root)
            def directory(name):
                result = root / name; result.mkdir(); return result
            def stage(script, arguments, **options):
                return runner.run(script, [str(i) for i in arguments], stop_root=root, **options)
            if stopped(): return finish("user_stop")
            if previous is None:
                emit({"phase": "workflow_preparing"})
                prep = directory("prepared")
                stage("Prepare-RvcDataset.py", ["--source", request["source"]["path"], "--source-sha256", request["source"]["sha256"],
                    "--decoder-root", runtime["decoder_root"], "--output-root", prep, "--consent"])
                dataset = _ref(prep / "dataset")
                if stopped(): return finish("user_stop")
                audit = directory("source-quality")
                stage("Audit-RvcSourceQuality.py", ["--dataset-root", dataset["root"], "--manifest-sha256", dataset["sha256"], "--output-root", audit])
                _ref(audit, "report.json")
                if stopped(): return finish("user_stop")
                intake = directory("preflight")
                stage("Check-RvcIntake.py", ["--dataset-root", dataset["root"], "--manifest-sha256", dataset["sha256"],
                    "--output-root", intake, "--clip-id", "clip-000000", "--experimental"])
                preflight = _ref(intake / "preflight", "decision.json")
                decision, _ = _read(Path(preflight["root"]) / "decision.json", preflight["sha256"])
                if decision["source_preflight_passed"] is not True: raise WorkflowError("workflow.source.rejected")
                if stopped(): return finish("user_stop")
                manifest, _ = _read(Path(dataset["root"]) / "manifest.json", dataset["sha256"], 2 * 1024 ** 2)
                ids = [clip["id"] for clip in manifest["clips"]]
                if not 1 <= len(ids) <= 2048 or ids != [f"clip-{i:06d}" for i in range(len(ids))]: raise ValueError("prepared clip inventory")
                shards = []
                for index in range(0, len(ids), 3):
                    if stopped(): return finish("user_stop")
                    shard = directory(f"features-{index // 3:04d}")
                    arguments = ["--dataset-root", dataset["root"], "--manifest-sha256", dataset["sha256"],
                        "--source-root", runtime["source_root"], "--asset-root", runtime["feature_asset_root"], "--output-root", shard]
                    for clip_id in ids[index:index + 3]: arguments.extend(["--clip-id", clip_id])
                    stage("Extract-RvcFeatures.py", arguments)
                    shards.append(_ref(shard / "features"))
                if stopped(): return finish("user_stop")
                corpus_output = directory("corpus")
                arguments = ["--dataset-root", dataset["root"], "--manifest-sha256", dataset["sha256"], "append", "--output-root", corpus_output]
                for shard in shards: arguments.extend(["--shard", shard["root"], shard["sha256"]])
                stage("Manage-RvcCorpus.py", arguments)
                corpus = _ref(corpus_output / "corpus")
                if stopped(): return finish("user_stop")
                validation = directory("admission-candidate")
                candidate_result = stage("Admit-RvcTraining.py", ["--dataset-root", dataset["root"], "--manifest-sha256", dataset["sha256"],
                    "--preflight-root", preflight["root"], "--preflight-sha256", preflight["sha256"], "--corpus-root", corpus["root"],
                    "--corpus-sha256", corpus["sha256"], "--source-root", runtime["source_root"], "--base-asset-root", runtime["base_asset_root"],
                    "--output-root", validation, "--experimental"])
                candidate_control = _control(candidate_result, "experimental_training_candidate_saved")
                if candidate_control["training_admitted"] is not False: raise ValueError("candidate not admitted")
                candidate = _ref(validation / "candidate", "decision.json", candidate_control["candidate_sha256"])
                if stopped(): return finish("user_stop")
                admission_output = directory("admission")
                stage("Finalize-RvcAdmission.py", ["--candidate-root", candidate["root"], "--candidate-sha256", candidate["sha256"],
                    "--output-root", admission_output, "--worker-exit-code", "0", "--worker-deadline-ms", "60000"])
                admission = _ref(admission_output / "admission", "decision.json")
                state["inputs"] = {"dataset_root": dataset["root"], "dataset_sha256": dataset["sha256"], "preflight": preflight,
                    "corpus": corpus, "candidate": candidate, "admission": admission,
                    "source_root": runtime["source_root"], "base_asset_root": runtime["base_asset_root"]}
                if stopped(): return finish("user_stop")
                initial = directory("initial-update")
                initial_request = {"format": 1, "purpose": "one-experimental-personal-native-update", "run_id": state["run_id"],
                    "inputs": state["inputs"], "output_root": str(initial), "previous": None}
                initial_path = root / "initial-request.json"
                initial_raw = _json(initial_request); _write(initial_path, initial_raw)
                initial_sha = hashlib.sha256(initial_raw).hexdigest()
                initial_args = ["--request", initial_path, "--request-sha256", initial_sha, "--output-root", initial]
                emit({"phase": "workflow_training", "global_step": 0})
                result = stage("Update-RvcPersonal.py", initial_args)
                control = _control(result, "personal_checkpoint_control")
                stage("Accept-RvcUpdate.py", [*initial_args, "--worker-exit-code", "0", "--worker-deadline-ms", "60000"], stdin=_json(control))
                checkpoint = _ref(initial / "checkpoint", expected=control["checkpoint_manifest_sha256"])
                receipt_path = initial / "accepted-update.json"
                _, receipt_sha = _read(receipt_path)
                state["accepted"] = {"checkpoint": checkpoint, "receipt": {"path": str(receipt_path), "sha256": receipt_sha}}
                for key in ("global_step", "completed_epoch", "epoch_size"): state[key] = control[key]
                state["stop_reason"] = "initial_checkpoint"
                emit(_publish(root, "initial-state", state, "workflow_checkpoint"))
            if stopped(): return finish("user_stop")
            if state["completed_epoch"] == 200: return finish("target_epoch")
            session = directory("session")
            session_request = {"format": 1, "purpose": "persistent-personal-native-session", "run_id": state["run_id"], "inputs": state["inputs"],
                "output_root": str(session), "previous": state["accepted"]["checkpoint"], "previous_receipt": state["accepted"]["receipt"],
                "target_completed_epoch": state["completed_epoch"] + 1}
            session_path = root / "session-request.json"
            session_raw = _json(session_request); _write(session_path, session_raw)
            session_sha = hashlib.sha256(session_raw).hexdigest()
            session_args = ["--request", session_path, "--request-sha256", session_sha, "--output-root", session]
            emit({"phase": "workflow_training", **{k: state[k] for k in ("global_step", "completed_epoch", "epoch_size")}})
            try:
                result = stage("Train-RvcSession.py", session_args, deadline_ms=3600000, session_root=session)
            except WorkflowError as error:
                if error.code == "workflow.stage.failed" and error.native_code == "training.session.no_updates" and stopped():
                    return finish("user_stop")
                raise
            session_control = _control(result, "personal_session_control")
            accepted_result = stage("Accept-RvcSession.py", [*session_args, "--worker-exit-code", "0", "--worker-deadline-ms", "3600000"], stdin=_json(session_control))
            accepted_control = _control(accepted_result, "personal_session_accepted")
            expected_selection = session / "selection"
            if accepted_control["selection_root"] != str(expected_selection): raise ValueError("owned selection")
            selection = _ref(expected_selection, "ledger.json", accepted_control["selection_sha256"])
            receipt, _ = _read(expected_selection / "ledger.json", selection["sha256"])
            point = receipt["selected_point"]
            state["accepted"] = {"checkpoint": {"root": point["checkpoint_root"], "sha256": point["checkpoint_manifest_sha256"]},
                "receipt": {"path": str(expected_selection / "ledger.json"), "sha256": selection["sha256"]}}
            for key in ("global_step", "completed_epoch", "epoch_size"): state[key] = point[key]
            return finish(accepted_control["stop_reason"])
    except WorkflowError:
        raise
    except Exception:
        raise WorkflowError("workflow.invalid") from None
