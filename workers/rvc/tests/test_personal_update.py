"""Pure cursor and fail-closed metadata tests; never personal/native ML proof."""
from pathlib import Path
import hashlib
import json
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointError
from las_rvc.feature_corpus import CorpusRef
try:
    from las_rvc.personal_update import choose_training_clip, advance_update_cursor, TrainingInputs, verify_admitted_training
except ImportError:
    choose_training_clip = advance_update_cursor = TrainingInputs = verify_admitted_training = None
try:
    from las_rvc.personal_update import make_update_control, publish_update_acceptance
except ImportError:
    make_update_control = publish_update_acceptance = None
try:
    from las_rvc.personal_update import plan_training_window, should_start_window_update, read_expected_window
except ImportError:
    plan_training_window = should_start_window_update = read_expected_window = None


class PersonalUpdateTests(unittest.TestCase):
    def test_two_update_window_order_rollover_limit_and_conservative_reserve(self):
        self.assertIsNotNone(plan_training_window, "Bounded training window is not implemented")
        ids = [f"clip-{i:06d}" for i in range(30)]
        window = plan_training_window(ids, 0, 29, 1234, 2)
        self.assertEqual([(0, 29), (1, 0)], [(e, o) for _, e, o in window])
        self.assertEqual(choose_training_clip(ids, 1, 0, 1234), window[1][0])
        self.assertEqual(1, len(plan_training_window(ids, 199, 29, 1234, 2)))
        for cap in (0, 3, True):
            with self.assertRaises(CheckpointError): plan_training_window(ids, 0, 0, 1234, cap)
        self.assertTrue(should_start_window_update(24.999, 1))
        self.assertFalse(should_start_window_update(25, 1))
        self.assertFalse(should_start_window_update(25, 0))
        for elapsed in (-1, float("nan"), float("inf"), True):
            with self.assertRaises(CheckpointError): should_start_window_update(elapsed, 0)

    def test_every_clip_once_per_epoch_and_initial_cursor_does_not_skip(self):
        self.assertIsNotNone(choose_training_clip, "Personal update cursor is not implemented")
        ids = [f"clip-{i:06d}" for i in range(30)]
        first = [choose_training_clip(ids, 0, i, 1234) for i in range(30)]
        self.assertEqual(set(ids), set(first))
        self.assertEqual(first, [choose_training_clip(ids, 0, i, 1234) for i in range(30)])
        self.assertNotEqual(first, [choose_training_clip(ids, 1, i, 1234) for i in range(30)])
        self.assertEqual((0, 1, False), advance_update_cursor(0, 0, 30))
        self.assertEqual((1, 0, True), advance_update_cursor(0, 29, 30))
        self.assertEqual((200, 0, True), advance_update_cursor(199, 29, 30))

    def test_invalid_cursor_seed_membership_or_finished_run_fail(self):
        self.assertIsNotNone(choose_training_clip, "Personal update cursor is not implemented")
        ids = [f"clip-{i:06d}" for i in range(30)]
        for epoch, ordinal, seed in ((-1, 0, 1234), (200, 0, 1234), (0, 30, 1234), (0, 0, True)):
            with self.subTest(epoch=epoch, ordinal=ordinal, seed=seed), self.assertRaises(CheckpointError):
                choose_training_clip(ids, epoch, ordinal, seed)
        for invalid in (ids[::-1], ids + ids[:1], ["validation"]):
            with self.assertRaises(CheckpointError):
                choose_training_clip(invalid, 0, 0, 1234)
        with self.assertRaises(CheckpointError):
            advance_update_cursor(200, 0, 30)

    def test_missing_explicit_admission_fails_before_any_native_construction(self):
        self.assertIsNotNone(TrainingInputs, "Personal training input contract is not implemented")
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-feature-tests"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="personal-input-", dir=parent) as temporary:
            root = Path(temporary)
            inputs = TrainingInputs(root / "dataset", "a" * 64,
                CorpusRef(root / "preflight", "b" * 64), CorpusRef(root / "corpus", "c" * 64),
                CorpusRef(root / "candidate", "d" * 64), CorpusRef(root / "admission", "e" * 64),
                root / "source", root / "assets")
            with self.assertRaises(CheckpointError):
                with verify_admitted_training(inputs):
                    self.fail("Missing admission granted a training contract")

    def test_invalid_supervisor_receipt_or_self_admitting_candidate_rejects_before_source(self):
        self.assertIsNotNone(TrainingInputs, "Personal training input contract is not implemented")
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-feature-tests"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="personal-receipt-", dir=parent) as temporary:
            root = Path(temporary)
            from las_rvc.feature_models import _write_bytes
            def publish(name, value):
                directory = root / name
                directory.mkdir()
                raw = json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
                digest = hashlib.sha256(raw).hexdigest()
                _write_bytes(directory / "decision.json", raw)
                _write_bytes(directory / "READY", digest.encode() + b"\n")
                return CorpusRef(directory, digest)
            base = {"format": 1, "purpose": "experimental-native-rvc-training-candidate",
                "training_admitted": False, "experimental_opt_in": True, "auto_activate_voice": False,
                "quality_established": False, "speech_duration_verified": False}
            candidate = publish("candidate", base)
            for index, mutation in enumerate(("missing", "timeout", "exit", "deadline", "identity", "self-admit")):
                receipt = {"candidate_sha256": candidate.sha256, "native_worker_exit_code": 0,
                    "native_worker_timed_out": False, "native_worker_deadline_ms": 60000,
                    "accepted_after_native_worker_exit": True}
                selected_candidate = candidate
                if mutation == "timeout": receipt["native_worker_timed_out"] = True
                if mutation == "exit": receipt["native_worker_exit_code"] = 1
                if mutation == "deadline": receipt["native_worker_deadline_ms"] = 120000
                if mutation == "identity": receipt["candidate_sha256"] = "f" * 64
                if mutation == "self-admit": selected_candidate = publish("self-admit", {**base, "training_admitted": True})
                decision = {**base, "purpose": "experimental-native-rvc-training-admission", "training_admitted": True}
                if mutation != "missing": decision["supervisor_acceptance"] = receipt
                admission = publish(f"admission-{index}", decision)
                inputs = TrainingInputs(root / "dataset", "a" * 64,
                    CorpusRef(root / "preflight", "b" * 64), CorpusRef(root / "corpus", "c" * 64),
                    selected_candidate, admission, root / "source", root / "assets")
                with self.subTest(mutation=mutation), patch("las_rvc.personal_update.load_prepared_clips") as source:
                    with self.assertRaises(CheckpointError):
                        with verify_admitted_training(inputs): self.fail("Invalid receipt admitted training")
                    source.assert_not_called()

    def test_native_point_control_retains_known_identity_only_after_successful_supervision(self):
        self.assertIsNotNone(make_update_control, "Private checkpoint control is not implemented")
        self.assertIsNotNone(publish_update_acceptance, "Supervisor checkpoint receipt is not implemented")
        # Tiny real CPU optimizer fixture, NOT native RVC or personal voice proof.
        from las_rvc.checkpoints import RunIdentity
        from las_rvc.training_state import TrainingState
        from las_rvc.update_checkpoints import make_update_position, save_update_checkpoint
        from training_fixture import make_state, seed_all, update
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-feature-tests"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="personal-control-", dir=parent) as temporary:
            root = Path(temporary)
            ids = ["clip-000000", "clip-000001", "clip-000002"]
            from las_rvc.feature_models import _write_bytes
            admission_root = root / "admission"
            admission_root.mkdir()
            admission_raw = json.dumps({"format": 1, "purpose": "experimental-native-rvc-training-admission",
                "training_admitted": True, "split": {"train_clip_ids": ids}}, sort_keys=True, separators=(",", ":")).encode()
            admission_sha = hashlib.sha256(admission_raw).hexdigest()
            _write_bytes(admission_root / "decision.json", admission_raw)
            _write_bytes(admission_root / "READY", admission_sha.encode() + b"\n")
            supervision = {"request_sha256": "6" * 64, "expected_epoch_size": 3, "expected_previous": None}
            identity = RunIdentity("1" * 32, "2" * 64, "3" * 64, "4" * 64,
                "81eed5e8f68b6bed1789f682fe78cdd324495afc", "rvc-cpu-experimental-updates-v1", 1, 40000, True)
            seed_all(947)
            state = make_state(TrainingState)
            update(state)
            first_root = root / "first"
            first_root.mkdir()
            point = save_update_checkpoint(state, identity,
                make_update_position(0, 1, ids, 1234, admission_sha), ids, first_root)
            control = make_update_control(point)
            self.assertEqual(point.manifest_sha256, control["checkpoint_manifest_sha256"])
            self.assertEqual(str(point.directory), control["checkpoint_root"])
            for exit_code, timed_out in ((1, False), (0, True), (False, False)):
                with self.assertRaises(CheckpointError):
                    publish_update_acceptance(control, first_root, worker_exit_code=exit_code,
                        worker_timed_out=timed_out, deadline_ms=60000, **supervision)
                self.assertFalse((first_root / "accepted-update.json").exists())
            with self.assertRaises(CheckpointError):
                publish_update_acceptance({**control, "checkpoint_manifest_sha256": "f" * 64}, first_root,
                    worker_exit_code=0, worker_timed_out=False, deadline_ms=60000, **supervision)
            self.assertFalse((first_root / "accepted-update.json").exists())
            receipt = publish_update_acceptance(control, first_root,
                worker_exit_code=0, worker_timed_out=False, deadline_ms=60000, **supervision)
            self.assertEqual(point.manifest_sha256, receipt["point"]["checkpoint_manifest_sha256"])
            self.assertEqual(receipt, json.loads((first_root / "accepted-update.json").read_bytes()))

            window_root = root / "window"
            window_root.mkdir()
            update(state)
            point2 = save_update_checkpoint(state, identity,
                make_update_position(0, 2, ids, 1234, admission_sha), ids, window_root)
            control2 = make_update_control(point2, start_global_step=0, completed_updates=2)
            for start, cap in ((0, 1), (1, 2), (False, 2), (0, True)):
                with self.subTest(start=start, cap=cap), self.assertRaises(CheckpointError):
                    publish_update_acceptance(control2, window_root, worker_exit_code=0,
                        worker_timed_out=False, deadline_ms=60000, expected_start_step=start, max_updates=cap, **supervision)
                self.assertFalse((window_root / "accepted-update.json").exists())
            with self.assertRaises(CheckpointError):
                publish_update_acceptance(control2, window_root, worker_exit_code=0, worker_timed_out=False,
                    deadline_ms=60000, max_updates=2, **{**supervision, "expected_epoch_size": 2})
            result = publish_update_acceptance(control2, window_root, worker_exit_code=0,
                worker_timed_out=False, deadline_ms=60000, expected_start_step=0, max_updates=2, **supervision)
            self.assertEqual(2, result["point"]["completed_updates"])

            request = {"format": 1, "purpose": "bounded-experimental-personal-native-window", "max_updates": 2,
                "run_id": identity.run_id, "inputs": {"admission": {"root": str(admission_root), "sha256": admission_sha}},
                "output_root": str(window_root), "previous": {"root": str(point.directory), "sha256": point.manifest_sha256}}
            from las_rvc.feature_models import _write_bytes
            def known_request(name, value):
                raw = json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
                path = root / name
                _write_bytes(path, raw)
                return path, hashlib.sha256(raw).hexdigest()
            path, sha = known_request("request.json", request)
            _, start, cap, size = read_expected_window(path, sha, window_root)
            self.assertEqual((1, 2), (start, cap))
            self.assertEqual(3, size)
            for index, mutation in enumerate(({"max_updates": True}, {"max_updates": 3}, {"run_id": "f" * 32},
                    {"previous": {"root": str(point.directory), "sha256": "f" * 64}}, {"output_root": str(root)})):
                path, sha = known_request(f"bad-request-{index}.json", {**request, **mutation})
                with self.subTest(mutation=mutation), self.assertRaises(CheckpointError):
                    read_expected_window(path, sha, window_root)
            for index, mutation in enumerate(({"global_step": 999}, {"epoch_size": 2}, {"next_ordinal": 3})):
                forged_root = root / f"forged-predecessor-{index}"
                forged_root.mkdir()
                forged = json.loads((point.directory / "manifest.json").read_bytes())
                forged["position"].update(mutation)
                raw = json.dumps(forged, sort_keys=True, separators=(",", ":")).encode()
                forged_sha = hashlib.sha256(raw).hexdigest()
                _write_bytes(forged_root / "manifest.json", raw)
                _write_bytes(forged_root / "READY", forged_sha.encode() + b"\n")
                path, sha = known_request(f"forged-request-{index}.json", {**request,
                    "previous": {"root": str(forged_root), "sha256": forged_sha}})
                with self.subTest(mutation=mutation), self.assertRaises(CheckpointError):
                    read_expected_window(path, sha, window_root)

            rollover_root = root / "rollover"
            rollover_root.mkdir()
            update(state)
            state.scheduler_g.step()
            state.scheduler_d.step()
            point3 = save_update_checkpoint(state, identity,
                make_update_position(1, 0, ids, 1234, admission_sha), ids, rollover_root)
            control3 = make_update_control(point3, start_global_step=1, completed_updates=2)
            predecessor = {"root": str(point.directory), "sha256": point.manifest_sha256}
            rollover = publish_update_acceptance(control3, rollover_root, worker_exit_code=0, worker_timed_out=False,
                deadline_ms=60000, expected_start_step=1, max_updates=2, **{**supervision, "expected_previous": predecessor})
            self.assertEqual((1, 0), (rollover["point"]["completed_epoch"], rollover["point"]["next_ordinal"]))
            self.assertEqual(predecessor, rollover["previous_checkpoint"])


if __name__ == "__main__":
    unittest.main()
