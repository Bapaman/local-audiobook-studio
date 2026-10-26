"""Small protocol fixtures, never a personal/native voice quality proof."""
from dataclasses import asdict
import hashlib
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointError, RunIdentity, _json, _write_new
try:
    from las_rvc.personal_session import session_stop_reason, expected_position, accept_session, read_session_request
except ImportError:
    session_stop_reason = expected_position = accept_session = read_session_request = None


class PersonalSessionTests(unittest.TestCase):
    def test_native_session_bootstrap_accepts_only_its_valid_early_stop_marker(self):
        from las_rvc import persistent_training
        validate_session_bootstrap = getattr(persistent_training, "validate_session_bootstrap", None)
        self.assertIsNotNone(validate_session_bootstrap, "Native bootstrap must validate its own early STOP marker")
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-feature-tests"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="early-stop-", dir=parent) as directory:
            root = Path(directory)
            self.assertFalse(validate_session_bootstrap(root))
            (root / "STOP").write_bytes(b"stop\n")
            self.assertTrue(validate_session_bootstrap(root))
            (root / "foreign.bin").write_bytes(b"never adopt")
            with self.assertRaises(CheckpointError): validate_session_bootstrap(root)
            (root / "foreign.bin").unlink()
            (root / "STOP").write_bytes(b"x" * 65)
            with self.assertRaises(CheckpointError): validate_session_bootstrap(root)

    def test_point_budget_rejects_during_write_before_ready_or_rename(self):
        from las_rvc.training_state import TrainingState
        from las_rvc.update_checkpoints import make_update_position, save_update_checkpoint
        from training_fixture import make_state, seed_all, update
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-feature-tests"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="session-size-", dir=parent) as directory:
            root = Path(directory)
            seed_all(72)
            state = make_state(TrainingState)
            update(state)
            identity = RunIdentity("1" * 32, "2" * 64, "3" * 64, "4" * 64,
                "81eed5e8f68b6bed1789f682fe78cdd324495afc", "rvc-cpu-experimental-updates-v1", 1, 40000, True)
            ids = ["clip-000000", "clip-000001"]
            with self.assertRaises(CheckpointError):
                save_update_checkpoint(state, identity, make_update_position(0, 1, ids, 1234, "a" * 64), ids,
                    root, max_point_bytes=66000)
            self.assertFalse((root / "checkpoint").exists())
            files = list(root.glob("stage-*/*"))
            self.assertFalse(any(p.name == "READY" for p in files))
            self.assertLessEqual(sum(p.stat().st_size for p in files), 66000)

    def test_first_epoch_fits_four_publications_and_reserves_shutdown(self):
        self.assertIsNotNone(session_stop_reason, "Persistent session is not implemented")
        self.assertIsNone(session_stop_reason(124, 125, 3299, False, 3))
        self.assertEqual("target_epoch", session_stop_reason(125, 125, 10, False, 4))
        self.assertEqual("time_budget", session_stop_reason(20, 125, 3300, False, 0))
        self.assertEqual("user_stop", session_stop_reason(20, 125, 10, True, 0))
        self.assertEqual("checkpoint_budget", session_stop_reason(123, 125, 10, False, 4))
        for elapsed in (True, -1, float("nan")):
            with self.assertRaises(CheckpointError): session_stop_reason(3, 125, elapsed, False, 0)

    def fixture(self, root):
        ids = [f"clip-{i:06d}" for i in range(125)]
        admission = {"format": 1, "purpose": "experimental-native-rvc-training-admission", "training_admitted": True,
            "split": {"train_clip_ids": ids}, "input": {"corpus_manifest_sha256": "2" * 64},
            "runtime_sha256": "3" * 64, "config_sha256": "4" * 64,
            "runtime_identity": {"rvc_commit": "81eed5e8f68b6bed1789f682fe78cdd324495afc"},
            "preset": {"id": "rvc-cpu-experimental-updates-v1", "sampler_seed": 1234}}
        admission_root = root / "admission"
        admission_root.mkdir()
        raw = _json(admission)
        sha = hashlib.sha256(raw).hexdigest()
        _write_new(admission_root / "decision.json", raw)
        _write_new(admission_root / "READY", sha.encode() + b"\n")
        identity = asdict(RunIdentity("1" * 32, "2" * 64, "3" * 64, "4" * 64,
            admission["runtime_identity"]["rvc_commit"], admission["preset"]["id"], 1, 40000, True))
        def point(directory, step):
            directory.mkdir(parents=True)
            files = {}
            for name in ("generator.pt", "discriminator.pt", "training.pt"):
                data = (name + str(step)).encode()
                _write_new(directory / name, data)
                files[name] = {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
            raw = _json({"format": 2, "purpose": "complete-update-checkpoint", "identity": identity,
                "position": expected_position(step, ids, sha), "files": files})
            digest = hashlib.sha256(raw).hexdigest()
            _write_new(directory / "manifest.json", raw)
            _write_new(directory / "READY", digest.encode() + b"\n")
            return {"root": str(directory), "sha256": digest}
        previous = point(root / "old/checkpoint", 3)
        position = expected_position(3, ids, sha)
        old_control = {"phase": "personal_checkpoint_control", "checkpoint_root": previous["root"],
            "checkpoint_manifest_sha256": previous["sha256"], "run_id": identity["run_id"],
            **{k: position[k] for k in ("global_step", "completed_epoch", "next_ordinal", "epoch_size", "admission_sha256")},
            "start_global_step": 2, "completed_updates": 1}
        old_receipt = {"format": 2, "purpose": "supervisor-accepted-personal-native-update", "point": old_control,
            "request_sha256": "6" * 64, "previous_checkpoint": {"root": str(root / "older"), "sha256": "7" * 64},
            "native_worker_exit_code": 0, "native_worker_timed_out": False, "native_worker_deadline_ms": 60000,
            "accepted_after_native_worker_exit": True, "quality_established": False, "auto_activate_voice": False}
        receipt_raw = _json(old_receipt)
        receipt_path = root / "old/accepted-update.json"
        _write_new(receipt_path, receipt_raw)
        output = root / "session"
        output.mkdir()
        inputs = {"dataset_root": str(root / "dataset"), "dataset_sha256": "8" * 64,
            "preflight": {"root": str(root / "preflight"), "sha256": "9" * 64},
            "corpus": {"root": str(root / "corpus"), "sha256": "2" * 64},
            "candidate": {"root": str(root / "candidate"), "sha256": "a" * 64},
            "admission": {"root": str(admission_root), "sha256": sha},
            "source_root": str(root / "source"), "base_asset_root": str(root / "assets")}
        request = {"format": 1, "purpose": "persistent-personal-native-session", "run_id": identity["run_id"],
            "inputs": inputs, "output_root": str(output), "previous": previous,
            "previous_receipt": {"path": str(receipt_path), "sha256": hashlib.sha256(receipt_raw).hexdigest()},
            "target_completed_epoch": 1}
        request_path = root / "request.json"
        request_raw = _json(request)
        _write_new(request_path, request_raw)
        request_sha = hashlib.sha256(request_raw).hexdigest()
        entries, predecessor = [], previous
        for index, step in enumerate((43, 83, 123, 125), 1):
            ref = point(output / f"points/point-{index:03d}/checkpoint", step)
            entries.append({"point": ref, "previous": predecessor, "global_step": step})
            predecessor = ref
        journal = {"format": 1, "purpose": "personal-session-candidate-journal", "request_sha256": request_sha,
            "start_global_step": 3, "entries": entries, "stop_reason": "target_epoch"}
        _write_new(output / "journal.json", _json(journal))
        control = {"phase": "personal_session_control", "request_sha256": request_sha,
            "journal_sha256": hashlib.sha256(_json(journal)).hexdigest(), "global_step": 125,
            "completed_updates": 122, "point_count": 4, "stop_reason": "target_epoch"}
        return request_path, request_sha, output, control, entries

    def test_exact_request_bound_exit_zero_acceptance_and_selection(self):
        self.assertIsNotNone(accept_session, "Session acceptance is not implemented")
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-feature-tests"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="session-", dir=parent) as directory:
            path, sha, output, control, entries = self.fixture(Path(directory))
            request, start, ids, identity = read_session_request(path, sha, output)
            self.assertEqual(3, start)
            self.assertEqual(125, len(ids))
            for code, timeout in ((1, False), (0, True), (False, False)):
                with self.assertRaises(CheckpointError):
                    accept_session(path, sha, output, control, worker_exit_code=code, worker_timed_out=timeout, deadline_ms=3600000)
            for changed in ({**control, "completed_updates": 123}, {**control, "request_sha256": "f" * 64}):
                with self.assertRaises(CheckpointError):
                    accept_session(path, sha, output, changed, worker_exit_code=0, worker_timed_out=False, deadline_ms=3600000)
            self.assertFalse((output / "selection").exists())
            value, digest = accept_session(path, sha, output, control, worker_exit_code=0, worker_timed_out=False, deadline_ms=3600000)
            self.assertEqual(125, value["selected_point"]["global_step"])
            self.assertEqual(entries[-1]["point"]["sha256"], value["selected_point"]["checkpoint_manifest_sha256"])
            self.assertFalse(value["quality_established"])
            self.assertFalse(value["auto_activate_voice"])
            self.assertEqual(digest.encode() + b"\n", (output / "selection/READY").read_bytes())

    def test_tampered_payload_rejected_without_publishing_selection(self):
        self.assertIsNotNone(accept_session, "Session acceptance is not implemented")
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-feature-tests"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="session-tamper-", dir=parent) as directory:
            path, sha, output, control, entries = self.fixture(Path(directory))
            from las_rvc.feature_models import _write_bytes
            payload = Path(entries[-1]["point"]["root"]) / "generator.pt"
            # Explicit fixture corruption, not a native artifact.
            payload.unlink()
            _write_bytes(payload, b"wrong")
            with self.assertRaises(CheckpointError):
                accept_session(path, sha, output, control, worker_exit_code=0, worker_timed_out=False, deadline_ms=3600000)
            self.assertFalse((output / "selection").exists())

    def test_real_tiny_optimizers_rollover_pause_save_and_exact_resume(self):
        try:
            from las_rvc.persistent_training import run_session_updates
        except ImportError:
            run_session_updates = None
        self.assertIsNotNone(run_session_updates, "Persistent training loop is not implemented")
        from las_rvc.training_state import TrainingState
        from las_rvc.update_checkpoints import load_update_checkpoint, make_update_position, save_update_checkpoint
        from training_fixture import make_state, seed_all, update
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-feature-tests"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="session-loop-", dir=parent) as directory:
            root = Path(directory)
            ids, admission_sha = [f"clip-{i:06d}" for i in range(3)], "a" * 64
            identity = RunIdentity("1" * 32, "2" * 64, "3" * 64, "4" * 64,
                "81eed5e8f68b6bed1789f682fe78cdd324495afc", "rvc-cpu-experimental-updates-v1", 1, 40000, True)
            seed_all(72)
            state = make_state(TrainingState)
            update(state)
            original = root / "original"
            original.mkdir()
            first = save_update_checkpoint(state, identity, make_update_position(0, 1, ids, 1234, admission_sha), ids, original)
            original_ref = {"root": str(first.directory), "sha256": first.manifest_sha256}
            calls = []
            def train_step(current, clip):
                calls.append(clip)
                g, d = update(current)
                return {"loss_g": g, "loss_d": d}
            events = []
            out = root / "paused"
            out.mkdir()
            # Graceful pause immediately after one actual update, not a half update.
            result = run_session_updates(state, {i: i for i in ids}, ids, identity, admission_sha, original_ref,
                out, "b" * 64, 1, 3, train_step=train_step, emit=events.append,
                stop_requested=lambda: len(calls) == 1, elapsed=lambda: 0)
            self.assertEqual("user_stop", result["stop_reason"])
            self.assertEqual(2, result["global_step"])
            self.assertEqual(1, len(result["entries"]))
            self.assertEqual(0, state.scheduler_g.last_epoch)
            point_ref = result["entries"][-1]["point"]
            resumed = make_state(TrainingState)
            resumed.restore(load_update_checkpoint(Path(point_ref["root"]), point_ref["sha256"], identity, ids, admission_sha, 1234))
            out2 = root / "resumed"
            out2.mkdir()
            final = run_session_updates(resumed, {i: i for i in ids}, ids, identity, admission_sha, point_ref,
                out2, "c" * 64, 2, 3, train_step=train_step, elapsed=lambda: 0)
            self.assertEqual("target_epoch", final["stop_reason"])
            self.assertEqual(3, final["global_step"])
            self.assertEqual(1, resumed.scheduler_g.last_epoch)
            self.assertEqual(1, resumed.scheduler_d.last_epoch)
            from las_rvc.personal_update import choose_training_clip
            self.assertEqual([choose_training_clip(ids, 0, i, 1234) for i in (1, 2)], calls)
