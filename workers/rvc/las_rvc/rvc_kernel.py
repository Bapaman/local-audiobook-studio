"""Pinned native RVCv2 CPU kernel; no device discovery or stock train CLI."""
from contextlib import ExitStack
from dataclasses import dataclass, fields
import hashlib
import importlib
import importlib.abc
import importlib.util
import json
import os
from pathlib import Path
import random
import sys
import threading

import numpy as np
import torch

from .checkpoints import CheckpointError
from .owned_files import pinned_file
from .training_state import TrainingState

RVC_COMMIT = "81eed5e8f68b6bed1789f682fe78cdd324495afc"
SOURCE_MANIFEST = {
    "infer/module/models.py": (35798, "769615b8dbed4775c8083881663a79c0e5ec0b662dd5f0c0a1bc3ccdd9827e99"),
    "infer/module/attentions.py": (15994, "8820e6047353d99344fc73fbb6c69d85e4f61066fc498982cc895e9577d4519f"),
    "infer/module/commons.py": (5463, "132a907eff444907861e63684c04c88ae631d03707470858f7c53f857b0cc5c8"),
    "infer/module/modules.py": (17775, "b1aa77b0921468a61492fa89ed1f1212cd22fee3caa551befe44d8ab359fd2f1"),
    "infer/module/transforms.py": (7458, "fb0d3320adf32ca0eaa44dd56e8922927f77ec3dd45c775e10c3da1704072c6a"),
    "train/losses.py": (1444, "6eb912eb073b44cf624bac90cced3ff8fa21a3ed1eb5022d0c7d8fefbe1cd075"),
    "train/mel_processing.py": (3819, "de0d14c7ddd111334e152363e584c66538de881bf9164d9551e6198f9a3575f9"),
    "configs/v1/40k.json": (1024, "da8c508eef404263cedfe6a85fd5a0b7d631a0a29766b392df145e8ea0412fb8"),
}
BASE_GENERATOR = (73106273, "3b2c44035e782c4b14ddc0bede9e2f4a724d025cd073f736d4f43708453adfcb")
BASE_DISCRIMINATOR = (142875703, "6b6ab091e70801b28e3f41f335f2fc5f3f35c75b39ae2628d419644ec2b0fa09")
_import_lock = threading.Lock()
_vendor_modules = {}


def _verify_stream(stream, expected):
    stream.seek(0, os.SEEK_END)
    if stream.tell() != expected[0]:
        raise ValueError("size mismatch")
    stream.seek(0)
    digest = hashlib.sha256()
    while chunk := stream.read(1024 * 1024):
        digest.update(chunk)
    if digest.hexdigest() != expected[1]:
        raise ValueError("hash mismatch")
    stream.seek(0)


class _SealedSource(importlib.abc.MetaPathFinder, importlib.abc.Loader):
    """Execute verified bytes, not mutable paths or pre-existing .pyc files.

    Empty virtual infer/infer.module/train packages seal namespace collisions.
    They have no disk search path: only this explicitly audited closure loads.
    """
    def __init__(self, sources):
        self.sources = sources

    def find_spec(self, fullname, path=None, target=None):
        if fullname in self.sources:
            return importlib.util.spec_from_loader(fullname, self,
                is_package=fullname in ("infer", "infer.module", "train"))
        return None

    def create_module(self, spec):
        return None

    def exec_module(self, module):
        path, source = self.sources[module.__name__]
        module.__file__ = str(path)
        if module.__spec__.submodule_search_locations is not None:
            module.__path__ = []
        exec(compile(source, str(path), "exec"), module.__dict__)


def _import_verified(source_root, source_bytes):
    global _vendor_modules
    with _import_lock:
        foreign = {name: value for name, value in sys.modules.items()
                   if name in ("infer", "train") or name.startswith(("infer.", "train."))}
        if any(_vendor_modules.get(name) is not value for name, value in foreign.items()):
            raise CheckpointError("training.source.collision")
        if _vendor_modules:
            if foreign != _vendor_modules:
                raise CheckpointError("training.source.collision")
            return tuple(_vendor_modules[name] for name in (
                "infer.module.models", "infer.module.commons", "train.losses", "train.mel_processing"))
        sources = {relative[:-3].replace("/", "."): (source_root / relative, data)
                   for relative, data in source_bytes.items() if relative.endswith(".py")}
        for package in ("infer", "infer.module", "train"):
            sources[package] = (source_root / package.replace(".", "/") / "__init__.py", b"")
        finder = _SealedSource(sources)
        # Disable inherited librosa disk caches before its first import.
        os.environ.pop("LIBROSA_CACHE_DIR", None)
        sys.meta_path.insert(0, finder)
        try:
            result = tuple(importlib.import_module(name) for name in (
                "infer.module.models", "infer.module.commons", "train.losses", "train.mel_processing"))
            _vendor_modules = {name: sys.modules[name] for name in sources if name in sys.modules}
            return result
        except Exception:
            for name in sources:
                if name in sys.modules and sys.modules[name].__loader__ is finder:
                    del sys.modules[name]
            raise CheckpointError("training.source.import_failed") from None
        finally:
            sys.meta_path.remove(finder)


def _model_weights(stream):
    payload = torch.load(stream, map_location="cpu", weights_only=True)
    if not isinstance(payload, dict) or not isinstance(payload.get("model"), dict):
        raise ValueError("missing model")
    weights = payload["model"]
    if not weights:
        raise ValueError("empty model")
    for name, tensor in weights.items():
        if (type(name) is not str or not isinstance(tensor, torch.Tensor)
                or tensor.device.type != "cpu" or tensor.layout != torch.strided
                or tensor.dtype not in (torch.float16, torch.float32)
                or not bool(torch.isfinite(tensor).all())):
            raise ValueError("invalid pretrained model tensor")
    # The pinned distributor's 560 G /165 D tensors are stored in FP16.
    # Widen those verified values for CPU FP32 training; never use half CPU AMP.
    return {name: tensor.float() for name, tensor in weights.items()}


def create_rvc_state(source_root: Path, generator_path: Path, discriminator_path: Path) -> TrainingState:
    if str(torch.__version__) != "2.10.0+cpu" or torch.version.cuda is not None:
        raise CheckpointError("training.runtime.incompatible")
    torch.set_num_threads(2)
    source_root = Path(source_root)
    with ExitStack() as pins:
        try:
            sources = {}
            for relative, expected in SOURCE_MANIFEST.items():
                stream = pins.enter_context(pinned_file(source_root / relative))
                _verify_stream(stream, expected)
                sources[relative] = stream.read()
            config = json.loads(sources["configs/v1/40k.json"])
        except Exception:
            raise CheckpointError("training.source.invalid") from None
        try:
            g_stream = pins.enter_context(pinned_file(Path(generator_path)))
            d_stream = pins.enter_context(pinned_file(Path(discriminator_path)))
            _verify_stream(g_stream, BASE_GENERATOR)
            _verify_stream(d_stream, BASE_DISCRIMINATOR)
        except Exception:
            raise CheckpointError("training.asset.invalid") from None
        models, _, _, _ = _import_verified(source_root, sources)
        try:
            random.seed(1234)
            np.random.seed(1234)
            torch.manual_seed(1234)
            # No device selection, CLI, DDP, CUDA or DirectML dependencies.
            with torch.device("cpu"):
                generator = models.SynthesizerTrnMs768NSFsid(
                    1025, 32, **config["model"], is_half=False, sr=40000).float()
                discriminator = models.MultiPeriodDiscriminatorV2(use_spectral_norm=False).float()
            generator.load_state_dict(_model_weights(g_stream), strict=True)
            discriminator.load_state_dict(_model_weights(d_stream), strict=True)
            optimizers = [torch.optim.AdamW(model.parameters(), lr=0.0001,
                betas=(0.8, 0.99), eps=1e-9, weight_decay=0.01, amsgrad=False,
                maximize=False, foreach=False, capturable=False, differentiable=False, fused=False)
                for model in (generator, discriminator)]
            schedulers = [torch.optim.lr_scheduler.ExponentialLR(optimizer, gamma=0.999875)
                          for optimizer in optimizers]
            return TrainingState(generator, discriminator, *optimizers, *schedulers,
                                 torch.amp.GradScaler("cpu", enabled=False), 1234)
        except CheckpointError:
            raise
        except Exception:
            raise CheckpointError("training.model.invalid") from None


@dataclass(frozen=True)
class RvcBatch:
    phone: torch.Tensor
    phone_lengths: torch.Tensor
    pitch: torch.Tensor
    pitchf: torch.Tensor
    spec: torch.Tensor
    spec_lengths: torch.Tensor
    wave: torch.Tensor
    wave_lengths: torch.Tensor
    speaker_ids: torch.Tensor


def _validate_batch(batch):
    try:
        if type(batch) is not RvcBatch:
            raise ValueError("batch type")
        for field in fields(batch):
            tensor = getattr(batch, field.name)
            if (type(tensor) is not torch.Tensor or tensor.device.type != "cpu"
                    or tensor.layout != torch.strided or tensor.requires_grad):
                raise ValueError("tensor type")
        if batch.phone.ndim != 3 or batch.phone.shape[0] != 1 or batch.phone.shape[2] != 768:
            raise ValueError("phone dimensions")
        frames = batch.phone.shape[1]
        # Owned batch1 dataset clips are <=4s, no padded or unbounded tensors.
        if not 32 <= frames <= 400:
            raise ValueError("frame count")
        expected = {
            "phone": ((1, frames, 768), torch.float32),
            "phone_lengths": ((1,), torch.int64), "pitch": ((1, frames), torch.int64),
            "pitchf": ((1, frames), torch.float32), "spec": ((1, 1025, frames), torch.float32),
            "spec_lengths": ((1,), torch.int64), "wave": ((1, 1, frames * 400), torch.float32),
            "wave_lengths": ((1,), torch.int64), "speaker_ids": ((1,), torch.int64),
        }
        for name, (shape, dtype) in expected.items():
            tensor = getattr(batch, name)
            if tuple(tensor.shape) != shape or tensor.dtype != dtype or not bool(torch.isfinite(tensor).all()):
                raise ValueError("dimensions or values")
        if (int(batch.phone_lengths[0]) != frames or int(batch.spec_lengths[0]) != frames
                or int(batch.wave_lengths[0]) != frames * 400 or int(batch.speaker_ids[0]) != 0
                or not bool(((batch.pitch >= 1) & (batch.pitch <= 255)).all())
                or not bool(((batch.pitchf >= 0) & (batch.pitchf <= 1100)).all())
                or not bool((batch.spec >= 0).all()) or not bool((batch.wave.abs() <= 1).all())):
            raise ValueError("lengths or ranges")
    except Exception:
        raise CheckpointError("training.batch.invalid") from None


def _finite(tensor):
    if (not isinstance(tensor, torch.Tensor) or tensor.device.type != "cpu"
            or tensor.dtype != torch.float32 or not bool(torch.isfinite(tensor).all())):
        raise CheckpointError("training.kernel.nonfinite")


def _validate_discriminator(output):
    if type(output) is not tuple or len(output) != 4:
        raise CheckpointError("training.kernel.invalid_output")
    real, fake, real_maps, fake_maps = output
    if any(type(value) is not list or len(value) != 9 for value in output):
        raise CheckpointError("training.kernel.invalid_output")
    for i, (a, b, maps_a, maps_b) in enumerate(zip(real, fake, real_maps, fake_maps)):
        _finite(a)
        _finite(b)
        if a.shape != b.shape or a.ndim != 2 or a.shape[0] != 1:
            raise CheckpointError("training.kernel.invalid_output")
        count = 7 if i == 0 else 6
        if type(maps_a) is not list or type(maps_b) is not list or len(maps_a) != count or len(maps_b) != count:
            raise CheckpointError("training.kernel.invalid_output")
        for layer_a, layer_b in zip(maps_a, maps_b):
            if layer_a.shape != layer_b.shape:
                raise CheckpointError("training.kernel.invalid_output")
            _finite(layer_a)
            _finite(layer_b)


def _validate_gradients(model):
    present = False
    for parameter in model.parameters():
        if parameter.grad is not None:
            present = True
            _finite(parameter.grad)
    if not present:
        raise CheckpointError("training.kernel.no_gradients")


def _validate_updated(model, optimizer):
    for parameter in model.parameters():
        _finite(parameter)
    for item in optimizer.state.values():
        for value in item.values():
            if isinstance(value, torch.Tensor):
                _finite(value)


def train_rvc_batch(state: TrainingState, batch: RvcBatch) -> dict[str, float]:
    _validate_batch(batch)  # Before any model/optimizer/RNG mutation.
    state._validate_cpu()
    models = _vendor_modules.get("infer.module.models")
    if (models is None or type(state.generator) is not models.SynthesizerTrnMs768NSFsid
            or type(state.discriminator) is not models.MultiPeriodDiscriminatorV2):
        raise CheckpointError("training.model.invalid")
    commons = _vendor_modules["infer.module.commons"]
    losses = _vendor_modules["train.losses"]
    mel = _vendor_modules["train.mel_processing"]
    try:
        state.generator.train()
        state.discriminator.train()
        state.optimizer_g.zero_grad(set_to_none=True)
        state.optimizer_d.zero_grad(set_to_none=True)
        generated, ids, _, mask, latents = state.generator(
            batch.phone, batch.phone_lengths, batch.pitch, batch.pitchf,
            batch.spec, batch.spec_lengths, batch.speaker_ids)
        _finite(generated)
        if generated.shape != (1, 1, 12800) or len(latents) != 6:
            raise CheckpointError("training.kernel.invalid_output")
        for value in (*latents, mask):
            _finite(value)
        _, z_p, m_p, logs_p, _, logs_q = latents
        target_mel = commons.slice_segments(
            mel.spec_to_mel_torch(batch.spec, 2048, 125, 40000, 0.0, None), ids, 32)
        generated_mel = mel.mel_spectrogram_torch(
            generated.squeeze(1), 2048, 125, 40000, 400, 2048, 0.0, None)
        if target_mel.shape != generated_mel.shape or target_mel.shape != (1, 125, 32):
            raise CheckpointError("training.kernel.invalid_output")
        real_wave = commons.slice_segments(batch.wave, ids * 400, 12800)
        d_output = state.discriminator(real_wave, generated.detach())
        _validate_discriminator(d_output)
        d_loss = losses.discriminator_loss(d_output[0], d_output[1])[0]
        _finite(d_loss)
        d_loss.backward()
        _validate_gradients(state.discriminator)
        state.optimizer_d.step()
        _validate_updated(state.discriminator, state.optimizer_d)
        del d_output

        d_output = state.discriminator(real_wave, generated)
        _validate_discriminator(d_output)
        g_loss = (losses.generator_loss(d_output[1])[0]
                  + losses.feature_loss(d_output[2], d_output[3])
                  + torch.nn.functional.l1_loss(target_mel, generated_mel) * 45
                  + losses.kl_loss(z_p, logs_q, m_p, logs_p, mask))
        _finite(g_loss)
        g_loss.backward()
        _validate_gradients(state.generator)
        state.optimizer_g.step()
        _validate_updated(state.generator, state.optimizer_g)
        return {"generator_loss": float(g_loss.detach()), "discriminator_loss": float(d_loss.detach())}
    except CheckpointError:
        state._training_failed = True
        raise
    except Exception:
        state._training_failed = True
        raise CheckpointError("training.kernel.failed") from None
