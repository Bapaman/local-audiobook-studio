"""Native CPU training-batch preparation only; no optimizer/admission changes."""
from contextlib import ExitStack
from pathlib import Path

import torch

from .checkpoints import CheckpointError
from .features import AlignedFeatures, align_rvc_features
from .owned_files import pinned_file, plain_path
from .rvc_kernel import SOURCE_MANIFEST, RvcBatch, _import_verified, _validate_batch, _verify_stream


def _assemble_batch(aligned, full_spec, source_samples):
    try:
        if (type(aligned) is not AlignedFeatures or type(source_samples) is not int
                or not 16000 <= source_samples <= 148000
                or torch.is_inference_mode_enabled() or type(full_spec) is not torch.Tensor
                or full_spec.device.type != "cpu" or full_spec.layout != torch.strided
                or full_spec.dtype != torch.float32 or full_spec.requires_grad
                or torch.is_inference(full_spec)
                or tuple(full_spec.shape) != (1, 1025, source_samples // 400)
                or not bool(torch.isfinite(full_spec).all()) or not bool((full_spec >= 0).all())):
            raise ValueError("full original spectrum contract")
        frames = aligned.phone.shape[0]
        if frames > full_spec.shape[2]:
            raise ValueError("no padding")
        length = lambda value: torch.tensor([value], dtype=torch.int64, device="cpu")
        batch = RvcBatch(torch.from_numpy(aligned.phone.copy()).unsqueeze(0), length(frames),
            torch.from_numpy(aligned.pitch.copy()).unsqueeze(0),
            torch.from_numpy(aligned.pitchf.copy()).unsqueeze(0), full_spec[:, :, :frames].clone(),
            length(frames), torch.from_numpy(aligned.wave.copy()).reshape(1, 1, -1),
            length(frames * 400), length(0))
        _validate_batch(batch)
        return batch
    except Exception:
        raise CheckpointError("features.batch.invalid") from None


def build_native_feature_batch(source_root, audio40, raw_phone, raw_f0):
    """Caller holds input pins; spectrum uses original audio before trimming.

    No G/D construction, gradient update, saved checkpoint, or admission. These
    ordinary no-grad tensors are suitable for a future admitted training loader.
    """
    if str(torch.__version__) != "2.10.0+cpu" or torch.version.cuda is not None:
        raise CheckpointError("features.runtime.incompatible")
    aligned = align_rvc_features(audio40, raw_phone, raw_f0)
    torch.set_num_threads(2)
    with ExitStack() as pins:
        try:
            root = plain_path(Path(source_root))
            sources = {}
            for relative, expected in SOURCE_MANIFEST.items():
                stream = pins.enter_context(pinned_file(root / relative))
                _verify_stream(stream, expected)
                sources[relative] = stream.read()
        except Exception:
            raise CheckpointError("features.batch.source_invalid") from None
        _, _, _, mel = _import_verified(root, sources)
        try:
            # no_grad, not inference_mode: generator backward needs to save the
            # input tensors later. Never FFT the already trimmed aligned.wave.
            with torch.no_grad(), torch.device("cpu"):
                full_spec = mel.spectrogram_torch(torch.from_numpy(audio40.copy()).unsqueeze(0),
                    2048, 40000, 400, 2048, center=False)
                return _assemble_batch(aligned, full_spec, int(audio40.size))
        except CheckpointError:
            raise
        except Exception:
            raise CheckpointError("features.batch.invalid") from None
