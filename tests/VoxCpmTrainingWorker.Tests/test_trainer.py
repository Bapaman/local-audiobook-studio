import tempfile
import types
import unittest
from pathlib import Path
from unittest.mock import patch
import contextlib
import math
import fixtures
import protocol
import trainer


class TrainerTests(unittest.TestCase):
    def test_optimizer_step_precedes_scheduler_and_zero_grad_precedes_save(self):
        for bad in (None, "loss", "gradient", "parameter", "moment"):
            with self.subTest(bad=bad):
                events = []
                class Scalar:
                    def __init__(self, value):
                        self.value = value
                        self.grad = None
                    def __add__(self, other):
                        return Scalar(self.value + other.value)
                    def detach(self):
                        return self
                    def clone(self):
                        return Scalar(self.value)
                    def backward(self):
                        events.append("backward")
                        p.grad = Scalar(float("nan") if bad == "gradient" else 0.1)
                    def __float__(self):
                        return float(self.value)
                p = Scalar(1)
                p.grad = None
                def step():
                    events.append("optimizer")
                    p.value = float("nan") if bad == "parameter" else 2
                    optimizer.state[p] = dict(step=Scalar(1), exp_avg=Scalar(float("nan") if bad == "moment" else 0.1), exp_avg_sq=Scalar(0.1))
                optimizer = types.SimpleNamespace(state={}, step=step, zero_grad=lambda **kw: events.append("zero"))
                scheduler = types.SimpleNamespace(last_epoch=0)
                def scheduled():
                    events.append("scheduler")
                    scheduler.last_epoch += 1
                scheduler.step = scheduled
                torch = types.SimpleNamespace(equal=lambda a, b: a.value == b.value, nn=types.SimpleNamespace(utils=types.SimpleNamespace(clip_grad_norm_=lambda params, norm, foreach: Scalar(0.1))))
                losses = {"loss/diff": Scalar(float("nan") if bad == "loss" else 0.5), "loss/stop": Scalar(0.1)}
                with patch.object(trainer, "tensor_finite", side_effect=lambda tensor, lib: math.isfinite(tensor.value)):
                    if bad:
                        with self.assertRaises(protocol.ProtocolError):
                            trainer.apply_update(losses, [("adapter", p)], optimizer, scheduler, torch, lambda: False, 1)
                    else:
                        metrics = trainer.apply_update(losses, [("adapter", p)], optimizer, scheduler, torch, lambda: False, 1)
                        self.assertEqual(events, ["backward", "optimizer", "scheduler", "zero"])
                        self.assertAlmostEqual(metrics["loss"], 0.6)
                        events.append("save")
                        self.assertEqual(events[-2:], ["zero", "save"])
    def test_restore_happens_after_construction_before_full_text_packer_and_checkpoint(self):
        events = []
        engine = types.SimpleNamespace(
            restore=lambda root: events.append("restore"),
            pack=lambda pcm, text: events.append(("pack", pcm, text)) or "batch",
            update=lambda batch, stopped: events.append("update") or dict(loss=0.5, gradientNorm=0.2),
            save=lambda: events.append("save") or {"payload": b"synthetic"})
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            value = fixtures.request(root)
            with patch.object(trainer, "_create_backend", side_effect=lambda r, p: events.append("construct") or engine), patch.object(trainer, "read_training_pcm", return_value=b"all samples"):
                candidate = trainer.train_one(value, lambda phase: None)
            self.assertEqual(events, ["construct", "restore", ("pack", b"all samples", "Это только синтетический пример."), "update", "save"])
            self.assertEqual(candidate["metrics"], dict(loss=0.5, gradientNorm=0.2))

    def test_nonfinite_loss_grad_or_tensor_never_publishes_ready(self):
        for metrics in (dict(loss=float("nan"), gradientNorm=1.0), dict(loss=1.0, gradientNorm=float("inf"))):
            saved = []
            engine = types.SimpleNamespace(restore=lambda root: None, pack=lambda pcm, text: None,
                                           update=lambda batch, stopped: metrics, save=lambda: saved.append("saved"))
            with tempfile.TemporaryDirectory() as directory, patch.object(trainer, "_create_backend", return_value=engine), patch.object(trainer, "read_training_pcm", return_value=b"all samples"):
                with self.assertRaises(protocol.ProtocolError):
                    trainer.train_one(fixtures.request(Path(directory)), lambda phase: None)
                self.assertEqual(saved, [])

    def test_stop_before_dependencies_and_after_restore_is_empty(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            value = fixtures.request(root)
            stop = root / "voxcpm-training" / "actions" / value["actionId"] / "executions" / value["executionId"] / "STOP"
            stop.parent.mkdir(parents=True)
            stop.write_bytes(b"")
            with patch.object(trainer, "_create_backend", side_effect=AssertionError("must not load")):
                self.assertIsNone(trainer.train_one(value, lambda phase: None))
            stop.unlink()
            engine = types.SimpleNamespace(restore=lambda root: stop.write_bytes(b""), pack=lambda pcm, text: self.fail("packer after stop"))
            with patch.object(trainer, "_create_backend", return_value=engine):
                self.assertIsNone(trainer.train_one(value, lambda phase: None))
