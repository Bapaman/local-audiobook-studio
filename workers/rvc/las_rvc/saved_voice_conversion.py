"""Bounded arbitrary-audio conversion with one explicitly accepted saved RVC G.

This is not held-out reconstruction, a quality gate, a trainer or a publisher.
The caller owns process lifetime and must validate exit/tree cleanup before
accepting the returned candidate. Native imports occur only after START.
"""
from contextlib import ExitStack, contextmanager
import hashlib
import io
import os
from pathlib import Path
import struct
import time
from types import SimpleNamespace
import wave

import numpy as np

from .local_workflow import _json, _load, _previous, _sha, _write
from .owned_files import pin_chain, pinned_file, plain_path

PROFILE_VERSION = "rvc-saved-voice-cpu-v1"
MAX_SAMPLES48 = 120 * 48000
CENTRAL40 = 3 * 40000
CONTEXT40 = 40000
OVERLAP40 = 4000


class ConversionError(Exception):
    def __init__(self, code):
        self.code = code
        super().__init__(code)


def _inside(root, value):
    if type(value) is not str:
        raise ValueError("explicit owned path")
    path = plain_path(Path(value))
    if path == root or not path.is_relative_to(root):
        raise ValueError("project-owned descendant")
    return path


def _hash_file(pins, path, sha, limit):
    stream = pins.enter_context(pinned_file(path))
    digest, size = hashlib.sha256(), 0
    while chunk := stream.read(1024 * 1024):
        size += len(chunk)
        if size > limit:
            raise ValueError("bounded file")
        digest.update(chunk)
    if digest.hexdigest() != _sha(sha):
        raise ValueError("changed bound bytes")
    stream.seek(0)
    return stream, size


def _selected_state(pins, project, workflow, source, runtime):
    if type(workflow) is not dict or set(workflow) != {"path", "sha256"}:
        raise ValueError("explicit workflow")
    state_path = _inside(project, workflow["path"])
    state_stream, _ = _hash_file(pins, state_path, workflow["sha256"], 65536)
    state = _load(state_stream.read(65537))
    model_source = state["source"]
    if (type(model_source) is not dict or set(model_source) != {"path", "sha256", "consent_confirmed"}
            or model_source["consent_confirmed"] is not True):
        raise ValueError("consented model source")
    model_source_path = _inside(project, model_source["path"])
    if model_source_path == Path(source["path"]) or model_source["sha256"] == source["sha256"]:
        raise ValueError("fresh source distinct from model recording")
    _hash_file(pins, model_source_path, model_source["sha256"], 1024 ** 3)
    pins.enter_context(pinned_file(state_path.parent / "READY"))
    # Replay existing exact accepted initial/session proof; no latest discovery.
    state = _previous(workflow, model_source)
    inputs = state["inputs"]
    _inside(project, inputs["dataset_root"])
    _sha(inputs["dataset_sha256"])
    for name in ("preflight", "corpus", "candidate", "admission"):
        ref = inputs[name]
        if type(ref) is not dict or set(ref) != {"root", "sha256"}:
            raise ValueError("known admitted input")
        _inside(project, ref["root"])
        _sha(ref["sha256"])
    if plain_path(Path(inputs["source_root"])) != plain_path(Path(runtime["source_root"])):
        raise ValueError("same pinned kernel runtime")
    checkpoint = state["accepted"]["checkpoint"]
    checkpoint_root = _inside(project, checkpoint["root"])
    pin_chain(pins, checkpoint_root)
    if {entry.name for entry in checkpoint_root.iterdir()} != {
            "generator.pt", "discriminator.pt", "training.pt", "manifest.json", "READY"}:
        raise ValueError("complete checkpoint inventory")
    manifest_stream, _ = _hash_file(pins, checkpoint_root / "manifest.json", checkpoint["sha256"], 65536)
    manifest = _load(manifest_stream.read(65537))
    pins.enter_context(pinned_file(checkpoint_root / "READY"))
    files = manifest["files"]
    if type(files) is not dict or set(files) != {"generator.pt", "discriminator.pt", "training.pt"}:
        raise ValueError("complete checkpoint files")
    for name, metadata in files.items():
        if (type(metadata) is not dict or set(metadata) != {"bytes", "sha256"}
                or type(metadata["bytes"]) is not int or not 1 <= metadata["bytes"] <= 2 * 1024 ** 3):
            raise ValueError("checkpoint payload metadata")
        _, size = _hash_file(pins, checkpoint_root / name, metadata["sha256"], 2 * 1024 ** 3)
        if size != metadata["bytes"]:
            raise ValueError("checkpoint payload size")
    receipt = state["accepted"]["receipt"]
    receipt_path = _inside(project, receipt["path"])
    _hash_file(pins, receipt_path, receipt["sha256"], 65536)
    if receipt_path.name == "ledger.json":
        pins.enter_context(pinned_file(receipt_path.parent / "READY"))
    return state


def _source_audio(pins, project, source):
    if type(source) is not dict or set(source) != {"path", "sha256"}:
        raise ValueError("exact fresh source")
    stream, size = _hash_file(pins, _inside(project, source["path"]), source["sha256"], MAX_SAMPLES48 * 2 + 44)
    header = stream.read(44)
    if len(header) != 44:
        raise ValueError("PCM header")
    riff, riff_size, wave_tag, fmt, fmt_size, encoding, channels, rate, byte_rate, block, bits, data_tag, data_size = struct.unpack(
        "<4sI4s4sIHHIIHH4sI", header)
    if ((riff, wave_tag, fmt, fmt_size, encoding, channels, rate, byte_rate, block, bits, data_tag)
            != (b"RIFF", b"WAVE", b"fmt ", 16, 1, 1, 48000, 96000, 2, 16, b"data")
            or not 2 <= data_size <= MAX_SAMPLES48 * 2 or data_size % 2
            or size != data_size + 44 or riff_size != size - 8):
        raise ValueError("exact bounded mono48k PCM16")
    audio = np.frombuffer(stream.read(data_size + 1), dtype="<i2").astype(np.float32) / 32768
    if audio.size != data_size // 2:
        raise ValueError("complete PCM")
    audio.flags.writeable = False
    return audio


def _request(pins, path, sha, output_root):
    request_path = plain_path(Path(path))
    stream, _ = _hash_file(pins, request_path, sha, 65536)
    request = _load(stream.read(65537))
    if (type(request) is not dict or set(request) != {"format", "purpose", "project_root", "workflow", "source",
            "runtime", "output_root", "profile_version"} or type(request["format"]) is not int or request["format"] != 1
            or request["purpose"] != "saved-voice-audio-conversion" or request["profile_version"] != PROFILE_VERSION
            or type(request["project_root"]) is not str):
        raise ValueError("known conversion request")
    project = plain_path(Path(request["project_root"]))
    pin_chain(pins, project)
    _inside(project, str(request_path))
    root = _inside(project, request["output_root"])
    if root != plain_path(Path(output_root)):
        raise ValueError("explicit output")
    pin_chain(pins, root)
    runtime = request["runtime"]
    if type(runtime) is not dict or set(runtime) != {"source_root", "feature_asset_root"}:
        raise ValueError("known feature runtime")
    for value in runtime.values():
        if type(value) is not str:
            raise ValueError("known runtime path")
        pin_chain(pins, plain_path(Path(value)))
    audio = _source_audio(pins, project, request["source"])
    state = _selected_state(pins, project, request["workflow"], request["source"], runtime)
    return request, state, audio


def read_request(path, sha, output_root):
    """Cheap SHA/PCM/accepted proof replay; never imports Torch or discovers files."""
    try:
        with ExitStack() as pins:
            request, _, _ = _request(pins, path, sha, output_root)
            return request
    except Exception:
        raise ConversionError("conversion.request.invalid") from None


def _stop(root):
    marker = root / "STOP"
    if not marker.exists():
        return
    with pinned_file(marker) as stream:
        if stream.seek(0, 2) > 64:
            raise ConversionError("conversion.stop.invalid")
    raise ConversionError("conversion.stopped")


def _start_gate(root):
    _stop(root)
    value = os.environ.get("LAS_CONVERSION_START_GATE")
    if value is None:
        return
    if plain_path(Path(value)) != root / "START":
        raise ConversionError("conversion.start_gate.invalid")
    until = time.monotonic() + 15
    while not (root / "START").exists():
        _stop(root)
        if time.monotonic() >= until:
            raise ConversionError("conversion.start_gate.timeout")
        time.sleep(.05)
    with pinned_file(root / "START") as stream:
        if stream.seek(0, 2) > 64:
            raise ConversionError("conversion.start_gate.invalid")
    _stop(root)


def align_conversion_features(samples40, phone, f0):
    """Keep raw zero F0; repeat2 and edge-pad the native two-frame shortfall."""
    try:
        if (type(samples40) is not int or not 80400 <= samples40 <= 200000 or samples40 % 400
                or type(phone) is not np.ndarray or phone.dtype != np.float32 or phone.ndim != 2
                or phone.shape != (((samples40 * 2 // 5 - 400) // 320 + 1), 768)
                or not np.isfinite(phone).all() or np.max(np.abs(phone)) > 1000
                or type(f0) is not np.ndarray or f0.dtype.kind != "f" or f0.ndim != 1
                or f0.size != samples40 // 400 + 1 or not np.isfinite(f0).all()
                or not np.all((f0 >= 0) & (f0 <= 1100))):
            raise ValueError("native feature geometry and values")
        frames = samples40 // 400
        phones = np.repeat(phone, 2, axis=0)
        if not 0 <= frames - phones.shape[0] <= 2:
            raise ValueError("native repeat2 tail")
        phones = np.pad(phones, ((0, frames - phones.shape[0]), (0, 0)), mode="edge")
        pitchf = f0[:frames].astype(np.float32, copy=True)
        mel_min, mel_max = 1127 * np.log(1 + 50 / 700), 1127 * np.log(1 + 1100 / 700)
        mel = (1127 * np.log(1 + pitchf / 700) - mel_min) * 254 / (mel_max - mel_min) + 1
        pitch = np.rint(np.clip(mel, 1, 255)).astype(np.int64)
        return phones, pitch, pitchf
    except Exception:
        raise ConversionError("conversion.features.invalid") from None


def _waveform(value, count):
    if (type(value) is not np.ndarray or value.dtype != np.float32 or value.shape != (count,)
            or not np.isfinite(value).all() or np.max(np.abs(value)) > 1):
        raise ConversionError("conversion.audio.invalid")
    return value


def convert_samples(samples48, kernel, *, stopped=lambda: None, emit=lambda event: None):
    """3s central windows, 100ms linear overlap, 1s reflected model context.

    Partial tails are reflection-extended to the next 10ms frame. A one-sample
    source uses edge repetition because reflection has no distinct neighbour.
    Exactly silent central samples remain silent; no RMS threshold or filtering.
    """
    try:
        if (type(samples48) is not np.ndarray or samples48.dtype != np.float32 or samples48.ndim != 1
                or not 1 <= samples48.size <= MAX_SAMPLES48):
            raise ConversionError("conversion.audio.invalid")
        _waveform(samples48, samples48.size)
        stopped()
        if not np.any(samples48):
            return np.zeros(samples48.size, np.float32)
        from scipy.signal import resample_poly
        raw40 = resample_poly(samples48, 5, 6).astype(np.float32)
        # Resampling may overshoot on legitimate full-scale PCM. Do not clip it.
        _waveform(raw40, raw40.size)
        count = ((raw40.size + 399) // 400) * 400
        audio40 = np.pad(raw40, (0, count - raw40.size), mode="reflect" if raw40.size > 1 else "edge")
        output, weight = np.zeros(count, np.float64), np.zeros(count, np.float64)
        starts = []
        start = 0
        while True:
            starts.append(start)
            if start + CENTRAL40 >= count:
                break
            start += CENTRAL40 - OVERLAP40
        for ordinal, start in enumerate(starts):
            stopped()
            end = min(start + CENTRAL40, count)
            left, right = max(0, start - CONTEXT40), min(count, end + CONTEXT40)
            context = audio40[left:right]
            pad_left = CONTEXT40 - (start - left)
            pad_right = CONTEXT40 - (right - end)
            context = np.pad(context, (pad_left, pad_right), mode="reflect" if context.size > 1 else "edge")
            central = audio40[start:end]
            if np.any(central):
                generated = _waveform(kernel(context), context.size)[CONTEXT40:CONTEXT40 + end - start]
            else:
                generated = np.zeros(end - start, np.float32)
            stopped()
            weights = np.ones(end - start, np.float64)
            if ordinal:
                overlap = min(OVERLAP40, weights.size)
                weights[:overlap] = (np.arange(overlap, dtype=np.float64) + .5) / overlap
            if ordinal < len(starts) - 1:
                overlap = end - starts[ordinal + 1]
                weights[-overlap:] = 1 - (np.arange(overlap, dtype=np.float64) + .5) / overlap
            output[start:end] += generated.astype(np.float64) * weights
            weight[start:end] += weights
            emit({"phase": "saved_voice_conversion_window", "window_index": ordinal + 1, "window_count": len(starts)})
        if np.any(weight <= 0) or not np.isfinite(output).all():
            raise ConversionError("conversion.audio.invalid")
        result40 = (output / weight).astype(np.float32)
        result48 = resample_poly(result40, 6, 5).astype(np.float32)[:samples48.size].copy()
        _waveform(result48, samples48.size)
        # Zero samples in a wholly silent central window must not become noise
        # due to another window's context or polyphase ringing at its boundary.
        for start in starts:
            end = min(start + CENTRAL40, count)
            if not np.any(audio40[start:end]):
                result48[start * 6 // 5:min(samples48.size, end * 6 // 5)] = 0
        stopped()
        return result48
    except ConversionError:
        raise
    except Exception:
        raise ConversionError("conversion.kernel.failed") from None


@contextmanager
def _native_kernel(request, state):
    """Full admitted source/runtime/checkpoint replay before restricted Torch loads."""
    from .feature_corpus import CorpusRef
    from .feature_models import create_feature_models
    from .personal_update import TrainingInputs, verify_admitted_training
    from .training_admission import TRAINING_PRESET
    from .update_checkpoints import load_update_checkpoint
    from .personal_inference import _generator
    data = state["inputs"]
    inputs = TrainingInputs(Path(data["dataset_root"]), data["dataset_sha256"],
        *[CorpusRef(Path(data[name]["root"]), data[name]["sha256"]) for name in ("preflight", "corpus", "candidate", "admission")],
        Path(data["source_root"]), Path(data["base_asset_root"]))
    with ExitStack() as pins:
        admitted = pins.enter_context(verify_admitted_training(inputs))
        with pinned_file(inputs.dataset_root / "manifest.json") as stream:
            prepared = _load(stream.read(2 * 1024 ** 2 + 1))
        if prepared["source"]["source_sha256"] != state["source"]["sha256"]:
            raise ConversionError("conversion.model.invalid")
        selected = state["accepted"]["checkpoint"]
        checkpoint = load_update_checkpoint(Path(selected["root"]), selected["sha256"], admitted.run_identity(state["run_id"]),
            admitted.train_ids, inputs.admission.sha256, TRAINING_PRESET["sampler_seed"])
        if (checkpoint.global_step, checkpoint.completed_epoch, checkpoint.position.epoch_size) != (
                state["global_step"], state["completed_epoch"], state["epoch_size"]):
            raise ConversionError("conversion.model.invalid")
        generator = _generator(inputs.source_root, checkpoint)
        models = pins.enter_context(create_feature_models(request["runtime"]["source_root"], request["runtime"]["feature_asset_root"]))
        import torch
        torch.set_num_threads(2)
        torch.manual_seed(1234)
        def infer(audio40):
            from scipy.signal import resample_poly
            audio16 = resample_poly(audio40, 2, 5).astype(np.float32)
            phone, f0 = models.extract(SimpleNamespace(audio16=audio16))
            phone, pitch, pitchf = align_conversion_features(audio40.size, phone, f0)
            frames = phone.shape[0]
            with torch.inference_mode(), torch.device("cpu"):
                result = generator.infer(torch.from_numpy(phone).unsqueeze(0), torch.tensor([frames], dtype=torch.int64),
                    torch.from_numpy(pitch).unsqueeze(0), torch.from_numpy(pitchf).unsqueeze(0), torch.tensor([0], dtype=torch.int64))[0]
                if result.shape != (1, 1, frames * 400) or result.dtype != torch.float32 or result.device.type != "cpu":
                    raise ConversionError("conversion.kernel.invalid")
                return result[0, 0].numpy().copy()
        yield infer


def run_conversion(request_path, request_sha256, output_root, *, kernel_factory=None, emit=lambda event: None):
    """Return a candidate only. START/STOP remain in the parent attempt root."""
    try:
        root = plain_path(Path(output_root))
        with ExitStack() as pins:
            pin_chain(pins, root)
            control_leaves = {"START", "STOP"}
            if plain_path(Path(request_path)) == root / "request.json":
                control_leaves.add("request.json")
            if {entry.name for entry in root.iterdir()} - control_leaves:
                raise ConversionError("conversion.output_not_empty")
            _start_gate(root)
            request, state, source = _request(pins, request_path, request_sha256, root)
            _stop(root)
            emit({"phase": "saved_voice_conversion_inputs_verified", "sample_count": int(source.size), "global_step": state["global_step"]})
            with (kernel_factory or _native_kernel)(request, state) as kernel:
                samples = convert_samples(source, kernel, stopped=lambda: _stop(root), emit=emit)
            _stop(root)
            buffer = io.BytesIO()
            with wave.open(buffer, "wb") as wav:
                wav.setnchannels(1)
                wav.setsampwidth(2)
                wav.setframerate(48000)
                wav.writeframes(np.rint(samples * 32767).astype("<i2").tobytes())
            raw_wav = buffer.getvalue()
            manifest = {"format": 1, "purpose": "saved-voice-audio-conversion-candidate", "request_sha256": request_sha256,
                "workflow": request["workflow"], "source": request["source"], "profile_version": PROFILE_VERSION,
                "sample_rate": 48000, "sample_count": int(source.size),
                "selected_point": {"checkpoint": state["accepted"]["checkpoint"], "run_id": state["run_id"],
                    **{name: state[name] for name in ("global_step", "completed_epoch", "epoch_size")}},
                "inference_seed": 1234, "central_seconds": 3, "context_seconds": 1, "overlap_seconds": .1,
                "unvoiced_f0": "preserved-zero", "peak_normalization": False, "source_mixing": False,
                "quality_established": False, "auto_activate_voice": False,
                "files": {"final.wav": {"bytes": len(raw_wav), "sha256": hashlib.sha256(raw_wav).hexdigest()}}}
            raw_manifest = _json(manifest)
            manifest_sha = hashlib.sha256(raw_manifest).hexdigest()
            candidate = root / "candidate"
            candidate.mkdir()
            pin_chain(pins, candidate)
            _write(candidate / "final.wav", raw_wav)
            _write(candidate / "manifest.json", raw_manifest)
            _stop(root)
            _write(candidate / "READY", manifest_sha.encode() + b"\n")
            emit({"phase": "saved_voice_conversion_candidate_written", "sample_count": int(source.size)})
            return {"phase": "saved_voice_conversion_control", "candidate_root": str(candidate), "manifest_sha256": manifest_sha}
    except ConversionError:
        raise
    except Exception:
        raise ConversionError("conversion.failed") from None
