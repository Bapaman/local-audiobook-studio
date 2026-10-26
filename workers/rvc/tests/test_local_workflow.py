"""Workflow orchestration fixtures. No feature models, native training or real audio."""
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
try:
    from las_rvc.local_workflow import WorkflowError, StageResult, OwnedStageRunner, read_request, run_workflow
except ImportError:
    WorkflowError = StageResult = read_request = run_workflow = None


def write_json(path, value):
    raw = json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
    path.write_bytes(raw)
    return hashlib.sha256(raw).hexdigest()


def seal(root, name, value):
    root.mkdir(parents=True, exist_ok=True)
    digest = write_json(root / name, value)
    (root / "READY").write_bytes(digest.encode() + b"\n")
    return digest


class InertStages:
    """Slow native boundary replaced with exact output/receipt stage fixtures."""
    def __init__(self, fail_session=False, stop_after=None):
        self.fail_session, self.stop_after = fail_session, stop_after
        self.calls = []

    def run(self, script, arguments, *, stdin=None, deadline_ms=60000, stop_root=None, session_root=None):
        self.calls.append(script)
        fields = {}
        for index, value in enumerate(arguments):
            if value.startswith("--") and index + 1 < len(arguments) and not arguments[index + 1].startswith("--"):
                fields[value] = arguments[index + 1]
        output = Path(fields["--output-root"])
        event = {"phase": "ok"}
        if script == "Prepare-RvcDataset.py":
            clips = [{"id": f"clip-{i:06d}"} for i in range(4)]
            seal(output / "dataset", "manifest.json", {"clips": clips})
        elif script == "Audit-RvcSourceQuality.py":
            seal(output, "report.json", {"reason_counts": {"clip.source_quiet": 1}})
        elif script == "Check-RvcIntake.py":
            seal(output / "preflight", "decision.json", {"source_preflight_passed": True})
        elif script == "Extract-RvcFeatures.py":
            seal(output / "features", "manifest.json", {"selected_clip_ids": [arguments[i + 1] for i, a in enumerate(arguments) if a == "--clip-id"]})
        elif script == "Manage-RvcCorpus.py":
            seal(output / "corpus", "manifest.json", {"full_dataset_extracted": True})
        elif script == "Admit-RvcTraining.py":
            digest = seal(output / "candidate", "decision.json", {"training_admitted": False})
            event = {"phase": "experimental_training_candidate_saved", "candidate_sha256": digest, "training_admitted": False}
        elif script == "Finalize-RvcAdmission.py":
            seal(output / "admission", "decision.json", {"training_admitted": True, "split": {"train_clip_ids": ["clip-000000", "clip-000001", "clip-000002"]}})
        elif script in ("Update-RvcPersonal.py", "Train-RvcSession.py"):
            request = json.loads(Path(fields["--request"]).read_bytes())
            admission = request["inputs"]["admission"]["sha256"]
            manifest = {"format": 2, "purpose": "complete-update-checkpoint", "identity": {"run_id": request["run_id"]},
                "position": {"global_step": 1 if script == "Update-RvcPersonal.py" else 3 * request["target_completed_epoch"], "completed_epoch": 0 if script == "Update-RvcPersonal.py" else request["target_completed_epoch"],
                    "next_ordinal": 1 if script == "Update-RvcPersonal.py" else 0, "epoch_size": 3, "admission_sha256": admission}, "files": {}}
            digest = seal(output / "checkpoint", "manifest.json", manifest)
            if script == "Train-RvcSession.py" and self.fail_session:
                raise WorkflowError("workflow.stage.failed")
            event = {"phase": "personal_checkpoint_control", "checkpoint_root": str(output / "checkpoint"), "checkpoint_manifest_sha256": digest,
                "run_id": request["run_id"], "admission_sha256": admission, **{k: manifest["position"][k] for k in ("global_step", "completed_epoch", "next_ordinal", "epoch_size")}}
            if script == "Train-RvcSession.py":
                event = {"phase": "personal_session_control", "request_sha256": fields["--request-sha256"], "journal_sha256": "a" * 64,
                    "global_step": manifest["position"]["global_step"], "completed_updates": 2, "point_count": 1, "stop_reason": "target_epoch"}
        elif script == "Accept-RvcUpdate.py":
            event = json.loads(stdin)
            write_json(output / "accepted-update.json", {"format": 2, "purpose": "supervisor-accepted-personal-native-update",
                "point": event, "native_worker_exit_code": 0, "native_worker_timed_out": False,
                "native_worker_deadline_ms": 60000, "accepted_after_native_worker_exit": True,
                "quality_established": False, "auto_activate_voice": False})
        elif script == "Accept-RvcSession.py":
            manifest = json.loads((output / "checkpoint/manifest.json").read_bytes())
            point = {"phase": "personal_checkpoint_control", "checkpoint_root": str(output / "checkpoint"),
                "checkpoint_manifest_sha256": hashlib.sha256((output / "checkpoint/manifest.json").read_bytes()).hexdigest(),
                "run_id": manifest["identity"]["run_id"], **manifest["position"]}
            digest = seal(output / "selection", "ledger.json", {"purpose": "supervisor-accepted-personal-native-session", "selected_point": point,
                "native_worker_exit_code": 0, "native_worker_timed_out": False, "native_worker_deadline_ms": 3600000,
                "accepted_after_native_worker_exit": True, "quality_established": False, "auto_activate_voice": False})
            event = {"phase": "personal_session_accepted", "selection_root": str(output / "selection"), "selection_sha256": digest, "stop_reason": "target_epoch"}
        if script == self.stop_after:
            (stop_root / "STOP").write_bytes(b"stop")
        return StageResult([event])


class LocalWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(run_workflow, "workflow adapter not implemented")
        self.temp = tempfile.TemporaryDirectory(prefix="local-workflow-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "source.wav"
        count = 300 * 48000 * 2
        with self.source.open("wb") as stream:
            stream.write(struct.pack("<4sI4s4sIHHIIHH4sI", b"RIFF", count + 36, b"WAVE", b"fmt ", 16, 1, 1, 48000, 96000, 2, 16, b"data", count))
            stream.truncate(count + 44)
        source_sha = hashlib.sha256(self.source.read_bytes()).hexdigest()
        runtime = {"python_path": sys.executable, "powershell_path": sys.executable}
        for key in ("scripts_root", "decoder_root", "source_root", "base_asset_root", "feature_asset_root"):
            directory = self.root / key
            directory.mkdir()
            runtime[key] = str(directory)
        self.output = self.root / "output"; self.output.mkdir()
        self.request = {"format": 1, "purpose": "local-personal-training-workflow", "source": {"path": str(self.source), "sha256": source_sha, "consent_confirmed": True},
            "runtime": runtime, "previous": None, "output_root": str(self.output)}
        self.request_path = self.root / "request.json"
        self.sha = write_json(self.request_path, self.request)

    def run_action(self, stages, events):
        return run_workflow(self.request_path, self.sha, self.output, runner=stages, emit=events.append)

    def test_known_request_digest_and_consent_are_required_before_stages(self):
        with self.assertRaises(WorkflowError):
            read_request(self.request_path, "a" * 64, self.output)
        self.request["source"]["consent_confirmed"] = False
        self.sha = write_json(self.request_path, self.request)
        with self.assertRaises(WorkflowError):
            read_request(self.request_path, self.sha, self.output)
        self.assertEqual([], list(self.output.iterdir()))

    def test_stop_before_first_stage_publishes_paused_state_without_model(self):
        (self.output / "STOP").write_bytes(b"stop")
        stages, events = InertStages(), []
        control = self.run_action(stages, events)
        state = json.loads(Path(control["state_path"]).read_bytes())
        self.assertEqual([], stages.calls)
        self.assertEqual("user_stop", control["stop_reason"])
        self.assertEqual(0, state["global_step"])
        self.assertIsNone(state["accepted"])

    def test_full_action_saves_initial_receipt_before_session_and_returns_known_state(self):
        stages, events = InertStages(), []
        control = self.run_action(stages, events)
        self.assertEqual(3, control["global_step"])
        self.assertEqual(1, control["completed_epoch"])
        self.assertEqual("target_epoch", control["stop_reason"])
        state = json.loads(Path(control["state_path"]).read_bytes())
        self.assertEqual(self.request["source"], state["source"])
        self.assertEqual("local-personal-training-workflow-state", state["purpose"])
        self.assertEqual(2, stages.calls.count("Extract-RvcFeatures.py"))
        self.assertLess(stages.calls.index("Accept-RvcUpdate.py"), stages.calls.index("Train-RvcSession.py"))
        self.assertEqual(1, len([e for e in events if e["phase"] == "workflow_checkpoint"]))
        self.assertEqual(hashlib.sha256(Path(control["state_path"]).read_bytes()).hexdigest(), control["state_sha256"])

    def test_native_session_failure_retains_accepted_initial_state_without_adopting_candidate(self):
        stages, events = InertStages(fail_session=True), []
        with self.assertRaises(WorkflowError):
            self.run_action(stages, events)
        checkpoint = [e for e in events if e["phase"] == "workflow_checkpoint"][0]
        state = json.loads(Path(checkpoint["state_path"]).read_bytes())
        self.assertEqual(1, state["global_step"])
        self.assertTrue(Path(state["accepted"]["receipt"]["path"]).exists())
        self.assertNotIn("Accept-RvcSession.py", stages.calls)

    def test_stop_between_prepare_stages_starts_no_feature_extraction(self):
        stages, events = InertStages(stop_after="Prepare-RvcDataset.py"), []
        control = self.run_action(stages, events)
        self.assertEqual(["Prepare-RvcDataset.py"], stages.calls)
        self.assertEqual(0, control["global_step"])

    def test_resume_uses_exact_accepted_point_and_never_prepares_again(self):
        first = self.run_action(InertStages(fail_session=False), [])
        output = self.root / "resume"; output.mkdir()
        self.request["output_root"] = str(output)
        self.request["previous"] = {"path": first["state_path"], "sha256": first["state_sha256"]}
        self.sha = write_json(self.request_path, self.request)
        self.output = output
        stages, events = InertStages(), []
        control = self.run_action(stages, events)
        self.assertEqual(["Train-RvcSession.py", "Accept-RvcSession.py"], stages.calls)
        self.assertEqual(6, control["global_step"])

    def test_resume_rejects_changed_source_or_unaccepted_receipt(self):
        first = self.run_action(InertStages(), [])
        output = self.root / "resume"; output.mkdir()
        self.request["output_root"] = str(output)
        self.request["previous"] = {"path": first["state_path"], "sha256": first["state_sha256"]}
        self.sha = write_json(self.request_path, self.request)
        self.output = output
        state = json.loads(Path(first["state_path"]).read_bytes())
        receipt = Path(state["accepted"]["receipt"]["path"])
        receipt.write_bytes(b"unaccepted")
        stages = InertStages()
        with self.assertRaises(WorkflowError):
            self.run_action(stages, [])
        self.assertEqual([], stages.calls)

    def test_changed_source_bytes_are_rejected_before_resume(self):
        first = self.run_action(InertStages(), [])
        output = self.root / "resume"; output.mkdir()
        self.request["output_root"] = str(output)
        self.request["previous"] = {"path": first["state_path"], "sha256": first["state_sha256"]}
        self.sha = write_json(self.request_path, self.request)
        self.output = output
        with self.source.open("r+b") as stream:
            stream.seek(100); stream.write(b"changed")
        stages = InertStages()
        with self.assertRaises(WorkflowError):
            self.run_action(stages, [])
        self.assertEqual([], stages.calls)

    def test_stop_before_session_first_update_returns_prior_accepted_point_paused(self):
        class NoUpdates(InertStages):
            def run(inner, script, arguments, **options):
                if script == "Train-RvcSession.py":
                    (options["stop_root"] / "STOP").write_bytes(b"stop")
                    error = WorkflowError("workflow.stage.failed")
                    error.native_code = "training.session.no_updates"
                    raise error
                return super().run(script, arguments, **options)
        control = self.run_action(NoUpdates(), [])
        self.assertEqual("user_stop", control["stop_reason"])
        self.assertEqual(1, control["global_step"])

    def test_stop_while_parent_start_gate_waits_creates_no_child_or_model(self):
        (self.output / "STOP").write_bytes(b"stop")
        stages = InertStages()
        with patch.dict("os.environ", {"LAS_WORKFLOW_START_GATE": str(self.output / "START")}):
            control = self.run_action(stages, [])
        self.assertEqual("user_stop", control["stop_reason"])
        self.assertEqual([], stages.calls)

    def test_cli_stop_returns_exact_state_control_without_importing_models(self):
        (self.output / "STOP").write_bytes(b"stop")
        script = Path(__file__).resolve().parents[3] / "scripts/training/Run-RvcWorkflow.py"
        result = subprocess.run([sys.executable, "-I", str(script), "--request", str(self.request_path),
            "--request-sha256", self.sha, "--output-root", str(self.output)], capture_output=True, timeout=30)
        self.assertEqual(0, result.returncode)
        control = json.loads(result.stdout)
        self.assertEqual("workflow_control", control["phase"])
        self.assertEqual(0, control["global_step"])
        self.assertEqual("user_stop", control["stop_reason"])
        self.assertEqual(str(self.output / "state/workflow_state.json"), control["state_path"])


class OwnedStageProcessTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(run_workflow, "workflow adapter not implemented")
        self.temp = tempfile.TemporaryDirectory(prefix="owned-stage-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.scripts = self.root / "scripts"; self.scripts.mkdir()
        self.runtime = {"python_path": sys.executable, "scripts_root": str(self.scripts)}

    def script(self, content):
        (self.scripts / "Prepare-RvcDataset.py").write_text(content, encoding="utf-8")

    def test_confirmed_success_returns_control_and_public_progress_never_contains_path(self):
        self.script('import json\nprint(json.dumps({"phase":"prepared","global_step":1,"private_path":"secret"}), flush=True)\n')
        events = []
        result = OwnedStageRunner(self.runtime, events.append).run("Prepare-RvcDataset.py", [], stop_root=self.root)
        self.assertEqual(1, result.events[0]["global_step"])
        self.assertEqual([{"phase": "prepared", "global_step": 1}], events)

    def test_timeout_terminates_exact_owned_descendant_before_it_can_publish(self):
        marker = self.root / "descendant-marker"
        descendant = "import pathlib,time;time.sleep(1.5);pathlib.Path(" + repr(str(marker)) + ").write_text('unexpected')"
        self.script("import subprocess,sys,time\nsubprocess.Popen([sys.executable,'-c'," + repr(descendant) + "])\ntime.sleep(30)\n")
        with self.assertRaises(WorkflowError) as error:
            OwnedStageRunner(self.runtime).run("Prepare-RvcDataset.py", [], stop_root=self.root, deadline_ms=250)
        self.assertEqual("workflow.stage.timeout", error.exception.code)
        time.sleep(1.6)
        self.assertFalse(marker.exists())

    def test_nonzero_child_output_is_not_an_accepted_stage(self):
        self.script('import json,sys\nprint(json.dumps({"phase":"prepared"}), flush=True)\nsys.exit(1)\n')
        with self.assertRaises(WorkflowError):
            OwnedStageRunner(self.runtime).run("Prepare-RvcDataset.py", [], stop_root=self.root)

    def test_repeated_arbitrary_session_output_cannot_renew_progress_watchdog(self):
        (self.scripts / "Train-RvcSession.py").write_text('import json\nprint(json.dumps({"phase":"heartbeat"}), flush=True)\n', encoding="utf-8")
        with self.assertRaises(WorkflowError) as error:
            OwnedStageRunner(self.runtime).run("Train-RvcSession.py", [], stop_root=self.root, session_root=self.root)
        self.assertEqual("workflow.session.progress.invalid", error.exception.code)

    def test_long_action_path_uses_short_owned_cache_for_numba(self):
        output = self.root / ("длинный-путь-записи-" + "x" * 100)
        output.mkdir()
        self.script('import json,os\nprint(json.dumps({"phase":"prepared","cache_length":len(os.environ["NUMBA_CACHE_DIR"])}), flush=True)\n')
        result = OwnedStageRunner(self.runtime).run("Prepare-RvcDataset.py", [], stop_root=output)
        self.assertLessEqual(result.events[0]["cache_length"], 128)

    def test_valid_session_transitions_return_only_private_final_control_after_exit(self):
        events = [{"phase": "session_preparing"}]
        events.extend({"phase": "session_batch_ready", "batch_count": i, "total_batches": 3} for i in range(1, 4))
        events.append({"phase": "session_state_ready", "global_step": 1, "epoch_size": 3})
        for step, epoch, ordinal in ((2, 0, 2), (3, 1, 0)):
            events.extend([{"phase": "session_update_started", "global_step": step, "clip_id": "clip-000000"},
                {"phase": "session_update_complete", "global_step": step, "completed_epoch": epoch, "next_ordinal": ordinal, "loss": 1.0}])
        events.extend([{"phase": "session_save_started", "global_step": 3, "point_count": 1},
            {"phase": "session_save_complete", "global_step": 3, "point_count": 1},
            {"phase": "personal_session_control", "request_sha256": "a" * 64, "journal_sha256": "b" * 64,
                "global_step": 3, "completed_updates": 2, "point_count": 1, "stop_reason": "target_epoch"}])
        (self.scripts / "Train-RvcSession.py").write_text("import json\nfor event in " + repr(events) + ":\n print(json.dumps(event), flush=True)\n", encoding="utf-8")
        public = []
        result = OwnedStageRunner(self.runtime, public.append).run("Train-RvcSession.py", [], stop_root=self.root, session_root=self.root)
        self.assertEqual("personal_session_control", result.events[-1]["phase"])
        self.assertEqual(3, result.events[-1]["global_step"])
        self.assertNotIn("personal_session_control", [event["phase"] for event in public])

    def test_native_no_updates_error_is_preserved_after_confirmed_exit_and_stop_propagates(self):
        session = self.root / "session"; session.mkdir()
        (self.root / "STOP").write_bytes(b"stop")
        marker = self.root / "propagated-stop"
        script = "import json,pathlib,sys,time\nprint(json.dumps({'phase':'session_preparing'}),flush=True)\n"
        script += "until=time.monotonic()+3\nwhile not pathlib.Path(" + repr(str(session / "STOP")) + ").exists():\n if time.monotonic()>until: sys.exit(2)\n time.sleep(.01)\n"
        script += "pathlib.Path(" + repr(str(marker)) + ").write_text('seen')\nprint(json.dumps({'phase':'failed','code':'training.session.no_updates'}),flush=True)\nsys.exit(1)\n"
        (self.scripts / "Train-RvcSession.py").write_text(script, encoding="utf-8")
        with self.assertRaises(WorkflowError) as error:
            OwnedStageRunner(self.runtime).run("Train-RvcSession.py", [], stop_root=self.root, session_root=session)
        self.assertEqual("training.session.no_updates", error.exception.native_code)
        self.assertTrue(marker.exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
