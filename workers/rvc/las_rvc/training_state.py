"""Full CPU training continuation using restricted, validated Torch payloads."""
from contextlib import ExitStack
from dataclasses import asdict, dataclass
import math
import random

import numpy as np
import torch

from .checkpoints import Checkpoint, CheckpointError, CheckpointStore


@dataclass(frozen=True)
class ResumePosition:
    next_epoch: int
    global_step: int
    sampler_seed: int


def _safe_tree(value, depth=0):
    if depth > 32:
        raise ValueError("state nesting")
    if isinstance(value, torch.Tensor):
        if value.device.type != "cpu" or value.layout != torch.strided:
            raise ValueError("unsupported tensor")
        if (value.is_floating_point() or value.is_complex()) and not torch.isfinite(value).all():
            raise ValueError("nonfinite tensor")
    elif value is None or type(value) in (bool, int):
        return
    elif type(value) is float:
        if not math.isfinite(value):
            raise ValueError("nonfinite number")
    elif type(value) is str:
        if len(value) > 4096:
            raise ValueError("long string")
    elif isinstance(value, dict):
        for key, child in value.items():
            if type(key) not in (int, str):
                raise ValueError("unsupported key")
            _safe_tree(child, depth + 1)
    elif type(value) in (list, tuple):
        for child in value:
            _safe_tree(child, depth + 1)
    else:
        raise ValueError("unsupported state object")


def _validate_optimizer(saved, optimizer, required_ids, global_step):
    prototype = optimizer.state_dict()
    if (not isinstance(optimizer, torch.optim.AdamW) or not isinstance(saved, dict)
            or set(saved) != {"state", "param_groups"}
            or type(saved["param_groups"]) is not list
            or len(saved["param_groups"]) != len(prototype["param_groups"])
            or not isinstance(saved["state"], dict)
            or type(required_ids) is not list or not required_ids
            or any(type(value) is not int for value in required_ids)
            or len(set(required_ids)) != len(required_ids)
            or set(saved["state"]) != set(required_ids)):
        raise ValueError("incomplete optimizer")
    parameters = {}
    for group, expected, actual in zip(saved["param_groups"], prototype["param_groups"], optimizer.param_groups):
        if (not isinstance(group, dict) or set(group) != set(expected)
                or group["params"] != expected["params"]):
            raise ValueError("optimizer parameter groups")
        for key in expected:
            if key != "lr" and group[key] != expected[key]:
                raise ValueError("optimizer configuration changed")
        if type(group["lr"]) is not float or group["lr"] <= 0:
            raise ValueError("optimizer learning rate")
        for param_id, parameter in zip(group["params"], actual["params"]):
            parameters[param_id] = (parameter, group["amsgrad"])
    for param_id, item in saved["state"].items():
        if param_id not in parameters or not isinstance(item, dict):
            raise ValueError("optimizer parameter identity")
        parameter, amsgrad = parameters[param_id]
        moments = ("exp_avg", "exp_avg_sq", "max_exp_avg_sq") if amsgrad else ("exp_avg", "exp_avg_sq")
        if set(item) != {"step", *moments}:
            raise ValueError("optimizer state fields")
        counter = item["step"]
        if (not isinstance(counter, torch.Tensor) or counter.ndim != 0
                or not 1 <= float(counter) <= global_step or not float(counter).is_integer()):
            raise ValueError("optimizer step")
        for name in moments:
            tensor = item[name]
            if (not isinstance(tensor, torch.Tensor) or tensor.shape != parameter.shape
                    or tensor.dtype != parameter.dtype
                    or (name != "exp_avg" and not bool((tensor >= 0).all()))):
                raise ValueError("optimizer moment shape")


def _validate_scheduler(saved, scheduler, optimizer_state, completed_epoch):
    prototype = scheduler.state_dict()
    if (not isinstance(scheduler, torch.optim.lr_scheduler.ExponentialLR)
            or not isinstance(saved, dict) or set(saved) != set(prototype)
            or saved["gamma"] != prototype["gamma"] or saved["base_lrs"] != prototype["base_lrs"]
            or type(saved["last_epoch"]) is not int or saved["last_epoch"] != completed_epoch
            or type(saved["_step_count"]) is not int or saved["_step_count"] != completed_epoch + 1
            or saved["_get_lr_called_within_step"] is not False
            or type(saved["_last_lr"]) is not list
            or saved["_last_lr"] != [group["lr"] for group in optimizer_state["param_groups"]]):
        raise ValueError("incomplete scheduler")


class TrainingState:
    def __init__(self, generator, discriminator, optimizer_g, optimizer_d,
                 scheduler_g, scheduler_d, scaler, sampler_seed):
        if torch.version.cuda is not None or str(torch.__version__) != "2.10.0+cpu":
            raise CheckpointError("training.runtime.incompatible")
        if type(sampler_seed) is not int or not 0 <= sampler_seed <= 2 ** 32 - 1:
            raise CheckpointError("training.state.invalid")
        self.generator, self.discriminator = generator, discriminator
        self.optimizer_g, self.optimizer_d = optimizer_g, optimizer_d
        self.scheduler_g, self.scheduler_d = scheduler_g, scheduler_d
        self.scaler, self.sampler_seed = scaler, sampler_seed
        self._validate_cpu()

    def _validate_cpu(self):
        if getattr(self, "_training_failed", False):
            raise CheckpointError("training.state.failed")
        for model in (self.generator, self.discriminator):
            for tensor in (*model.parameters(), *model.buffers()):
                if tensor.device.type != "cpu" or (tensor.is_floating_point() and tensor.dtype != torch.float32):
                    raise CheckpointError("training.device.invalid")
        if self.scaler.is_enabled():
            raise CheckpointError("training.precision.invalid")

    def save(self, store: CheckpointStore, completed_epoch: int, global_step: int) -> Checkpoint:
        self._validate_cpu()
        common = {"format": 1, "identity": asdict(store.identity),
                  "completed_epoch": completed_epoch, "next_epoch": completed_epoch + 1,
                  "global_step": global_step, "torch_version": str(torch.__version__)}
        numpy_state = np.random.get_state()
        training = {
            **common, "scheduler_g": self.scheduler_g.state_dict(),
            "scheduler_d": self.scheduler_d.state_dict(), "scaler": self.scaler.state_dict(),
            "sampler_seed": self.sampler_seed, "sampler_next_epoch": completed_epoch + 1,
            "optimizer_state_ids_g": list(self.optimizer_g.state_dict()["state"]),
            "optimizer_state_ids_d": list(self.optimizer_d.state_dict()["state"]),
            "python_rng": random.getstate(), "torch_rng": torch.get_rng_state(),
            "numpy_rng": (numpy_state[0], torch.tensor(numpy_state[1].astype(np.int64)),
                          numpy_state[2], numpy_state[3], numpy_state[4]),
        }
        generator = {**common, "model": self.generator.state_dict(), "optimizer": self.optimizer_g.state_dict()}
        discriminator = {**common, "model": self.discriminator.state_dict(), "optimizer": self.optimizer_d.state_dict()}
        try:
            for payload in (generator, discriminator, training):
                _safe_tree(payload)
        except ValueError:
            raise CheckpointError("training.state.invalid") from None

        def write_payload(stage):
            opener = getattr(store, "open_payload_writer", None)
            for name, payload in (("generator.pt", generator), ("discriminator.pt", discriminator),
                                  ("training.pt", training)):
                with (opener(stage / name) if opener is not None else (stage / name).open("xb")) as stream:
                    torch.save(payload, stream)
        return store.publish(completed_epoch, global_step, write_payload)

    def restore(self, checkpoint: Checkpoint) -> ResumePosition:
        self._validate_cpu()
        try:
            with ExitStack() as pins:
                # Pin ALL validated payloads before deserializing any of them.
                batch_opener = getattr(checkpoint, "open_payloads", None)
                if batch_opener is not None:
                    opened = pins.enter_context(batch_opener())
                    streams = [opened[name] for name in ("generator.pt", "discriminator.pt", "training.pt")]
                else:
                    # Legacy whole-epoch checkpoints retain their old reader.
                    streams = [pins.enter_context(checkpoint.open_payload(name))
                               for name in ("generator.pt", "discriminator.pt", "training.pt")]
                generator, discriminator, training = [
                    torch.load(stream, map_location="cpu", weights_only=True) for stream in streams]
            for payload in (generator, discriminator, training):
                _safe_tree(payload)
                if not isinstance(payload, dict):
                    raise ValueError("state object")
                expected = {"format": 1, "identity": asdict(checkpoint.identity),
                            "completed_epoch": checkpoint.completed_epoch,
                            "next_epoch": checkpoint.next_epoch, "global_step": checkpoint.global_step,
                            "torch_version": str(torch.__version__)}
                for key, value in expected.items():
                    if payload[key] != value:
                        raise ValueError("checkpoint pair mismatch")
            if (type(training["sampler_seed"]) is not int
                    or not 0 <= training["sampler_seed"] <= 2 ** 32 - 1
                    or training["sampler_next_epoch"] != checkpoint.next_epoch):
                raise ValueError("sampler state")
            python_rng = training["python_rng"]
            random.Random().setstate(python_rng)  # validate before mutating global RNG
            numpy_rng = training["numpy_rng"]
            if (type(numpy_rng) is not tuple or len(numpy_rng) != 5 or numpy_rng[0] != "MT19937"
                    or not isinstance(numpy_rng[1], torch.Tensor) or numpy_rng[1].dtype != torch.int64
                    or tuple(numpy_rng[1].shape) != (624,)
                    or not bool(((numpy_rng[1] >= 0) & (numpy_rng[1] <= 2 ** 32 - 1)).all())):
                raise ValueError("numpy RNG state")
            numpy_rng = (numpy_rng[0], numpy_rng[1].numpy().astype(np.uint32), *numpy_rng[2:])
            np.random.RandomState().set_state(numpy_rng)
            torch_rng = training["torch_rng"]
            if (not isinstance(torch_rng, torch.Tensor) or torch_rng.dtype != torch.uint8
                    or torch_rng.shape != torch.get_rng_state().shape):
                raise ValueError("torch RNG state")
            # Validate in an independent generator before changing the global RNG.
            torch.Generator(device="cpu").set_state(torch_rng)
            if training["scaler"] != {}:
                raise ValueError("CPU FP32 scaler state")
            _validate_optimizer(generator["optimizer"], self.optimizer_g,
                                training["optimizer_state_ids_g"], checkpoint.global_step)
            _validate_optimizer(discriminator["optimizer"], self.optimizer_d,
                                training["optimizer_state_ids_d"], checkpoint.global_step)
            _validate_scheduler(training["scheduler_g"], self.scheduler_g,
                                generator["optimizer"], checkpoint.completed_epoch)
            _validate_scheduler(training["scheduler_d"], self.scheduler_d,
                                discriminator["optimizer"], checkpoint.completed_epoch)
            # Trusted publication adapters may impose additional cursor
            # invariants. Validate AFTER restricted decoding/core checks but
            # BEFORE touching models, optimizers, schedulers or global RNG.
            update_validator = getattr(checkpoint, "_validate_update_payloads", None)
            if update_validator is not None:
                update_validator(generator, discriminator, training)
            self.generator.load_state_dict(generator["model"], strict=True)
            self.discriminator.load_state_dict(discriminator["model"], strict=True)
            self.optimizer_g.load_state_dict(generator["optimizer"])
            self.optimizer_d.load_state_dict(discriminator["optimizer"])
            self.scheduler_g.load_state_dict(training["scheduler_g"])
            self.scheduler_d.load_state_dict(training["scheduler_d"])
            self.scaler.load_state_dict(training["scaler"])
            self.sampler_seed = training["sampler_seed"]
            # Constructors/loading may consume randomness; restore RNG last.
            random.setstate(python_rng)
            np.random.set_state(numpy_rng)
            torch.set_rng_state(torch_rng)
            return ResumePosition(checkpoint.next_epoch, checkpoint.global_step, self.sampler_seed)
        except CheckpointError:
            raise
        except Exception:
            # Worker discards this state object after failure; never silently
            # warm-start or continue with a partly loaded optimizer.
            raise CheckpointError("training.state.invalid") from None
