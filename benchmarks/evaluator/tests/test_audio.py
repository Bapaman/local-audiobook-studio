from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

import numpy as np
import pytest
import soundfile as sf

from spike_eval.audio import (
    AudioValidationError,
    extract_reference_anchor,
    inspect_audio,
    prepare_reference,
)


REPO_ROOT = Path(__file__).resolve().parents[3]
PREPARE_SCRIPT = REPO_ROOT / "scripts" / "spike" / "Prepare-ReferenceAudio.ps1"
ANCHOR_SCRIPT = REPO_ROOT / "scripts" / "spike" / "New-ReferenceAnchor.ps1"


def _sine(duration_seconds: float, sample_rate: int, amplitude: float = 0.5) -> np.ndarray:
    frame_count = round(duration_seconds * sample_rate)
    timeline = np.arange(frame_count, dtype=np.float64) / sample_rate
    return amplitude * np.sin(2.0 * np.pi * 220.0 * timeline)


def _write_wav(
    path: Path,
    samples: np.ndarray,
    sample_rate: int,
    *,
    subtype: str = "PCM_24",
) -> None:
    sf.write(path, samples, sample_rate, subtype=subtype, format="WAV")


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _find_ffmpeg() -> str | None:
    explicit = os.environ.get("FFMPEG_PATH")
    if explicit and Path(explicit).is_file():
        return explicit
    command = shutil.which("ffmpeg")
    if command:
        return command
    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        package_root = Path(local_app_data) / "Microsoft" / "WinGet" / "Packages"
        candidates = sorted(
            package_root.glob("Gyan.FFmpeg.Essentials*/**/ffmpeg.exe"),
            key=lambda path: path.stat().st_mtime,
            reverse=True,
        )
        if candidates:
            return str(candidates[0])
    return None


def test_inspect_audio_reports_required_metadata_for_mono_48khz(tmp_path: Path) -> None:
    source = tmp_path / "reference-master.wav"
    _write_wav(source, _sine(4.0, 48_000), 48_000)

    info = inspect_audio(source)

    assert info == {
        "sample_rate": 48_000,
        "channels": 1,
        "duration_seconds": pytest.approx(4.0, abs=1 / 48_000),
        "peak_dbfs": pytest.approx(-6.0206, abs=0.001),
        "clipping_ratio": 0.0,
        "sha256": _sha256(source),
    }


def test_prepare_reference_canonicalizes_pcm16_without_touching_master(
    tmp_path: Path,
) -> None:
    source = tmp_path / "reference-master.wav"
    destination = tmp_path / "reference.wav"
    _write_wav(source, _sine(4.0, 48_000), 48_000, subtype="PCM_16")
    source_hash = _sha256(source)

    info = prepare_reference(source, destination)

    assert _sha256(source) == source_hash
    assert info["sha256"] == _sha256(destination)
    assert info["sample_rate"] == 48_000
    assert info["channels"] == 1
    assert sf.info(destination).format == "WAV"
    assert sf.info(destination).subtype == "PCM_16"


def test_prepare_reference_canonicalizes_wavex_pcm16_for_pinned_worker(
    tmp_path: Path,
) -> None:
    source = tmp_path / "wasapi-master.wav"
    destination = tmp_path / "reference.wav"
    sf.write(
        source,
        _sine(4.0, 48_000),
        48_000,
        subtype="PCM_16",
        format="WAVEX",
    )
    source_hash = _sha256(source)
    assert sf.info(source).format == "WAVEX"

    info = prepare_reference(source, destination)

    assert _sha256(source) == source_hash
    assert sf.info(destination).format == "WAV"
    assert sf.info(destination).subtype == "PCM_16"
    assert info["sample_rate"] == 48_000
    assert info["channels"] == 1


def test_prepare_reference_converts_pcm24_to_worker_safe_pcm16(
    tmp_path: Path,
) -> None:
    source = tmp_path / "reference-master.wav"
    destination = tmp_path / "reference.wav"
    _write_wav(source, _sine(4.0, 48_000), 48_000, subtype="PCM_24")
    source_hash = _sha256(source)

    info = prepare_reference(source, destination)

    assert _sha256(source) == source_hash
    assert destination.read_bytes() != source.read_bytes()
    assert sf.info(destination).subtype == "PCM_16"
    assert info["sample_rate"] == 48_000
    assert info["channels"] == 1
    assert info["clipping_ratio"] == 0.0


def test_prepare_reference_resamples_stereo_44khz_to_unclipped_mono_48khz(
    tmp_path: Path,
) -> None:
    source = tmp_path / "stereo-master.wav"
    destination = tmp_path / "reference.wav"
    mono = _sine(4.0, 44_100, amplitude=0.8)
    _write_wav(source, np.column_stack((mono, mono)), 44_100)
    source_hash = _sha256(source)

    info = prepare_reference(source, destination)

    assert _sha256(source) == source_hash
    assert info["sample_rate"] == 48_000
    assert info["channels"] == 1
    assert info["duration_seconds"] == pytest.approx(4.0, abs=1 / 44_100)
    assert info["clipping_ratio"] == 0.0
    assert info["peak_dbfs"] < 0.0
    assert sf.info(destination).subtype == "PCM_16"
    assert destination.read_bytes() != source.read_bytes()


def test_inspect_audio_rejects_truncated_wav_with_stable_error_code(tmp_path: Path) -> None:
    source = tmp_path / "truncated.wav"
    source.write_bytes(b"RIFF\x20\x00\x00\x00WAVEfmt ")

    with pytest.raises(AudioValidationError) as error:
        inspect_audio(source)

    assert error.value.code == "INVALID_WAV"


def test_prepare_reference_counts_voiced_time_instead_of_total_duration(tmp_path: Path) -> None:
    source = tmp_path / "mostly-silence.wav"
    destination = tmp_path / "reference.wav"
    samples = np.concatenate((_sine(2.5, 48_000), np.zeros(round(2.5 * 48_000))))
    _write_wav(source, samples, 48_000)
    source_hash = _sha256(source)

    with pytest.raises(AudioValidationError) as error:
        prepare_reference(source, destination)

    assert error.value.code == "REFERENCE_TOO_SHORT"
    assert _sha256(source) == source_hash
    assert not destination.exists()


def test_extract_reference_anchor_creates_worker_safe_16khz_clip_without_touching_master(
    tmp_path: Path,
) -> None:
    source = tmp_path / "normalized-master.wav"
    destination = tmp_path / "neutral-anchor.wav"
    _write_wav(source, _sine(24.0, 48_000), 48_000, subtype="PCM_16")
    source_hash = _sha256(source)

    info = extract_reference_anchor(
        source,
        destination,
        start_seconds=6.95,
        duration_seconds=12.35,
    )

    assert _sha256(source) == source_hash
    assert sf.info(destination).format == "WAV"
    assert sf.info(destination).subtype == "PCM_16"
    assert info["sample_rate"] == 16_000
    assert info["channels"] == 1
    assert info["duration_seconds"] == pytest.approx(12.35, abs=1 / 16_000)
    assert info["clipping_ratio"] == 0.0


@pytest.mark.parametrize(
    ("start_seconds", "duration_seconds", "error_code"),
    [
        (-0.1, 10.0, "ANCHOR_RANGE_INVALID"),
        (1.0, 4.99, "ANCHOR_DURATION_INVALID"),
        (1.0, 15.01, "ANCHOR_DURATION_INVALID"),
        (15.0, 10.0, "ANCHOR_RANGE_INVALID"),
    ],
)
def test_extract_reference_anchor_rejects_unsafe_ranges(
    tmp_path: Path,
    start_seconds: float,
    duration_seconds: float,
    error_code: str,
) -> None:
    source = tmp_path / "normalized-master.wav"
    destination = tmp_path / "neutral-anchor.wav"
    _write_wav(source, _sine(20.0, 48_000), 48_000, subtype="PCM_16")

    with pytest.raises(AudioValidationError) as error:
        extract_reference_anchor(
            source,
            destination,
            start_seconds=start_seconds,
            duration_seconds=duration_seconds,
        )

    assert error.value.code == error_code
    assert not destination.exists()


def test_prepare_script_requires_rights_confirmation(tmp_path: Path) -> None:
    source = tmp_path / "reference-master.wav"
    transcript = tmp_path / "reference-transcript.txt"
    destination = tmp_path / "reference.wav"
    manifest = tmp_path / "reference.manifest.json"
    _write_wav(source, _sine(4.0, 48_000), 48_000)
    transcript.write_text("Это мой голос и я разрешаю его использовать.", encoding="utf-8")

    result = subprocess.run(
        [
            "pwsh",
            "-NoProfile",
            "-File",
            str(PREPARE_SCRIPT),
            "-SourcePath",
            str(source),
            "-TranscriptPath",
            str(transcript),
            "-DestinationPath",
            str(destination),
            "-ManifestPath",
            str(manifest),
            "-PythonPath",
            sys.executable,
        ],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )

    assert result.returncode != 0
    assert "RIGHTS_CONFIRMATION_REQUIRED" in (result.stdout + result.stderr)
    assert not destination.exists()
    assert not manifest.exists()


@pytest.mark.parametrize(
    ("left_role", "right_role"),
    [
        ("source", "transcript"),
        ("source", "destination"),
        ("source", "manifest"),
        ("transcript", "destination"),
        ("transcript", "manifest"),
        ("destination", "manifest"),
    ],
)
def test_prepare_script_rejects_every_path_collision_before_writing(
    tmp_path: Path,
    left_role: str,
    right_role: str,
) -> None:
    paths = {
        "source": tmp_path / "reference-master.wav",
        "transcript": tmp_path / "reference-transcript.txt",
        "destination": tmp_path / "reference.wav",
        "manifest": tmp_path / "reference.manifest.json",
    }
    paths[right_role] = paths[left_role]
    _write_wav(paths["source"], _sine(4.0, 48_000), 48_000, subtype="PCM_16")
    if paths["transcript"] != paths["source"]:
        paths["transcript"].write_text("Точная расшифровка.", encoding="utf-8")
    protected = {
        path: path.read_bytes()
        for path in {paths["source"], paths["transcript"]}
        if path.exists()
    }

    result = subprocess.run(
        [
            "pwsh",
            "-NoProfile",
            "-File",
            str(PREPARE_SCRIPT),
            "-SourcePath",
            str(paths["source"]),
            "-TranscriptPath",
            str(paths["transcript"]),
            "-DestinationPath",
            str(paths["destination"]),
            "-ManifestPath",
            str(paths["manifest"]),
            "-PythonPath",
            sys.executable,
            "-ConfirmRights",
        ],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )

    assert result.returncode != 0
    assert "PATH_COLLISION" in (result.stdout + result.stderr)
    assert all(path.read_bytes() == content for path, content in protected.items())


def test_prepare_script_creates_private_manifest_and_preserves_master(tmp_path: Path) -> None:
    source = tmp_path / "reference-master.wav"
    transcript = tmp_path / "reference-transcript.txt"
    destination = tmp_path / "reference.wav"
    manifest_path = tmp_path / "reference.manifest.json"
    literal_transcript = "  Это мой голос; я разрешаю локальную проверку.  \n"
    _write_wav(source, _sine(4.0, 48_000), 48_000)
    transcript.write_text(literal_transcript, encoding="utf-8")
    source_hash = _sha256(source)

    result = subprocess.run(
        [
            "pwsh",
            "-NoProfile",
            "-File",
            str(PREPARE_SCRIPT),
            "-SourcePath",
            str(source),
            "-TranscriptPath",
            str(transcript),
            "-DestinationPath",
            str(destination),
            "-ManifestPath",
            str(manifest_path),
            "-PythonPath",
            sys.executable,
            "-ConfirmRights",
        ],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )

    assert result.returncode == 0, result.stdout + result.stderr
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    assert _sha256(source) == source_hash
    assert destination.exists()
    assert manifest["schemaVersion"] == 1
    assert manifest["consent"] == {"rightsConfirmed": True, "localOnly": True}
    assert manifest["source"]["sha256"] == source_hash
    assert manifest["reference"]["sha256"] == _sha256(destination)
    assert manifest["reference"]["sample_rate"] == 48_000
    assert manifest["reference"]["channels"] == 1
    assert manifest["transcript"]["text"] == literal_transcript.strip()
    assert str(tmp_path) not in manifest_path.read_text(encoding="utf-8")
    assert literal_transcript.strip() not in result.stdout
    assert str(tmp_path) not in result.stdout


def test_prepare_script_decodes_lossless_m4a_and_records_original_master(
    tmp_path: Path,
) -> None:
    ffmpeg = _find_ffmpeg()
    if not ffmpeg:
        if sys.platform == "win32":
            pytest.fail("FFmpeg is required for the Windows M4A integration test")
        pytest.skip("FFmpeg is required for the M4A integration test")

    intermediate_wav = tmp_path / "capture.wav"
    source = tmp_path / "voice-master.m4a"
    transcript = tmp_path / "reference-transcript.txt"
    destination = tmp_path / "reference.wav"
    manifest_path = tmp_path / "reference.manifest.json"
    _write_wav(intermediate_wav, _sine(4.0, 48_000), 48_000, subtype="PCM_16")
    subprocess.run(
        [
            ffmpeg,
            "-nostdin",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            str(intermediate_wav),
            "-c:a",
            "alac",
            str(source),
        ],
        check=True,
    )
    transcript.write_text("Это мой голос и точная расшифровка записи.", encoding="utf-8")
    master_hash = _sha256(source)

    result = subprocess.run(
        [
            "pwsh",
            "-NoProfile",
            "-File",
            str(PREPARE_SCRIPT),
            "-SourcePath",
            str(source),
            "-TranscriptPath",
            str(transcript),
            "-DestinationPath",
            str(destination),
            "-ManifestPath",
            str(manifest_path),
            "-PythonPath",
            sys.executable,
            "-ConfirmRights",
        ],
        cwd=REPO_ROOT,
        env={
            **os.environ,
            "PATH": str(Path(ffmpeg).parent) + os.pathsep + os.environ.get("PATH", ""),
        },
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )

    assert result.returncode == 0, result.stdout + result.stderr
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    prepared = inspect_audio(destination)
    assert _sha256(source) == master_hash
    assert manifest["source"] == {"fileName": source.name, "sha256": master_hash}
    assert manifest["decoder"]["name"] == "ffmpeg"
    assert manifest["decoder"]["version"].startswith("ffmpeg version ")
    assert len(manifest["decoder"]["sha256"]) == 64
    assert prepared["sample_rate"] == 48_000
    assert prepared["channels"] == 1
    assert prepared["clipping_ratio"] == 0.0
    assert not list(tmp_path.glob("*.decode.*"))


def test_anchor_script_creates_private_exact_transcript_bundle(
    tmp_path: Path,
) -> None:
    source = tmp_path / "normalized-master.wav"
    transcript = tmp_path / "reference-transcript.txt"
    destination = tmp_path / "neutral-anchor.wav"
    prompt_path = tmp_path / "neutral-anchor.txt"
    manifest_path = tmp_path / "neutral-anchor.manifest.json"
    _write_wav(source, _sine(24.0, 48_000), 48_000, subtype="PCM_16")
    full_transcript = (
        "Первое предложение. Второе предложение! "
        "Третье предложение? Четвёртое предложение."
    )
    expected_prompt = "Второе предложение! Третье предложение? "
    transcript.write_text(full_transcript, encoding="utf-8")
    source_hash = _sha256(source)
    transcript_hash = _sha256(transcript)

    result = subprocess.run(
        [
            "pwsh",
            "-NoProfile",
            "-File",
            str(ANCHOR_SCRIPT),
            "-SourcePath",
            str(source),
            "-TranscriptPath",
            str(transcript),
            "-DestinationPath",
            str(destination),
            "-PromptTextPath",
            str(prompt_path),
            "-ManifestPath",
            str(manifest_path),
            "-PythonPath",
            sys.executable,
            "-StartSeconds",
            "5.0",
            "-DurationSeconds",
            "10.0",
            "-FirstSentence",
            "2",
            "-SentenceCount",
            "2",
            "-ConfirmRights",
        ],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )

    assert result.returncode == 0, result.stdout + result.stderr
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    assert _sha256(source) == source_hash
    assert _sha256(transcript) == transcript_hash
    assert prompt_path.read_text(encoding="utf-8") == expected_prompt
    assert manifest["selection"] == {
        "startSeconds": 5.0,
        "durationSeconds": 10.0,
        "firstSentence": 2,
        "sentenceCount": 2,
    }
    assert manifest["prompt"]["sha256"] == _sha256(prompt_path)
    assert manifest["prompt"]["text"] == expected_prompt
    assert manifest["reference"]["sha256"] == _sha256(destination)
    assert sf.info(destination).samplerate == 16_000
    assert sf.info(destination).subtype == "PCM_16"
    assert expected_prompt.strip() not in result.stdout
    assert str(tmp_path) not in result.stdout
