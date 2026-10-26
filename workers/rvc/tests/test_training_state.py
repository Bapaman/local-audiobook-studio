"""Breaks caught: resume reinitializing optimizer/scheduler/RNG or loading bad bytes."""
import copy
import random
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import numpy as np
import torch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointStore, RunIdentity, CheckpointError
from training_fixture import make_state, seed_all, update, step_schedulers
try:
    from las_rvc.training_state import TrainingState
except ModuleNotFoundError:
    TrainingState = None


class TrainingStateTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(TrainingState, "Full optimizer/scheduler/RNG state is not implemented")
        self.assertIsNone(torch.version.cuda, "Tests require a pure CPU Torch build")
        parent = Path(__file__).resolve().parents[3] / ".artifacts" / "rvc-state-tests"
        parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="state-", dir=parent)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "run"
        self.root.mkdir()
        identity = RunIdentity("1" * 32, "2" * 64, "3" * 64, "4" * 64,
                               "81eed5e8f68b6bed1789f682fe78cdd324495afc",
                               "rvc-cpu-dev-v1", 1, 40000, True)
        self.store = CheckpointStore(self.root, identity)
        self.addCleanup(self.store.close)

    def assert_state_equal(self, expected, actual):
        if isinstance(expected, torch.Tensor):
            self.assertTrue(torch.equal(expected, actual), f"Unequal tensors: {expected} vs {actual}")
        elif isinstance(expected, dict):
            self.assertEqual(set(expected), set(actual))
            for key in expected:
                self.assert_state_equal(expected[key], actual[key])
        elif isinstance(expected, (list, tuple)):
            self.assertEqual(len(expected), len(actual))
            for left, right in zip(expected, actual):
                self.assert_state_equal(left, right)
        else:
            self.assertEqual(expected, actual)

    def test_failed_kernel_state_cannot_be_checkpointed(self):
        state = make_state(TrainingState)
        state._training_failed = True
        with self.assertRaises(CheckpointError) as caught:
            state.save(self.store, 1, 1)
        self.assertEqual("training.state.failed", caught.exception.code)
        self.assertIsNone(self.store.latest())

    def test_restored_training_matches_uninterrupted_updates(self):
        seed_all(947)
        uninterrupted = make_state(TrainingState)
        update(uninterrupted)
        step_schedulers(uninterrupted)
        point = uninterrupted.save(self.store, 1, 1)
        update(uninterrupted)
        step_schedulers(uninterrupted)
        expected = [copy.deepcopy(x.state_dict()) for x in (
            uninterrupted.generator, uninterrupted.discriminator,
            uninterrupted.optimizer_g, uninterrupted.optimizer_d,
            uninterrupted.scheduler_g, uninterrupted.scheduler_d)]
        expected_rng = (random.random(), float(np.random.rand()), torch.rand(3))
        seed_all(404)
        resumed = make_state(TrainingState)
        position = resumed.restore(point)
        self.assertEqual((2, 1, 1234), (position.next_epoch, position.global_step, position.sampler_seed))
        update(resumed)
        step_schedulers(resumed)
        for expected_state, actual in zip(expected, (
                resumed.generator, resumed.discriminator, resumed.optimizer_g,
                resumed.optimizer_d, resumed.scheduler_g, resumed.scheduler_d)):
            self.assert_state_equal(expected_state, actual.state_dict())
        self.assertEqual(expected_rng[0], random.random())
        self.assertEqual(expected_rng[1], float(np.random.rand()))
        self.assertTrue(torch.equal(expected_rng[2], torch.rand(3)))

    def test_corrupt_payload_is_rejected_before_deserialization(self):
        seed_all(947)
        state = make_state(TrainingState)
        update(state)
        step_schedulers(state)
        point = state.save(self.store, 1, 1)
        (point.directory / "generator.pt").write_bytes(b"changed after publication")
        # Mock only the dangerous deserialization boundary. Actual store validation
        # must reject before this sentinel could ever be executed.
        with patch("torch.load", side_effect=AssertionError("Unvalidated bytes reached torch.load")):
            with self.assertRaises(CheckpointError) as raised:
                state.restore(point)
        self.assertEqual("training.checkpoint.invalid", raised.exception.code)

    def test_scheduler_optimizer_and_rng_are_restored(self):
        seed_all(113)
        original = make_state(TrainingState)
        update(original)
        step_schedulers(original)
        point = original.save(self.store, 1, 1)
        seed_all(888)
        restored = make_state(TrainingState)
        restored.restore(point)
        self.assertAlmostEqual(.0009, restored.optimizer_g.param_groups[0]["lr"], places=15)
        self.assertEqual(.0016, restored.optimizer_d.param_groups[0]["lr"])
        self.assertEqual(1, restored.scheduler_g.last_epoch)
        self.assertEqual(1, restored.scheduler_d.last_epoch)
        for optimizer in (restored.optimizer_g, restored.optimizer_d):
            self.assertTrue(optimizer.state)
            self.assertTrue(all(float(x["step"]) == 1 for x in optimizer.state.values()))
        with point.open_payload("training.pt") as stream:
            data = torch.load(stream, map_location="cpu", weights_only=True)
        self.assertEqual(2, data["next_epoch"])
        self.assertEqual(1234, data["sampler_seed"])
        self.assertEqual(1, data["global_step"])
        self.assertIn("python_rng", data)
        self.assertIn("numpy_rng", data)
        self.assertIn("torch_rng", data)
        self.assertEqual({}, data["scaler"])

    def test_integrity_valid_incomplete_optimizer_or_scheduler_is_rejected(self):
        seed_all(947)
        original = make_state(TrainingState)
        update(original)
        step_schedulers(original)
        point = original.save(self.store, 1, 1)
        payloads = {}
        for name in ("generator.pt", "discriminator.pt", "training.pt"):
            with point.open_payload(name) as stream:
                payloads[name] = torch.load(stream, weights_only=True, map_location="cpu")
        for index, mutation in enumerate(("scheduler", "optimizer_history", "moment_shape")):
            with self.subTest(mutation=mutation):
                changed = copy.deepcopy(payloads)
                if mutation == "scheduler":
                    changed["training.pt"]["scheduler_g"] = {}
                elif mutation == "optimizer_history":
                    changed["generator.pt"]["optimizer"]["state"] = {}
                else:
                    first_id = next(iter(changed["generator.pt"]["optimizer"]["state"]))
                    changed["generator.pt"]["optimizer"]["state"][first_id]["exp_avg"] = torch.zeros(1)
                new_root = Path(self.temp.name) / f"invalid-{index}"
                new_root.mkdir()
                with CheckpointStore(new_root, self.store.identity) as bad_store:
                    def write_bad(stage):
                        for name, data in changed.items():
                            with (stage / name).open("xb") as stream:
                                torch.save(data, stream)
                    bad = bad_store.publish(1, 1, write_bad)
                    fresh = make_state(TrainingState)
                    with self.assertRaises(CheckpointError) as raised:
                        fresh.restore(bad)
                    self.assertEqual("training.state.invalid", raised.exception.code)


if __name__ == "__main__":
    unittest.main()
