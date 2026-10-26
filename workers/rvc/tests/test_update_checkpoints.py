"""Small real CPU optimizer/RNG proof, NOT native RVC or personal voice proof."""
import copy
import hashlib
import json
from pathlib import Path
import random
import sys
import tempfile
import unittest
from unittest.mock import patch

import numpy as np
import torch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointError, RunIdentity
from las_rvc.training_state import TrainingState
from training_fixture import make_state, seed_all, step_schedulers, update
try:
    from las_rvc.update_checkpoints import make_update_position, save_update_checkpoint, load_update_checkpoint
except ImportError:
    make_update_position = save_update_checkpoint = load_update_checkpoint = None


class UpdateCheckpointTests(unittest.TestCase):
    def setUp(self):
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-state-tests"
        parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="update-position-", dir=parent)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.identity = RunIdentity("1" * 32, "2" * 64, "3" * 64, "4" * 64,
            "81eed5e8f68b6bed1789f682fe78cdd324495afc", "rvc-cpu-dev-v1", 1, 40000, True)
        self.ids = ["clip-000000", "clip-000001", "clip-000002"]
        self.admission = "5" * 64

    def new_root(self, name):
        root = self.root / name
        root.mkdir()
        return root

    def position(self, epoch=0, ordinal=1):
        self.assertIsNotNone(make_update_position, "Update cursor is not implemented")
        return make_update_position(epoch, ordinal, self.ids, 1234, self.admission)

    def load(self, point, **changes):
        args = dict(root=point.directory, manifest_sha256=point.manifest_sha256,
            identity=self.identity, train_ids=self.ids, admission_sha256=self.admission, sampler_seed=1234)
        args.update(changes)
        return load_update_checkpoint(**args)

    def test_epoch_zero_first_update_resumes_models_optimizers_and_rng_exactly(self):
        self.assertIsNotNone(save_update_checkpoint, "Update checkpoint is not implemented")
        seed_all(947)
        uninterrupted = make_state(TrainingState)
        update(uninterrupted)
        point = save_update_checkpoint(uninterrupted, self.identity, self.position(), self.ids, self.new_root("point"))
        self.assertEqual((0, 1, 1), (point.completed_epoch, point.position.next_ordinal, point.global_step))
        self.assertEqual(0, uninterrupted.scheduler_g.last_epoch)
        update(uninterrupted)
        expected = [copy.deepcopy(x.state_dict()) for x in (uninterrupted.generator, uninterrupted.discriminator,
            uninterrupted.optimizer_g, uninterrupted.optimizer_d, uninterrupted.scheduler_g, uninterrupted.scheduler_d)]
        expected_rng = (random.random(), float(np.random.rand()), torch.rand(3))
        seed_all(404)
        resumed = make_state(TrainingState)
        resumed.restore(self.load(point))
        update(resumed)
        for want, actual in zip(expected, (resumed.generator, resumed.discriminator, resumed.optimizer_g,
                resumed.optimizer_d, resumed.scheduler_g, resumed.scheduler_d)):
            self.assert_tree_equal(want, actual.state_dict())
        self.assertEqual(expected_rng[0], random.random())
        self.assertEqual(expected_rng[1], float(np.random.rand()))
        self.assertTrue(torch.equal(expected_rng[2], torch.rand(3)))

    def test_restore_does_not_rehash_the_whole_point_for_each_payload(self):
        from las_rvc import update_checkpoints as module
        seed_all(947)
        state = make_state(TrainingState)
        update(state)
        point = save_update_checkpoint(state, self.identity, self.position(), self.ids, self.new_root("read-budget"))
        fresh = make_state(TrainingState)
        real_digest = module._digest
        bytes_hashed = []
        def counted_digest(stream):
            metadata = real_digest(stream)
            bytes_hashed.append(metadata["bytes"])
            return metadata
        with patch.object(module, "_digest", counted_digest):
            fresh.restore(point)
        # Actual hashes still run. Restore must read the complete payload set
        # once, not one full set plus its selected file for every torch.load.
        self.assertEqual(sum(info["bytes"] for info in point.files.values()), sum(bytes_hashed))
        self.assert_tree_equal(state.generator.state_dict(), fresh.generator.state_dict())
        self.assert_tree_equal(state.optimizer_d.state_dict(), fresh.optimizer_d.state_dict())

    def test_last_corrupt_payload_blocks_all_deserialization_and_state_changes(self):
        seed_all(947)
        state = make_state(TrainingState)
        update(state)
        point = save_update_checkpoint(state, self.identity, self.position(), self.ids, self.new_root("corrupt-last"))
        (point.directory / "training.pt").write_bytes(b"changed after initial verification")
        fresh = make_state(TrainingState)
        before = copy.deepcopy(fresh.generator.state_dict())
        with patch("torch.load") as deserialize:
            with self.assertRaises(CheckpointError): fresh.restore(point)
            deserialize.assert_not_called()
        self.assert_tree_equal(before, fresh.generator.state_dict())

    def assert_tree_equal(self, expected, actual):
        if isinstance(expected, torch.Tensor):
            self.assertTrue(torch.equal(expected, actual))
        elif isinstance(expected, dict):
            self.assertEqual(set(expected), set(actual))
            for key in expected:
                self.assert_tree_equal(expected[key], actual[key])
        elif isinstance(expected, (list, tuple)):
            self.assertEqual(len(expected), len(actual))
            for left, right in zip(expected, actual):
                self.assert_tree_equal(left, right)
        else:
            self.assertEqual(expected, actual)

    def test_epoch_boundary_requires_one_scheduler_step_not_child_count(self):
        self.assertIsNotNone(save_update_checkpoint, "Update checkpoint is not implemented")
        seed_all(113)
        state = make_state(TrainingState)
        for _ in self.ids:
            update(state)
        with self.assertRaises(CheckpointError):
            save_update_checkpoint(state, self.identity, self.position(1, 0), self.ids, self.new_root("wrong-scheduler"))
        step_schedulers(state)
        point = save_update_checkpoint(state, self.identity, self.position(1, 0), self.ids, self.new_root("whole-epoch"))
        fresh = make_state(TrainingState)
        fresh.restore(self.load(point))
        self.assertEqual((1, 3, 0), (point.completed_epoch, point.global_step, point.position.next_ordinal))
        self.assertEqual(1, fresh.scheduler_g.last_epoch)

    def test_changed_expected_identity_order_admission_or_digest_rejected_before_torch_load(self):
        self.assertIsNotNone(save_update_checkpoint, "Update checkpoint is not implemented")
        seed_all(947)
        state = make_state(TrainingState)
        update(state)
        point = save_update_checkpoint(state, self.identity, self.position(), self.ids, self.new_root("point"))
        for changes in ({"manifest_sha256": "c" * 64}, {"admission_sha256": "6" * 64},
                {"train_ids": self.ids[:2]}, {"sampler_seed": 4321}):
            with self.subTest(changes=list(changes)), patch("torch.load") as load:
                with self.assertRaises(CheckpointError):
                    self.load(point, **changes)
                load.assert_not_called()
        (point.directory / "generator.pt").write_bytes(b"changed")
        with patch("torch.load") as load, self.assertRaises(CheckpointError):
            self.load(point)
        load.assert_not_called()

    def test_discriminator_only_update_cannot_publish_complete_point(self):
        self.assertIsNotNone(save_update_checkpoint, "Update checkpoint is not implemented")
        seed_all(947)
        state = make_state(TrainingState)
        state.optimizer_d.zero_grad()
        state.discriminator(torch.ones(1, 2)).square().mean().backward()
        state.optimizer_d.step()
        root = self.new_root("incomplete-pair")
        with self.assertRaises(CheckpointError):
            save_update_checkpoint(state, self.identity, self.position(), self.ids, root)
        self.assertFalse((root / "checkpoint").exists())

    def test_cursor_arithmetic_and_reuse_fail_closed(self):
        self.assertIsNotNone(save_update_checkpoint, "Update checkpoint is not implemented")
        for epoch, ordinal in ((0, 0), (0, 3), (-1, 1), (201, 0), (True, 1)):
            with self.subTest(epoch=epoch, ordinal=ordinal), self.assertRaises(CheckpointError):
                self.position(epoch, ordinal)
        seed_all(947)
        state = make_state(TrainingState)
        update(state)
        output = self.new_root("point")
        point = save_update_checkpoint(state, self.identity, self.position(), self.ids, output)
        before = (point.directory / "manifest.json").read_bytes()
        with self.assertRaises(CheckpointError):
            save_update_checkpoint(state, self.identity, self.position(), self.ids, output)
        self.assertEqual(before, (point.directory / "manifest.json").read_bytes())

    def test_resealed_seed_or_generator_counter_mismatch_rejected_before_state_mutation(self):
        self.assertIsNotNone(save_update_checkpoint, "Update checkpoint is not implemented")
        seed_all(947)
        state = make_state(TrainingState)
        update(state)
        update(state)
        for mutation in ("sampler_seed", "generator_counter"):
            with self.subTest(mutation=mutation):
                point = save_update_checkpoint(state, self.identity, self.position(0, 2), self.ids,
                    self.new_root(mutation))
                target = "training.pt" if mutation == "sampler_seed" else "generator.pt"
                payload_path = point.directory / target
                with payload_path.open("rb") as stream:
                    payload = torch.load(stream, weights_only=True, map_location="cpu")
                if mutation == "sampler_seed":
                    payload["sampler_seed"] = 4321
                else:
                    for item in payload["optimizer"]["state"].values():
                        item["step"].fill_(1)
                with payload_path.open("wb") as stream:
                    torch.save(payload, stream)
                raw_payload = payload_path.read_bytes()
                manifest = json.loads((point.directory / "manifest.json").read_bytes())
                manifest["files"][target] = {"bytes": len(raw_payload),
                    "sha256": hashlib.sha256(raw_payload).hexdigest()}
                raw = json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode()
                (point.directory / "manifest.json").write_bytes(raw)
                known_sha = hashlib.sha256(raw).hexdigest()
                (point.directory / "READY").write_bytes(known_sha.encode() + b"\n")
                altered = self.load(point, manifest_sha256=known_sha)
                fresh = make_state(TrainingState)
                rng = torch.get_rng_state().clone()
                with patch.object(fresh.generator, "load_state_dict") as model_load, \
                        patch.object(fresh.optimizer_g, "load_state_dict") as optimizer_load:
                    with self.assertRaises(CheckpointError):
                        fresh.restore(altered)
                    model_load.assert_not_called()
                    optimizer_load.assert_not_called()
                self.assertTrue(torch.equal(rng, torch.get_rng_state()))


if __name__ == "__main__":
    unittest.main()
