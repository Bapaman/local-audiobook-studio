"""Explicit decoder and owned immutable recording preparation; CPU only."""
from contextlib import ExitStack
from dataclasses import dataclass
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import uuid

import numpy as np
from scipy import signal
from scipy.io import wavfile

from .checkpoints import CheckpointError
from .owned_files import pin_chain, pinned_file, plain_path


@dataclass(frozen=True)
class DecodedRecording:
    samples: np.ndarray
    metadata: dict


def _digest_stream(stream, expected):
    if type(expected) is not str or re.fullmatch(r"[0-9a-f]{64}", expected) is None:
        raise ValueError("digest")
    stream.seek(0)
    digest = hashlib.sha256()
    while chunk := stream.read(1024 * 1024):
        digest.update(chunk)
    if digest.hexdigest() != expected:
        raise ValueError("changed bytes")
    stream.seek(0)


def _native(arguments):
    try:
        return subprocess.run(arguments, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            stdin=subprocess.DEVNULL, check=True, timeout=30,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).stdout
    except Exception:
        raise CheckpointError("recording.decoder.failed") from None


def decode_recording(*, source_path: Path, source_sha256: str,
                     ffmpeg_path: Path, ffmpeg_sha256: str,
                     ffprobe_path: Path, ffprobe_sha256: str,
                     consent: bool) -> DecodedRecording:
    if consent is not True:
        raise CheckpointError("recording.consent.required")
    with ExitStack() as pins:
        try:
            source = pins.enter_context(pinned_file(Path(source_path)))
            source.seek(0, os.SEEK_END)
            source_bytes = source.tell()
            if not 12 <= source_bytes <= 1024 ** 3:
                raise ValueError("source size")
            _digest_stream(source, source_sha256)
            header = source.read(16)
            if header[:4] in (b"RIFF", b"RF64") and header[8:12] == b"WAVE":
                container = "wav"
            elif header[4:8] == b"ftyp":
                container = "mov"
            elif header[:3] == b"ID3" or (header[0] == 255 and header[1] & 0xE0 == 0xE0):
                container = "mp3"
            else:
                raise ValueError("container")
        except Exception:
            raise CheckpointError("recording.source.invalid") from None
        try:
            for path, digest in ((ffmpeg_path, ffmpeg_sha256), (ffprobe_path, ffprobe_sha256)):
                binary = pins.enter_context(pinned_file(Path(path)))
                binary.seek(0, os.SEEK_END)
                if not 1 <= binary.tell() <= 256 * 1024 ** 2:
                    raise ValueError("decoder size")
                _digest_stream(binary, digest)
        except Exception:
            raise CheckpointError("recording.decoder.invalid") from None
        common = ["-v", "error", "-threads", "2", "-protocol_whitelist", "file,pipe", "-f", container]
        probe_raw = _native([str(ffprobe_path), *common, "-select_streams", "a:0",
            "-show_entries", "stream=codec_name,codec_type,channels,sample_rate:format=duration",
            "-of", "json", str(source_path)])
        try:
            if len(probe_raw) > 65536:
                raise ValueError("probe size")
            probe = json.loads(probe_raw)
            streams = probe["streams"]
            if (type(streams) is not list or len(streams) != 1
                    or streams[0]["codec_type"] != "audio"
                    or type(streams[0]["channels"]) is not int
                    or not 1 <= streams[0]["channels"] <= 2):
                raise ValueError("audio stream")
            duration = float(probe["format"]["duration"])
            if not math.isfinite(duration) or not 300 <= duration <= 1200:
                raise CheckpointError("recording.duration.invalid")
        except CheckpointError:
            raise
        except Exception:
            raise CheckpointError("recording.probe.invalid") from None
        raw = _native([str(ffmpeg_path), "-nostdin", *common, "-threads", "2",
            "-i", str(source_path), "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-t", "1201", "-ac", "1", "-ar", "40000", "-threads", "2",
            "-filter_threads", "1", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"])
        try:
            if len(raw) % 4 or len(raw) > 1201 * 40000 * 4:
                raise ValueError("PCM size")
            samples = np.frombuffer(raw, dtype="<f4")
            if not np.isfinite(samples).all() or not 300 * 40000 <= samples.size <= 1200 * 40000:
                raise ValueError("PCM values or duration")
            samples.flags.writeable = False
            return DecodedRecording(samples, {
                "source_sha256": source_sha256, "source_bytes": source_bytes,
                "container": container, "codec": streams[0].get("codec_name", "unknown"),
                "channels": streams[0]["channels"], "probe_duration_seconds": duration,
                "decoded_samples": int(samples.size), "sample_rate": 40000,
                "ffmpeg_sha256": ffmpeg_sha256, "ffprobe_sha256": ffprobe_sha256,
                "consent_confirmed": True, "pcm_sha256": hashlib.sha256(raw).hexdigest(),
            })
        except Exception:
            raise CheckpointError("recording.decode.invalid") from None


PREPARATION_PROFILE = {"version": "voice-prep-cpu-v1", "sample_rate": 40000,
    "highpass_hz": 48, "highpass_order": 5, "rms_hop_samples": 600,
    "rms_threshold_db": -42, "merge_gap_samples": 16000, "context_samples": 10000,
    "clip_samples": 148000, "step_samples": 136000, "min_clip_samples": 16000,
    "normalization_peak": 0.9, "resampler": "scipy-resample-poly-2-5"}


def _write_bytes(path, data):
    with path.open("xb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())


def _write_wave(path, rate, samples):
    with path.open("xb") as stream:
        wavfile.write(stream, rate, samples.astype(np.float32, copy=False))
        stream.flush()
        os.fsync(stream.fileno())


def _file_info(path):
    with pinned_file(path) as stream:
        digest = hashlib.sha256()
        size = 0
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
            size += len(chunk)
        return {"bytes": size, "sha256": digest.hexdigest()}


def _verify_inventory(stage, files):
    observed = set()
    for item in stage.iterdir():
        if item.name in ("clips40k", "clips16k") and item.is_dir():
            observed.update(f"{item.name}/{child.name}" for child in item.iterdir())
        elif item.name not in ("manifest.json", "READY"):
            observed.add(item.name)
    if observed != set(files):
        raise CheckpointError("dataset.inventory.invalid")
    for relative, expected in files.items():
        if _file_info(stage / relative) != expected:
            raise CheckpointError("dataset.inventory.invalid")


def _intervals(filtered):
    hop = PREPARATION_PROFILE["rms_hop_samples"]
    padding = (-filtered.size) % hop
    framed = np.pad(filtered, (0, padding)).reshape(-1, hop)
    voiced_frames = np.sqrt(np.mean(framed.astype(np.float64) ** 2, axis=1)) > 10 ** (-42 / 20)
    voiced = np.repeat(voiced_frames, hop)[:filtered.size]
    active = np.flatnonzero(voiced_frames)
    groups = []
    if active.size:
        start = previous = int(active[0])
        for value in active[1:]:
            value = int(value)
            if (value - previous - 1) * hop > 16000:
                groups.append((max(0, start * hop - 10000), min(filtered.size, (previous + 1) * hop + 10000)))
                start = value
            previous = value
        groups.append((max(0, start * hop - 10000), min(filtered.size, (previous + 1) * hop + 10000)))
    return voiced, groups


def prepare_pcm_dataset(recording: DecodedRecording, source_path: Path, output_root: Path) -> dict:
    try:
        if type(recording) is not DecodedRecording or type(recording.metadata) is not dict:
            raise ValueError("recording type")
        audio = recording.samples
        if (type(audio) is not np.ndarray or audio.ndim != 1 or audio.dtype != np.float32
                or not audio.flags.c_contiguous or not 16000 <= audio.size <= 1200 * 40000
                or not np.isfinite(audio).all() or float(np.abs(audio).max()) > 2.5
                or recording.metadata.get("sample_rate") != 40000
                or recording.metadata.get("decoded_samples") != audio.size
                or recording.metadata.get("consent_confirmed") is not True
                or hashlib.sha256(audio.view(np.uint8)).hexdigest() != recording.metadata.get("pcm_sha256")):
            raise ValueError("PCM values or provenance")
    except Exception:
        raise CheckpointError("dataset.pcm.invalid") from None
    root = Path(output_root)
    with ExitStack() as root_pins:
        try:
            plain_path(root)
            pin_chain(root_pins, root)
            if any(root.iterdir()):
                raise ValueError("not empty")
        except Exception:
            raise CheckpointError("dataset.root.invalid") from None
        try:
            source = root_pins.enter_context(pinned_file(Path(source_path)))
            _digest_stream(source, recording.metadata["source_sha256"])
            source.seek(0, os.SEEK_END)
            if source.tell() != recording.metadata["source_bytes"] or source.tell() > 1024 ** 3:
                raise ValueError("source size changed")
            source.seek(0)
        except Exception:
            raise CheckpointError("recording.source.invalid") from None
        dataset_id = uuid.uuid4().hex
        stage = root / ("preparing-" + dataset_id)
        try:
            stage.mkdir()
            with ExitStack() as stage_pins:
                pin_chain(stage_pins, stage)
                for directory in (stage / "clips40k", stage / "clips16k"):
                    directory.mkdir()
                    pin_chain(stage_pins, directory)
                with (stage / "master.bin").open("xb") as master:
                    while chunk := source.read(1024 * 1024):
                        master.write(chunk)
                    master.flush()
                    os.fsync(master.fileno())
                files = {"master.bin": _file_info(stage / "master.bin")}
                if files["master.bin"]["sha256"] != recording.metadata["source_sha256"]:
                    raise CheckpointError("recording.source.invalid")
                _write_wave(stage / "decoded40k.wav", 40000, audio)
                files["decoded40k.wav"] = _file_info(stage / "decoded40k.wav")
                b, a = signal.butter(5, 48, btype="high", fs=40000)
                filtered = signal.lfilter(b, a, audio).astype(np.float32)
                if not np.isfinite(filtered).all():
                    raise CheckpointError("dataset.pcm.invalid")
                voiced, groups = _intervals(filtered)
                covered = np.zeros(audio.size, dtype=np.bool_)
                clips = []
                for group_start, group_end in groups:
                    start = group_start
                    while group_end - start >= 16000:
                        end = min(group_end, start + 148000)
                        segment = filtered[start:end]
                        peak = float(np.abs(segment).max())
                        if peak > 0:
                            if len(clips) >= 2048:
                                raise CheckpointError("dataset.clips.limit")
                            normalized = (segment * (0.9 / peak)).astype(np.float32)
                            resampled = signal.resample_poly(normalized, 2, 5).astype(np.float32)
                            if not np.isfinite(resampled).all():
                                raise CheckpointError("dataset.pcm.invalid")
                            clip_id = f"clip-{len(clips):06d}"
                            relative40 = f"clips40k/{clip_id}.wav"
                            relative16 = f"clips16k/{clip_id}.wav"
                            _write_wave(stage / relative40, 40000, normalized)
                            _write_wave(stage / relative16, 16000, resampled)
                            files[relative40] = _file_info(stage / relative40)
                            files[relative16] = _file_info(stage / relative16)
                            clips.append({"id": clip_id, "wave40k": relative40, "wave16k": relative16,
                                "source_start_sample": start, "source_end_sample": end,
                                "samples40k": end - start, "samples16k": int(resampled.size)})
                            covered[start:end] = True
                        if end == group_end:
                            break
                        start += 136000
                usable_samples = int(np.count_nonzero(voiced & covered))
                coverage_samples = int(np.count_nonzero(covered))
                clipping = float(np.count_nonzero(np.abs(audio) >= 0.999) / audio.size)
                reasons = []
                if usable_samples < 300 * 40000:
                    reasons.append("dataset.usable_duration.short")
                if len(clips) < 30:
                    reasons.append("dataset.clips.insufficient")
                if clipping > 0.01:
                    reasons.append("dataset.clipping.excessive")
                profile_bytes = json.dumps(PREPARATION_PROFILE, sort_keys=True, separators=(",", ":")).encode()
                result = {"format": 1, "dataset_id": dataset_id, "profile": PREPARATION_PROFILE,
                    "profile_sha256": hashlib.sha256(profile_bytes).hexdigest(), "source": recording.metadata,
                    "duration_seconds": audio.size / 40000, "usable_samples": usable_samples,
                    "usable_seconds": usable_samples / 40000, "eligible": not reasons, "reasons": reasons,
                    "qc": {"decoded_clipping_proxy_fraction": clipping,
                           "signal_activity_samples": usable_samples,
                           "signal_activity_seconds": usable_samples / 40000,
                           "unique_clip_coverage_samples": coverage_samples,
                           "unique_clip_coverage_seconds": coverage_samples / 40000,
                           "coverage_duration_method": "unique-accepted-clip-source-intervals-including-context",
                           "speech_duration_seconds": None,
                           "speech_duration_verified": False,
                           "speech_music_other_speakers_verified": False,
                           "usable_duration_method": "unique-covered-above-threshold-RMS-samples"},
                    "clips": clips, "files": files}
                manifest = json.dumps(result, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()
                if len(manifest) > 2 * 1024 ** 2:
                    raise CheckpointError("dataset.manifest.limit")
                _verify_inventory(stage, files)
                _write_bytes(stage / "manifest.json", manifest)
                _write_bytes(stage / "READY", hashlib.sha256(manifest).hexdigest().encode() + b"\n")
            # Windows rename refuses any pre-existing destination, never overwrite.
            os.rename(stage, root / "dataset")
            _verify_inventory(root / "dataset", files)
            return result
        except CheckpointError:
            raise
        except Exception:
            # Retain only this owned incomplete stage for diagnosis; no foreign cleanup.
            raise CheckpointError("dataset.prepare.failed") from None
