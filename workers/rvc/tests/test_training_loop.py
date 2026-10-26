"""Breaks caught: half-epoch publication, repeated saved epochs, stale LR."""
from pathlib import Path
import sys
import tempfile
import unittest

import torch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointStore, RunIdentity, CheckpointError
from las_rvc.training_state import TrainingState
from training_fixture import seed_all, make_state, update
try:
    from las_rvc.training_loop import train_epochs, EpochCanceled
except ModuleNotFoundError:
    train_epochs = EpochCanceled = None


class TrainingLoopTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(train_epochs, "Persistent epoch/pause/resume driver is not implemented")
        parent = Path(__file__).resolve().parents[3] / ".artifacts" / "rvc-state-tests"
        parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="loop-", dir=parent)
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name) / "run"
        root.mkdir()
        identity = RunIdentity("1" * 32, "2" * 64, "3" * 64, "4" * 64,
                               "81eed5e8f68b6bed1789f682fe78cdd324495afc",
                               "rvc-cpu-dev-v1", 1, 40000, True)
        self.store = CheckpointStore(root, identity)
        self.addCleanup(self.store.close)
        seed_all(947)
        self.state = make_state(TrainingState)
        self.requested = "run"
        self.events, self.epochs = [], []

    def epoch(self, epoch, step, should_cancel):
        self.epochs.append(epoch)
        if should_cancel():
            raise EpochCanceled()
        update(self.state)
        return step + 1

    def run_training(self, total=2, epoch=None):
        return train_epochs(self.state, self.store, epoch or self.epoch, total,
                            lambda: self.requested, self.events.append)

    def test_pause_saves_after_both_schedulers_and_resume_starts_next_epoch(self):
        def pause_during_epoch(epoch, step, should_cancel):
            result = self.epoch(epoch, step, should_cancel)
            self.requested = "pause"
            return result
        result = self.run_training(epoch=pause_during_epoch)
        self.assertEqual(("paused", 1, 1), (result.status, result.completed_epoch, result.global_step))
        point = self.store.latest()
        with point.open_payload("training.pt") as stream:
            saved = torch.load(stream, weights_only=True, map_location="cpu")
        self.assertEqual(1, saved["scheduler_g"]["last_epoch"])
        self.assertEqual(1, saved["scheduler_d"]["last_epoch"])
        self.assertEqual(2, saved["sampler_next_epoch"])
        self.requested = "run"
        self.state = make_state(TrainingState)
        resumed = self.run_training()
        self.assertEqual([1, 2], self.epochs)
        self.assertEqual(("completed", 2, 2), (resumed.status, resumed.completed_epoch, resumed.global_step))
        self.assertEqual(2, self.store.latest().completed_epoch)
        self.assertTrue(all(set(event) == {"phase", "completed", "total"} for event in self.events))

    def test_cancel_does_not_publish_partial_epoch(self):
        self.run_training(total=1)
        first = self.store.latest()
        def cancel_in_partial_epoch(epoch, step, should_cancel):
            self.epoch(epoch, step, should_cancel)
            self.requested = "cancel"
            if should_cancel():
                raise EpochCanceled()
            raise AssertionError("Cancel was not observed at batch boundary")
        result = self.run_training(total=3, epoch=cancel_in_partial_epoch)
        self.assertEqual(("canceled", 1, 1), (result.status, result.completed_epoch, result.global_step))
        self.assertEqual(first.directory, self.store.latest().directory)
        self.assertEqual([1, 2], self.epochs)
        self.assertEqual(1, len(list((self.store.root / "checkpoints").iterdir())))

    def test_failed_epoch_preserves_previous_point(self):
        self.run_training(total=1)
        first = self.store.latest()
        def fail_mid_epoch(epoch, step, should_cancel):
            self.epoch(epoch, step, should_cancel)
            raise RuntimeError("private path must not escape")
        with self.assertRaises(CheckpointError) as raised:
            self.run_training(total=3, epoch=fail_mid_epoch)
        self.assertEqual("training.epoch.failed", raised.exception.code)
        self.assertNotIn("private path", str(raised.exception))
        self.assertEqual(first.directory, self.store.latest().directory)

    def test_periodic_checkpoint_and_completed_save(self):
        result = self.run_training(total=21)
        self.assertEqual(("completed", 21, 21), (result.status, result.completed_epoch, result.global_step))
        self.assertEqual(["epoch-000020", "epoch-000021"],
                         sorted(p.name for p in (self.store.root / "checkpoints").iterdir()))
        self.assertEqual(list(range(1, 22)), self.epochs)
        self.assertEqual(21, self.state.scheduler_g.last_epoch)
        self.assertEqual(21, self.state.scheduler_d.last_epoch)

    def test_pause_between_epochs_saves_completed_unsaved_work(self):
        def request_pause_at_completed_report(event):
            self.events.append(event)
            if event == {"phase": "training", "completed": 1, "total": 3}:
                self.requested = "pause"
        result = train_epochs(self.state, self.store, self.epoch, 3,
                              lambda: self.requested, request_pause_at_completed_report)
        self.assertEqual(("paused", 1, 1), (result.status, result.completed_epoch, result.global_step))
        self.assertIsNotNone(result.checkpoint, "Pause must not lose the completed epoch between reports")
        self.assertEqual(2, result.checkpoint.next_epoch)
        self.requested = "run"
        self.state = make_state(TrainingState)
        self.run_training(total=3)
        self.assertEqual([1, 2, 3], self.epochs)

    def test_corrupt_final_checkpoint_rolls_back_and_replays_successfully(self):
        self.run_training(total=1)
        self.run_training(total=2)
        bad = self.store.latest()
        (bad.directory / "generator.pt").write_bytes(b"bad final point retained")
        self.state = make_state(TrainingState)
        try:
            recovered = self.run_training(total=2)
        except CheckpointError:
            recovered = None
        self.assertIsNotNone(recovered, "A rollback must be able to complete the replayed epoch")
        self.assertEqual(("completed", 2, 2), (recovered.status, recovered.completed_epoch, recovered.global_step))
        self.assertEqual([1, 2, 2], self.epochs)
        self.assertEqual(b"bad final point retained", (bad.directory / "generator.pt").read_bytes())


if __name__ == "__main__":
    unittest.main()
