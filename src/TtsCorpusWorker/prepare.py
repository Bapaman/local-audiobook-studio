"""Clean PCM preparation; optional numerical libraries load only after START."""
import io
import wave
from protocol import (MAX_CLIPS, MAX_SAMPLES, VERSION, ProtocolError, clip_identity, io_path, no_reparse, require, safe_path,
                      sha256_file, write_bytes, write_json)


def read_wav(path, rate, samples):
    path = io_path(path)
    no_reparse(path)
    require(type(samples) is int and 0 < samples <= MAX_SAMPLES * (3 if rate == 48000 else 1))
    require(path.stat().st_size <= samples * 2 + 1024 * 1024)
    try:
        with wave.open(str(path), "rb") as source:
            require(source.getnchannels() == 1 and source.getsampwidth() == 2)
            require(source.getframerate() == rate and source.getcomptype() == "NONE")
            require(source.getnframes() == samples)
            data = source.readframes(samples + 1)
            require(len(data) == samples * 2)
            return data
    except (wave.Error, EOFError) as error:
        raise ProtocolError("tts_corpus.worker.invalid_audio") from error


def write_wav(path, pcm, rate=16000):
    require(len(pcm) > 0 and len(pcm) % 2 == 0)
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as target:
        target.setnchannels(1)
        target.setsampwidth(2)
        target.setframerate(rate)
        target.writeframes(pcm)
    return write_bytes(path, buffer.getvalue())


def plan_clips(rms, total_samples):
    """Half-open contiguous 16k sample spans; pauses use 10ms RMS frames."""
    require(type(total_samples) is int and 0 < total_samples <= MAX_SAMPLES)
    require(len(rms) == (total_samples + 159) // 160)
    pauses = []
    first = None
    for index, value in enumerate(rms):
        require(isinstance(value, (int, float)) and 0 <= value < float("inf"))
        if value < 0.008:
            if first is None:
                first = index
        elif first is not None:
            if index - first >= 10:
                pauses.append((first + index) * 80)
            first = None
    if first is not None and len(rms) - first >= 10:
        pauses.append(min(total_samples, (first + len(rms)) * 80))
    spans = []
    start = 0
    while total_samples - start > 256000:
        high = min(start + 248000, total_samples - 64000)
        candidates = [p for p in pauses if start + 128000 <= p <= high]
        end = min(candidates, key=lambda p: (abs(p - (start + 224000)), p)) if candidates else min(start + 224000, total_samples - 64000)
        spans.append((start, end))
        require(len(spans) < MAX_CLIPS)
        start = end
    spans.append((start, total_samples))
    return spans


def prepare(request, output):
    import numpy as np
    from scipy.signal import resample_poly
    source = safe_path(request["projectRoot"], request["sourceRelativePath"])
    require(sha256_file(source, request["sourceSampleCount"] * 2 + 1024 * 1024) == request["sourceSha256"])
    original = read_wav(source, 48000, request["sourceSampleCount"])
    # Polyphase antialiasing is part of resampling; no additional audio processing.
    signal = np.frombuffer(original, dtype="<i2").astype(np.float32) / 32768.0
    clean = resample_poly(signal, 1, 3)
    require(clean.ndim == 1 and len(clean) == (len(signal) + 2) // 3 and bool(np.isfinite(clean).all()))
    pcm = np.clip(np.rint(clean * 32768.0), -32768, 32767).astype("<i2").tobytes()
    # Plan the actual saved PCM, including the short final RMS frame.
    saved = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
    full = len(saved) // 160 * 160
    rms = np.sqrt(np.mean(saved[:full].reshape(-1, 160) ** 2, axis=1)).tolist()
    if full < len(saved):
        rms.append(float(np.sqrt(np.mean(saved[full:] ** 2))))
    return publish_pcm_plan(request, output, pcm, rms)


def publish_pcm_plan(request, output, pcm, rms):
    """Publish exact PCM slices; callable without numerical or model dependencies."""
    total_samples = len(pcm) // 2
    require(len(pcm) % 2 == 0 and total_samples == (request["sourceSampleCount"] + 2) // 3)
    spans = plan_clips(rms, total_samples)
    clean_sha = write_wav(output / "clean.wav", pcm)
    clips_dir = safe_path(request["projectRoot"], request["outputRelativeRoot"] + "/clips")
    clips_dir.mkdir(exist_ok=False)
    clips = []
    for index, (start, end) in enumerate(spans):
        clip_id = clip_identity(request["actionId"], index)
        audio_sha = write_wav(clips_dir / (clip_id + ".wav"), pcm[start * 2:end * 2])
        clips.append(dict(id=clip_id, index=index, startSample=start, endSample=end,
                          audioRelativePath=request["outputRelativeRoot"] + "/clips/" + clip_id + ".wav",
                          audioSha256=audio_sha))
    disk = dict(schemaVersion=VERSION, actionId=request["actionId"], sourceSha256=request["sourceSha256"],
                runtimeFingerprint=request["runtimeFingerprint"], totalSamples=total_samples, clips=clips,
                cleanAudioRelativePath=request["outputRelativeRoot"] + "/clean.wav", cleanAudioSha256=clean_sha)
    plan_sha = write_json(output / "plan.json", disk)
    return dict(planRelativePath=request["outputRelativeRoot"] + "/plan.json", sha256=plan_sha,
                sourceSha256=request["sourceSha256"], runtimeFingerprint=request["runtimeFingerprint"],
                totalSamples=total_samples, clips=clips)
