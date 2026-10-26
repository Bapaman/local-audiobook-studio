"""Synthetic metadata ledger boundaries, NOT native/trained voice proof."""
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointError
from las_rvc.feature_corpus import CorpusRef
from las_rvc.feature_models import _write_bytes
try:
    from las_rvc.continuation_ledger import ReceiptRef, create_continuation, reserve_window, accept_window, fail_window
except ImportError:
    ReceiptRef = create_continuation = reserve_window = accept_window = fail_window = None


class ContinuationLedgerTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(create_continuation, "Durable continuation ledger is not implemented")
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-feature-tests"
        parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="continuation-ledger-", dir=parent)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.owned = self.root / "continuation"
        self.owned.mkdir()
        self.protected = [CorpusRef(self.root / "proof1/checkpoint", "a" * 64),
            CorpusRef(self.root / "proof2/checkpoint", "b" * 64)]
        self.initial_control = {"phase": "personal_checkpoint_control", "checkpoint_root": str(self.protected[1].root),
            "checkpoint_manifest_sha256": "b" * 64, "run_id": "1" * 32, "admission_sha256": "2" * 64,
            "global_step": 2, "completed_epoch": 0, "next_ordinal": 2, "epoch_size": 125}

    def receipt(self, root, control, *, previous=None, new_contract=False):
        root.mkdir(parents=True, exist_ok=True)
        value = {"format": 1, "purpose": "supervisor-accepted-personal-native-update", "point": control,
            "native_worker_exit_code": 0, "native_worker_timed_out": False, "native_worker_deadline_ms": 60000,
            "accepted_after_native_worker_exit": True, "quality_established": False, "auto_activate_voice": False}
        if new_contract:
            value.update(format=2, request_sha256="d" * 64, previous_checkpoint=previous)
        raw = json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
        _write_bytes(root / "accepted-update.json", raw)
        return ReceiptRef(root / "accepted-update.json", hashlib.sha256(raw).hexdigest())

    def initial(self):
        ref = self.receipt(self.root / "proof2", self.initial_control)
        return create_continuation(self.owned, ref, "1" * 32, "2" * 64, self.protected)

    def window_receipt(self, reserved, count=2, *, legacy=False, predecessor=None):
        state = json.loads((reserved.root / "ledger.json").read_bytes())
        out = Path(state["windows"][-1]["output_root"])
        start = state["selected_point"]["global_step"]
        point = {**self.initial_control, "checkpoint_root": str(out / "checkpoint"),
            "global_step": start + count, "next_ordinal": start + count,
            "start_global_step": start, "completed_updates": count}
        checkpoint = out / "checkpoint"
        checkpoint.mkdir()
        manifest = {"format": 2, "purpose": "complete-update-checkpoint", "identity": {"run_id": "1" * 32},
            "position": {key: point[key] for key in ("global_step", "completed_epoch", "next_ordinal", "epoch_size", "admission_sha256")}}
        raw = json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode()
        point["checkpoint_manifest_sha256"] = hashlib.sha256(raw).hexdigest()
        _write_bytes(checkpoint / "manifest.json", raw)
        _write_bytes(checkpoint / "READY", point["checkpoint_manifest_sha256"].encode() + b"\n")
        for name in ("generator.pt", "discriminator.pt", "training.pt"):
            _write_bytes(checkpoint / name, b"synthetic metadata fixture, not a loadable or trained model")
        selected = state["selected_point"]
        previous = predecessor or {"root": selected["checkpoint_root"], "sha256": selected["checkpoint_manifest_sha256"]}
        return self.receipt(out, point, previous=previous, new_contract=not legacy)

    def test_accepted_contiguous_windows_select_only_after_receipt_and_stop_at_two_slots(self):
        initial = self.initial()
        reserved = reserve_window(initial)
        receipt = self.window_receipt(reserved)
        accepted = accept_window(reserved, receipt)
        value = json.loads((accepted.root / "ledger.json").read_bytes())
        self.assertEqual(4, value["selected_point"]["global_step"])
        self.assertEqual(reserved.sha256, value["previous_sha256"])
        self.assertEqual("accepted", value["windows"][0]["state"])
        self.assertEqual(2, json.loads((reserved.root / "ledger.json").read_bytes())["selected_point"]["global_step"])
        with self.assertRaises(CheckpointError): accept_window(reserved, receipt)
        reserved2 = reserve_window(accepted)
        accepted2 = accept_window(reserved2, self.window_receipt(reserved2, 1))
        value = json.loads((accepted2.root / "ledger.json").read_bytes())
        self.assertEqual(5, value["selected_point"]["global_step"])
        self.assertEqual(6 * 1024 ** 3, value["reserved_bytes"])
        with self.assertRaises(CheckpointError): reserve_window(accepted2)

    def test_failed_and_unselected_crash_windows_consume_slots_without_changing_model(self):
        reserved = reserve_window(self.initial())
        self.window_receipt(reserved)  # Worker success alone does not commit selection.
        self.assertEqual(2, json.loads((reserved.root / "ledger.json").read_bytes())["selected_point"]["global_step"])
        failed = fail_window(reserved)
        with self.assertRaises(CheckpointError): accept_window(failed, self.window_receipt)
        failed2 = fail_window(reserve_window(failed))
        value = json.loads((failed2.root / "ledger.json").read_bytes())
        self.assertEqual(2, value["selected_point"]["global_step"])
        self.assertEqual(["failed", "failed"], [window["state"] for window in value["windows"]])
        with self.assertRaises(CheckpointError): reserve_window(failed2)

    def test_new_selection_requires_exact_supervised_predecessor_not_only_step(self):
        reserved = reserve_window(self.initial())
        receipt = self.window_receipt(reserved, legacy=True)  # Legacy receipt has no exact predecessor proof.
        with self.assertRaises(CheckpointError): accept_window(reserved, receipt)

    def test_same_step_foreign_predecessor_cannot_select_model(self):
        reserved = reserve_window(self.initial())
        receipt = self.window_receipt(reserved,
            predecessor={"root": str(self.root / "other-at-step2/checkpoint"), "sha256": "c" * 64})
        with self.assertRaises(CheckpointError): accept_window(reserved, receipt)
        self.assertEqual(2, json.loads((reserved.root / "ledger.json").read_bytes())["selected_point"]["global_step"])

    def test_continuation_cannot_be_created_inside_a_protected_proof(self):
        ref = self.receipt(self.root / "proof2", self.initial_control)
        ancestor = self.root / "parent-proof/checkpoint"
        nested = ancestor / "continuation"
        nested.mkdir(parents=True)
        with self.assertRaises(CheckpointError):
            create_continuation(nested, ref, "1" * 32, "2" * 64,
                [CorpusRef(ancestor, "a" * 64), self.protected[1]])

    def test_reservation_is_durable_bounded_and_never_changes_original_selection(self):
        first = self.initial()
        before = (first.root / "ledger.json").read_bytes()
        reserved = reserve_window(first)
        state = json.loads((reserved.root / "ledger.json").read_bytes())
        self.assertEqual(2, state["selected_point"]["global_step"])
        self.assertEqual("reserved", state["windows"][0]["state"])
        self.assertEqual(2, state["windows"][0]["max_updates"])
        self.assertEqual(before, (first.root / "ledger.json").read_bytes())
        self.assertEqual([], list(Path(state["windows"][0]["output_root"]).iterdir()))
        with self.assertRaises(CheckpointError): reserve_window(reserved)
        with self.assertRaises(CheckpointError): reserve_window(CorpusRef(first.root, "f" * 64))

    def test_foreign_or_noncontiguous_receipt_cannot_select_model(self):
        first = self.initial()
        reserved = reserve_window(first)
        state = json.loads((reserved.root / "ledger.json").read_bytes())
        out = Path(state["windows"][0]["output_root"])
        bad = {**self.initial_control, "checkpoint_root": str(out / "checkpoint"),
            "run_id": "3" * 32, "global_step": 4, "next_ordinal": 4,
            "start_global_step": 2, "completed_updates": 2}
        receipt = self.receipt(out, bad)
        with self.assertRaises(CheckpointError): accept_window(reserved, receipt)
        self.assertEqual(2, json.loads((reserved.root / "ledger.json").read_bytes())["selected_point"]["global_step"])

    def test_protected_duplicate_or_owned_proof_points_are_rejected(self):
        ref = self.receipt(self.root / "proof2", self.initial_control)
        with self.assertRaises(CheckpointError):
            create_continuation(self.owned, ref, "1" * 32, "2" * 64, [self.protected[1]] * 2)
        with self.assertRaises(CheckpointError):
            create_continuation(self.owned, ref, "1" * 32, "2" * 64,
                [CorpusRef(self.owned / "forbidden/checkpoint", "a" * 64), self.protected[1]])


if __name__ == "__main__": unittest.main()
