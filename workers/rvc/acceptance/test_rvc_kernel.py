"""Explicit dependency acceptance: official base models, no personal recording."""
import os
from dataclasses import replace
import gc
import math
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

import torch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointError, CheckpointStore, RunIdentity
try:
    from las_rvc.rvc_kernel import create_rvc_state
except ModuleNotFoundError:
    create_rvc_state = None
try:
    from las_rvc.rvc_kernel import RvcBatch, train_rvc_batch
except ImportError:
    RvcBatch = train_rvc_batch = None

torch.set_num_threads(2)
torch.set_num_interop_threads(1)


class RvcKernelAcceptance(unittest.TestCase):
    state = None

    def setUp(self):
        self.assertIsNotNone(create_rvc_state, "Real pretrained RVC factory is not implemented")
        self.assertIsNone(torch.version.cuda, "Acceptance requires pure CPU Torch")
        self.source = Path(os.environ["LAS_RVC_SOURCE"])
        self.assets = Path(os.environ["LAS_RVC_ASSETS"])

    def get_state(self):
        if type(self).state is None:
            type(self).state = create_rvc_state(
                self.source, self.assets / "f0G40k.pth", self.assets / "f0D40k.pth")
        return type(self).state

    def test_creates_real_cpu_pretrained_state(self):
        state = self.get_state()
        self.assertEqual("SynthesizerTrnMs768NSFsid", type(state.generator).__name__)
        self.assertEqual("MultiPeriodDiscriminatorV2", type(state.discriminator).__name__)
        for model in (state.generator, state.discriminator):
            self.assertGreater(sum(p.numel() for p in model.parameters()), 10_000_000)
            for tensor in (*model.parameters(), *model.buffers()):
                self.assertEqual("cpu", tensor.device.type)
                if tensor.is_floating_point():
                    self.assertEqual(torch.float32, tensor.dtype)
                    self.assertTrue(bool(torch.isfinite(tensor).all()))
        for optimizer in (state.optimizer_g, state.optimizer_d):
            self.assertIsInstance(optimizer, torch.optim.AdamW)
            self.assertEqual(0.0001, optimizer.param_groups[0]["lr"])
            self.assertEqual((0.8, 0.99), optimizer.param_groups[0]["betas"])
            self.assertEqual(1e-9, optimizer.param_groups[0]["eps"])
            self.assertEqual({}, optimizer.state)
        self.assertEqual(0.999875, state.scheduler_g.gamma)
        self.assertEqual(0, state.scheduler_g.last_epoch)
        self.assertFalse(state.scaler.is_enabled())
        self.assertEqual(1234, state.sampler_seed)

    def test_tampered_asset_rejected_before_loading(self):
        parent = Path(__file__).resolve().parents[3] / ".artifacts" / "rvc-kernel-tests"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="bad-asset-", dir=parent) as directory:
            bad = Path(directory) / "f0G40k.pth"
            shutil.copyfile(self.assets / "f0G40k.pth", bad)
            with bad.open("r+b") as stream:
                byte = stream.read(1)
                stream.seek(0)
                stream.write(bytes([byte[0] ^ 1]))
            with patch("torch.load") as load, self.assertRaises(CheckpointError) as caught:
                create_rvc_state(self.source, bad, self.assets / "f0D40k.pth")
            self.assertEqual("training.asset.invalid", caught.exception.code)
            load.assert_not_called()

    def make_batch(self):
        self.assertIsNotNone(RvcBatch, "Native RVC batch contract is not implemented")
        self.assertIsNotNone(train_rvc_batch, "Real GAN update is not implemented")
        self.get_state()  # Verified, sealed source is imported before mel helpers.
        from train.mel_processing import spectrogram_torch
        torch.manual_seed(19)
        wave = (0.02 * torch.sin(torch.arange(12800) * (2 * math.pi * 220 / 40000))).reshape(1, 1, -1)
        return RvcBatch(
            phone=torch.randn(1, 32, 768) * 0.005,
            phone_lengths=torch.tensor([32]), pitch=torch.full((1, 32), 96, dtype=torch.int64),
            pitchf=torch.full((1, 32), 220.0),
            spec=spectrogram_torch(wave.squeeze(1), 2048, 40000, 400, 2048),
            spec_lengths=torch.tensor([32]), wave=wave,
            wave_lengths=torch.tensor([12800]), speaker_ids=torch.tensor([0]))

    def representative_weights(self, state):
        return (state.generator.enc_p.emb_phone.weight,
                state.discriminator.discriminators[0].convs[0].weight_v)

    def test_full_real_gan_update_changes_both_models(self):
        batch = self.make_batch()
        state = self.get_state()
        before = [parameter.detach().clone() for parameter in self.representative_weights(state)]
        losses = train_rvc_batch(state, batch)
        self.assertEqual({"generator_loss", "discriminator_loss"}, set(losses))
        self.assertTrue(all(math.isfinite(value) for value in losses.values()))
        for saved, current in zip(before, self.representative_weights(state)):
            self.assertFalse(torch.equal(saved, current), "Actual model weights must update")
        for model, optimizer in ((state.generator, state.optimizer_g),
                                 (state.discriminator, state.optimizer_d)):
            self.assertTrue(optimizer.state)
            self.assertTrue(all(float(item["step"]) == 1 for item in optimizer.state.values()))
            for parameter in model.parameters():
                self.assertEqual("cpu", parameter.device.type)
                self.assertEqual(torch.float32, parameter.dtype)
                self.assertTrue(bool(torch.isfinite(parameter).all()))
                if parameter.grad is not None:
                    self.assertTrue(bool(torch.isfinite(parameter.grad).all()))
        self.assertEqual(0, state.scheduler_g.last_epoch)
        self.assertEqual(0, state.scheduler_d.last_epoch)
        # Real native optimizer/model state, not just a toy checkpoint fixture.
        parent = Path(__file__).resolve().parents[3] / ".artifacts" / "rvc-kernel-tests"
        parent.mkdir(parents=True, exist_ok=True)
        identity = RunIdentity("a" * 32, "b" * 64, "c" * 64,
            "da8c508eef404263cedfe6a85fd5a0b7d631a0a29766b392df145e8ea0412fb8",
            "81eed5e8f68b6bed1789f682fe78cdd324495afc", "rvc-cpu-dev-v1", 1, 40000, True)
        with tempfile.TemporaryDirectory(prefix="native-state-", dir=parent) as directory:
            run_root = Path(directory) / "run"
            run_root.mkdir()
            with CheckpointStore(run_root, identity) as store:
                state.scheduler_g.step()
                state.scheduler_d.step()
                point = state.save(store, 1, 1)
                saved = [value.detach().clone() for value in self.representative_weights(state)]
                # Release full live gradients/models before constructing the resumed state.
                type(self).state = None
                state = None
                model = optimizer = parameter = current = None
                gc.collect()
                resumed = self.get_state()
                position = resumed.restore(point)
                self.assertEqual((2, 1), (position.next_epoch, position.global_step))
                self.assertEqual(1, resumed.scheduler_g.last_epoch)
                self.assertEqual(1, resumed.scheduler_d.last_epoch)
                for expected, actual in zip(saved, self.representative_weights(resumed)):
                    self.assertTrue(torch.equal(expected, actual))
                for optimizer in (resumed.optimizer_g, resumed.optimizer_d):
                    self.assertTrue(optimizer.state)
                    self.assertTrue(all(float(item["step"]) == 1 for item in optimizer.state.values()))

    def assert_rejected_without_update(self, batch):
        state = self.get_state()
        before = [parameter.detach().clone() for parameter in self.representative_weights(state)]
        with patch.object(state.optimizer_g, "step", wraps=state.optimizer_g.step) as g_step, \
                patch.object(state.optimizer_d, "step", wraps=state.optimizer_d.step) as d_step, \
                self.assertRaises(CheckpointError) as caught:
            train_rvc_batch(state, batch)
        self.assertEqual("training.batch.invalid", caught.exception.code)
        g_step.assert_not_called()
        d_step.assert_not_called()
        for saved, current in zip(before, self.representative_weights(state)):
            self.assertTrue(torch.equal(saved, current))

    def test_malformed_batch_cannot_update_weights(self):
        batch = self.make_batch()
        self.assert_rejected_without_update(replace(batch, phone=batch.phone[:, :, :767]))

    def test_nonfinite_input_rejected_before_update(self):
        batch = self.make_batch()
        phone = batch.phone.clone()
        phone[0, 0, 0] = float("nan")
        self.assert_rejected_without_update(replace(batch, phone=phone))


if __name__ == "__main__":
    unittest.main(verbosity=2)
