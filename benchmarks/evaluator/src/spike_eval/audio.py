from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import sys
from typing import TypedDict
import uuid

import numpy as np
from scipy.signal import resample_poly
import soundfile as sf


TARGET_SAMPLE_RATE = 48_000
ANCHOR_SAMPLE_RATE = 16_000
MIN_VOICED_SECONDS = 3.0
MIN_ANCHOR_SECONDS = 5.0
MAX_ANCHOR_SECONDS = 15.0
VOICE_THRESHOLD_DBFS = -45.0
CLIPPING_THRESHOLD = 1.0 - (1.0 / 32_768.0)


class AudioInfo(TypedDict):
    sample_rate: int
    channels: int
    duration_seconds: float
    peak_dbfs: float
    clipping_ratio: float
    sha256: str


class AudioValidationError(ValueError):
    def __init__(self, code: str, message: str) -> None:
        self.code = code
        super().__init__(f"{code}: {message}")


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
    except OSError as error:
        raise AudioValidationError("INVALID_WAV", f"cannot read {path.name}: {error}") from error
    return digest.hexdigest()


def _load_wav(path: Path) -> tuple[np.ndarray, int]:
    try:
        file_info = sf.info(str(path))
        if not file_info.format.upper().startswith("WAV"):
            raise AudioValidationError("INVALID_WAV", f"{path.name} is not a WAV file")
        if file_info.frames <= 0 or file_info.samplerate <= 0 or file_info.channels <= 0:
            raise AudioValidationError("INVALID_WAV", f"{path.name} has an invalid header")

        samples, sample_rate = sf.read(str(path), dtype="float64", always_2d=True)
        if samples.shape != (file_info.frames, file_info.channels):
            raise AudioValidationError("INVALID_WAV", f"{path.name} is truncated")
        if not np.isfinite(samples).all():
            raise AudioValidationError("INVALID_WAV", f"{path.name} contains non-finite samples")
    except AudioValidationError:
        raise
    except (OSError, RuntimeError, ValueError, sf.LibsndfileError) as error:
        raise AudioValidationError("INVALID_WAV", f"cannot decode {path.name}: {error}") from error

    return samples, int(sample_rate)


def _metadata(path: Path, samples: np.ndarray, sample_rate: int) -> AudioInfo:
    peak = float(np.max(np.abs(samples))) if samples.size else 0.0
    peak_dbfs = 20.0 * math.log10(peak) if peak > 0.0 else -math.inf
    clipped = int(np.count_nonzero(np.abs(samples) >= CLIPPING_THRESHOLD))
    return {
        "sample_rate": sample_rate,
        "channels": int(samples.shape[1]),
        "duration_seconds": float(samples.shape[0] / sample_rate),
        "peak_dbfs": peak_dbfs,
        "clipping_ratio": float(clipped / samples.size),
        "sha256": _sha256(path),
    }


def inspect_audio(path: str | os.PathLike[str]) -> AudioInfo:
    audio_path = Path(path)
    samples, sample_rate = _load_wav(audio_path)
    return _metadata(audio_path, samples, sample_rate)


def _assert_reference_quality(samples: np.ndarray, sample_rate: int) -> None:
    amplitude_by_frame = np.max(np.abs(samples), axis=1)
    threshold = 10.0 ** (VOICE_THRESHOLD_DBFS / 20.0)
    voiced_seconds = float(np.count_nonzero(amplitude_by_frame > threshold) / sample_rate)
    if voiced_seconds < MIN_VOICED_SECONDS:
        raise AudioValidationError(
            "REFERENCE_TOO_SHORT",
            f"at least {MIN_VOICED_SECONDS:g} voiced seconds required; found {voiced_seconds:.3f}",
        )

    if np.any(np.abs(samples) >= CLIPPING_THRESHOLD):
        raise AudioValidationError("CLIPPING_DETECTED", "reference contains full-scale samples")


def prepare_reference(
    source: str | os.PathLike[str],
    destination: str | os.PathLike[str],
) -> AudioInfo:
    """Create an atomic mono 48 kHz reference without modifying the master."""
    source_path = Path(source).resolve()
    destination_path = Path(destination).resolve()
    if os.path.normcase(source_path) == os.path.normcase(destination_path):
        raise AudioValidationError("MASTER_OVERWRITE_FORBIDDEN", "source and destination must differ")

    source_hash_before = _sha256(source_path)
    destination_path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path = destination_path.with_name(f"{destination_path.name}.{uuid.uuid4().hex}.part")

    try:
        samples, sample_rate = _load_wav(source_path)
        mono = np.mean(samples, axis=1, dtype=np.float64)
        if sample_rate == TARGET_SAMPLE_RATE:
            prepared = mono
        else:
            divisor = math.gcd(sample_rate, TARGET_SAMPLE_RATE)
            prepared = resample_poly(
                mono,
                TARGET_SAMPLE_RATE // divisor,
                sample_rate // divisor,
                padtype="line",
            )
        prepared_2d = np.asarray(prepared, dtype=np.float64).reshape(-1, 1)
        _assert_reference_quality(prepared_2d, TARGET_SAMPLE_RATE)
        sf.write(
            str(temporary_path),
            prepared,
            TARGET_SAMPLE_RATE,
            subtype="PCM_16",
            format="WAV",
        )

        result = inspect_audio(temporary_path)
        if result["sample_rate"] != TARGET_SAMPLE_RATE or result["channels"] != 1:
            raise AudioValidationError("INVALID_WAV", "prepared reference is not mono 48 kHz")
        prepared_info = sf.info(str(temporary_path))
        if prepared_info.format != "WAV" or prepared_info.subtype != "PCM_16":
            raise AudioValidationError(
                "WORKER_INCOMPATIBLE_WAV",
                "prepared reference must use canonical RIFF PCM16 for the pinned VoxCPM2 worker",
            )
        if result["clipping_ratio"] != 0.0:
            raise AudioValidationError("CLIPPING_DETECTED", "conversion introduced clipping")

        os.replace(temporary_path, destination_path)
        result["sha256"] = _sha256(destination_path)
        return result
    finally:
        temporary_path.unlink(missing_ok=True)
        source_hash_after = _sha256(source_path)
        if source_hash_after != source_hash_before:
            raise AudioValidationError("MASTER_HASH_CHANGED", "source master changed during preparation")


def extract_reference_anchor(
    source: str | os.PathLike[str],
    destination: str | os.PathLike[str],
    *,
    start_seconds: float,
    duration_seconds: float,
) -> AudioInfo:
    """Atomically extract a short canonical prompt for the pinned worker."""
    source_path = Path(source).resolve()
    destination_path = Path(destination).resolve()
    if os.path.normcase(source_path) == os.path.normcase(destination_path):
        raise AudioValidationError("MASTER_OVERWRITE_FORBIDDEN", "source and destination must differ")
    if not math.isfinite(start_seconds) or start_seconds < 0.0:
        raise AudioValidationError("ANCHOR_RANGE_INVALID", "anchor start must be non-negative")
    if (
        not math.isfinite(duration_seconds)
        or duration_seconds < MIN_ANCHOR_SECONDS
        or duration_seconds > MAX_ANCHOR_SECONDS
    ):
        raise AudioValidationError(
            "ANCHOR_DURATION_INVALID",
            f"anchor duration must be between {MIN_ANCHOR_SECONDS:g} and {MAX_ANCHOR_SECONDS:g} seconds",
        )

    source_hash_before = _sha256(source_path)
    samples, sample_rate = _load_wav(source_path)
    start_frame = round(start_seconds * sample_rate)
    frame_count = round(duration_seconds * sample_rate)
    end_frame = start_frame + frame_count
    if start_frame < 0 or frame_count <= 0 or end_frame > samples.shape[0]:
        raise AudioValidationError("ANCHOR_RANGE_INVALID", "anchor extends outside the source audio")

    mono = np.mean(samples[start_frame:end_frame], axis=1, dtype=np.float64)
    if sample_rate == ANCHOR_SAMPLE_RATE:
        anchor = mono
    else:
        divisor = math.gcd(sample_rate, ANCHOR_SAMPLE_RATE)
        anchor = resample_poly(
            mono,
            ANCHOR_SAMPLE_RATE // divisor,
            sample_rate // divisor,
            padtype="line",
        )
    anchor_2d = np.asarray(anchor, dtype=np.float64).reshape(-1, 1)
    _assert_reference_quality(anchor_2d, ANCHOR_SAMPLE_RATE)

    destination_path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path = destination_path.with_name(f"{destination_path.name}.{uuid.uuid4().hex}.part")
    try:
        sf.write(
            str(temporary_path),
            anchor,
            ANCHOR_SAMPLE_RATE,
            subtype="PCM_16",
            format="WAV",
        )
        result = inspect_audio(temporary_path)
        output_info = sf.info(str(temporary_path))
        if (
            result["sample_rate"] != ANCHOR_SAMPLE_RATE
            or result["channels"] != 1
            or output_info.format != "WAV"
            or output_info.subtype != "PCM_16"
        ):
            raise AudioValidationError(
                "WORKER_INCOMPATIBLE_WAV",
                "anchor must use canonical mono 16 kHz RIFF PCM16",
            )
        if result["clipping_ratio"] != 0.0:
            raise AudioValidationError("CLIPPING_DETECTED", "anchor conversion introduced clipping")
        os.replace(temporary_path, destination_path)
        result["sha256"] = _sha256(destination_path)
        return result
    finally:
        temporary_path.unlink(missing_ok=True)
        if _sha256(source_path) != source_hash_before:
            raise AudioValidationError("MASTER_HASH_CHANGED", "source master changed during anchor extraction")


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Prepare a local VoxCPM2 reference WAV")
    subparsers = parser.add_subparsers(dest="command", required=True)
    prepare = subparsers.add_parser("prepare")
    prepare.add_argument("--source", required=True)
    prepare.add_argument("--destination", required=True)
    anchor = subparsers.add_parser("anchor")
    anchor.add_argument("--source", required=True)
    anchor.add_argument("--destination", required=True)
    anchor.add_argument("--start-seconds", required=True, type=float)
    anchor.add_argument("--duration-seconds", required=True, type=float)
    return parser


def main(argv: list[str] | None = None) -> int:
    arguments = _build_parser().parse_args(argv)
    try:
        if arguments.command == "prepare":
            result = prepare_reference(arguments.source, arguments.destination)
        elif arguments.command == "anchor":
            result = extract_reference_anchor(
                arguments.source,
                arguments.destination,
                start_seconds=arguments.start_seconds,
                duration_seconds=arguments.duration_seconds,
            )
        else:  # pragma: no cover - argparse enforces the command set.
            raise AssertionError(f"unsupported command: {arguments.command}")
    except AudioValidationError as error:
        print(str(error), file=sys.stderr)
        return 2

    print(json.dumps(result, ensure_ascii=False, allow_nan=False, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
