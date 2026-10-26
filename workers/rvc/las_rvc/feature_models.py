"""Audited, offline CPU HuBERT/RMVPE extraction; no stock CLI/device discovery."""
import builtins
from contextlib import ExitStack, contextmanager
import hashlib
import io
from importlib.metadata import version
import json
import os
from pathlib import Path
import re
import sys
import threading
import types
import uuid

import numpy as np

from .checkpoints import CheckpointError
from .features import PreparedSelection, align_rvc_features
from .owned_files import pin_chain, pinned_file, plain_path

RVC_COMMIT = "81eed5e8f68b6bed1789f682fe78cdd324495afc"
SOURCE_FILES = {
    "infer/hubert.py": (4148, "bbfba54d1d83eae3fb634b2909f8f6bf41e6ffe436200ec246867c5c93ebba26"),
    "infer/rmvpe.py": (24108, "074171aec63e4b68f04d8691c62af50a1d99b9bc6dc56c4d6749fad4ba64853a"),
    "tools/cuda_graph.py": (7721, "e3b4ce4fc8a33a3a921e70f78ce6965440c770d79d3ce71fb9f215db3a2db1a2"),
}
ASSET_FILES = {
    "hubert_base/config.json": (1492, "0346950779dfb7f9316fa74ed846e2b8a22a08eedfdc5387b73f327cb1a4a7cf"),
    "hubert_base/preprocessor_config.json": (225, "7c1976a680fb7acc757cd36fb08eef878fa36c70b4c9d2d595df9c608bbbbf0e"),
    "hubert_base/pytorch_model.bin": (189206711, "cc8c20f4b90a520757260197a3ff2505705a7adbd20ad9eeaa4e1a9b38442ef5"),
    "rmvpe.pt": (181184272, "6d62215f4306e3ca278246188607209f09af3dc77ed4232efdd069798c4ec193"),
}
FEATURE_PROFILE = {
    "version": 1, "rvc": "v2", "sample_rate": 40000, "feature_sample_rate": 16000,
    "hubert_layer": 12, "hubert_width": 768, "hubert_do_normalize": False,
    "hubert_attention": "eager", "phone_repeat": 2, "hop40k": 400,
    "f0": "rmvpe", "f0_threshold": 0.03, "f0_range": [50, 1100],
    "unvoiced": "native-linear-interpolation", "device": "cpu",
    "dtype": "float32", "threads": 2, "torch": "2.10.0+cpu", "transformers": "4.49.0",
    "rmvpe_batchnorm_counter": "positive-infinite-half-scalar-to-zero-int64-eval-only",
}
_PREFIX = "_las_rvc_feature_vendor"
_import_lock = threading.Lock()
_vendor_modules = {}


def _verify(stream, expected):
    stream.seek(0, 2)
    if stream.tell() != expected[0]:
        raise ValueError("size")
    stream.seek(0)
    digest = hashlib.sha256()
    while chunk := stream.read(1024 * 1024):
        digest.update(chunk)
    if digest.hexdigest() != expected[1]:
        raise ValueError("digest")
    stream.seek(0)


def _import_verified(source_root, sources):
    """Compile only verified bytes under an empty, private virtual package.

    The sole audited tools.cuda_graph import is redirected to this package.
    No global infer/tools packages, source directory search or .pyc is used.
    """
    global _vendor_modules
    with _import_lock:
        current = {name: value for name, value in sys.modules.items()
                   if name == _PREFIX or name.startswith(_PREFIX + ".")}
        if current != _vendor_modules:
            raise CheckpointError("features.source.collision")
        if _vendor_modules:
            return _vendor_modules[_PREFIX + ".hubert"], _vendor_modules[_PREFIX + ".rmvpe"]
        package = types.ModuleType(_PREFIX)
        package.__path__ = []
        created = {_PREFIX: package}
        sys.modules[_PREFIX] = package
        def guarded_import(name, globals=None, locals=None, fromlist=(), level=0):
            if name == "tools.cuda_graph" and level == 0:
                return created[_PREFIX + ".cuda_graph"]
            return builtins.__import__(name, globals, locals, fromlist, level)
        try:
            for relative, name in (("tools/cuda_graph.py", "cuda_graph"),
                    ("infer/hubert.py", "hubert"), ("infer/rmvpe.py", "rmvpe")):
                fullname = _PREFIX + "." + name
                module = types.ModuleType(fullname)
                module.__file__ = str(source_root / relative)
                module.__package__ = _PREFIX
                module.__builtins__ = dict(vars(builtins), __import__=guarded_import)
                created[fullname] = module
                sys.modules[fullname] = module
                exec(compile(sources[relative], module.__file__, "exec"), module.__dict__)
            _vendor_modules = created.copy()
            return created[_PREFIX + ".hubert"], created[_PREFIX + ".rmvpe"]
        except Exception:
            for name, module in created.items():
                if sys.modules.get(name) is module:
                    del sys.modules[name]
            raise CheckpointError("features.source.import_failed") from None


def _weights(stream, torch, reset_batchnorm_overflow=False):
    # Explicit weights_only also resists inherited FORCE_NO_WEIGHTS_ONLY_LOAD.
    payload = torch.load(stream, map_location="cpu", weights_only=True)
    if not isinstance(payload, dict) or not payload:
        raise ValueError("raw state dictionary")
    for name, tensor in payload.items():
        # The EXACT pinned RMVPE state cast BN integer counters to FP16; some
        # overflowed to +inf. PyTorch BatchNorm never uses these in eval mode.
        # Normalize only those scalar counters, never parameters/running stats.
        if (reset_batchnorm_overflow and type(name) is str and name.endswith(".num_batches_tracked")
                and type(tensor) is torch.Tensor and tensor.device.type == "cpu"
                and tensor.layout == torch.strided and tensor.dtype == torch.float16
                and tensor.ndim == 0 and bool(torch.isposinf(tensor))):
            tensor = torch.zeros((), dtype=torch.int64, device="cpu")
            payload[name] = tensor
        if (type(name) is not str or not isinstance(tensor, torch.Tensor)
                or tensor.device.type != "cpu" or tensor.layout != torch.strided
                or tensor.dtype not in (torch.float16, torch.float32, torch.int64)
                or not bool(torch.isfinite(tensor).all())):
            raise ValueError("state tensor")
    return payload


class _FeatureModels:
    def __init__(self, torch, hubert_module, hubert, rmvpe):
        self.torch, self.hubert_module, self.hubert, self.rmvpe = torch, hubert_module, hubert, rmvpe

    def extract(self, clip):
        # Paired input loader owns validation. Copy read-only PCM for Torch.
        audio16 = clip.audio16.copy()
        with self.torch.inference_mode():
            source = self.torch.from_numpy(audio16).unsqueeze(0)
            phone = self.hubert_module.extract_hubert_features(self.hubert, source, "v2")
            phone = phone.squeeze(0).detach().cpu().numpy().astype(np.float32, copy=True)
            f0 = self.rmvpe.infer_from_audio(audio16, thred=0.03)
        return phone, np.asarray(f0).copy()


def _build_models(source_root, sources, assets):
    if version("torch") != "2.10.0+cpu" or version("transformers") != "4.49.0":
        raise CheckpointError("features.runtime.incompatible")
    os.environ["CUDA_VISIBLE_DEVICES"] = ""
    os.environ["RVC_CUDA_GRAPH"] = "0"
    os.environ.pop("LIBROSA_CACHE_DIR", None)
    import torch
    from transformers import HubertConfig
    if torch.version.cuda is not None:
        raise CheckpointError("features.runtime.incompatible")
    torch.set_num_threads(2)
    config_data = json.load(assets["hubert_base/config.json"])
    processor = json.load(assets["hubert_base/preprocessor_config.json"])
    if (processor["do_normalize"] is not False or processor["sampling_rate"] != 16000
            or config_data["hidden_size"] != 768 or config_data["num_hidden_layers"] != 12):
        raise CheckpointError("features.config.invalid")
    hubert_module, rmvpe_module = _import_verified(source_root, sources)
    config = HubertConfig.from_dict(config_data)
    config._attn_implementation = "eager"
    # Never call from_pretrained, AutoFeatureExtractor, or RMVPE.__init__ (it
    # reopens a path and has optional GPU/ONNX/automatic-device branches).
    with torch.device("cpu"):
        hubert = hubert_module.HubertModelWithFinalProj(config).float()
        hubert.load_state_dict(_weights(assets["hubert_base/pytorch_model.bin"], torch), strict=True)
        hubert.eval()
        rmvpe = rmvpe_module.RMVPE.__new__(rmvpe_module.RMVPE)
        rmvpe.resample_kernel = {}
        rmvpe.is_half, rmvpe.device = False, "cpu"
        rmvpe.mel_extractor = rmvpe_module.MelSpectrogram(
            False, 128, 16000, 1024, 160, None, 30, 8000).float().to("cpu")
        rmvpe.model = rmvpe_module.E2E(4, 1, (2, 2)).float()
        rmvpe.model.load_state_dict(_weights(assets["rmvpe.pt"], torch, reset_batchnorm_overflow=True), strict=True)
        rmvpe.model.eval()
        rmvpe.cents_mapping = np.pad(20 * np.arange(360) + 1997.3794084376191, (4, 4))
    return _FeatureModels(torch, hubert_module, hubert, rmvpe)


@contextmanager
def create_feature_models(source_root, asset_root):
    """All source/config/weights verified before importing or building ML code."""
    with ExitStack() as pins:
        try:
            source_root, asset_root = plain_path(Path(source_root)), plain_path(Path(asset_root))
            pin_chain(pins, source_root)
            pin_chain(pins, asset_root)
            sources, assets = {}, {}
            for relative, expected in SOURCE_FILES.items():
                stream = pins.enter_context(pinned_file(source_root / relative))
                _verify(stream, expected)
                sources[relative] = stream.read()
            for relative, expected in ASSET_FILES.items():
                stream = pins.enter_context(pinned_file(asset_root / relative))
                _verify(stream, expected)
                assets[relative] = stream
        except Exception:
            raise CheckpointError("features.models.invalid") from None
        try:
            models = _build_models(source_root, sources, assets)
        except CheckpointError:
            raise
        except Exception:
            raise CheckpointError("features.models.load_failed") from None
        yield models


def _write_bytes(path, data):
    with path.open("xb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())


def _write_array(path, array, pins):
    # Arrays are bounded by the <=400-frame native contract. Hash the bytes
    # actually serialized from validated arrays, not a potentially changed file.
    buffer = io.BytesIO()
    np.save(buffer, array, allow_pickle=False)
    raw = buffer.getvalue()
    expected = (len(raw), hashlib.sha256(raw).hexdigest())
    _write_bytes(path, raw)
    stream = pins.enter_context(pinned_file(path))
    _verify(stream, expected)
    return {"bytes": expected[0], "sha256": expected[1], "shape": list(array.shape), "dtype": str(array.dtype)}


def _verify_bundle(root, raw_manifest, files, ids):
    with ExitStack() as pins:
        pin_chain(pins, root)
        if {entry.name for entry in root.iterdir()} != {*ids, "manifest.json", "READY"}:
            raise ValueError("output inventory")
        for name in ids:
            directory = root / name
            pin_chain(pins, directory)
            if {entry.name for entry in directory.iterdir()} != {
                    "hubert.npy", "f0.npy", "phone.npy", "pitch.npy", "pitchf.npy", "wave.npy"}:
                raise ValueError("output inventory")
        digest = hashlib.sha256(raw_manifest).hexdigest()
        for name, expected in (("manifest.json", (len(raw_manifest), digest)),
                ("READY", (65, hashlib.sha256(digest.encode() + b"\n").hexdigest()))):
            _verify(pins.enter_context(pinned_file(root / name)), expected)
        for relative, info in files.items():
            _verify(pins.enter_context(pinned_file(root / relative)), (info["bytes"], info["sha256"]))


def publish_feature_preview(selection, output_root, extractor):
    """READY means immutable preview, NEVER permission to start training.

    Caller holds the prepared-input context and verified model context. Only a
    fresh empty explicitly owned root is accepted. Failed stages are retained;
    nothing existing is overwritten, adopted, deleted, or exposed as READY.
    """
    try:
        with ExitStack() as parents:
            root = plain_path(Path(output_root))
            pin_chain(parents, root)
            if any(root.iterdir()) or type(selection) is not PreparedSelection:
                raise ValueError("nonempty output or input type")
            ids = [clip.id for clip in selection.clips]
            if (not 1 <= len(ids) <= 3 or len(set(ids)) != len(ids)
                    or any(type(name) is not str or re.fullmatch(r"clip-[0-9]{6}", name) is None for name in ids)
                    or type(selection.training_eligible) is not bool
                    or type(selection.dataset_clip_count) is not int
                    or not len(ids) <= selection.dataset_clip_count <= 2048
                    or re.fullmatch(r"[0-9a-f]{64}", selection.manifest_sha256) is None
                    or re.fullmatch(r"[0-9a-f]{32}", selection.dataset_id) is None):
                raise ValueError("selection")
            stage = root / ("stage-" + uuid.uuid4().hex)
            stage.mkdir()
            with ExitStack() as stage_pins:
                pin_chain(stage_pins, stage)
                files, clips = {}, []
                for clip in selection.clips:
                    raw_phone, raw_f0 = extractor(clip)
                    aligned = align_rvc_features(clip.audio40, raw_phone, raw_f0)
                    directory = stage / clip.id
                    directory.mkdir()
                    pin_chain(stage_pins, directory)
                    arrays = {"hubert": raw_phone, "f0": raw_f0, "phone": aligned.phone,
                        "pitch": aligned.pitch, "pitchf": aligned.pitchf, "wave": aligned.wave}
                    for name, array in arrays.items():
                        files[f"{clip.id}/{name}.npy"] = _write_array(directory / (name + ".npy"), array, stage_pins)
                    clips.append({"id": clip.id, "source_samples40k": int(clip.audio40.size),
                        "source_samples16k": int(clip.audio16.size), "hubert_frames": int(raw_phone.shape[0]),
                        "f0_frames": int(raw_f0.size), "aligned_frames": int(aligned.pitch.size),
                        "voiced_f0_frames": int(np.count_nonzero(raw_f0 > 0))})
                manifest = {"format": 1, "purpose": "cpu-feature-preview", "profile": FEATURE_PROFILE,
                    "input": {"manifest_sha256": selection.manifest_sha256, "dataset_id": selection.dataset_id,
                        "dataset_clip_count": selection.dataset_clip_count,
                        "training_eligible": selection.training_eligible},
                    "selected_clip_ids": ids, "full_dataset_extracted": len(ids) == selection.dataset_clip_count,
                    "training_admitted": False, "rvc_commit": RVC_COMMIT,
                    "source_pins": SOURCE_FILES, "asset_pins": ASSET_FILES, "clips": clips, "files": files}
                raw = json.dumps(manifest, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()
                _write_bytes(stage / "manifest.json", raw)
                _write_bytes(stage / "READY", hashlib.sha256(raw).hexdigest().encode() + b"\n")
                _verify_bundle(stage, raw, files, ids)
            # Stage pins released, parent still denies rename/delete. On Windows
            # rename is no-replacement; output was empty at operation entry.
            stage.rename(root / "features")
            _verify_bundle(root / "features", raw, files, ids)
            return manifest
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("features.preview.failed") from None
