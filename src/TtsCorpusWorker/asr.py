"""Local Whisper CPU adapter. Importing this module never imports ML libraries."""
import math
from protocol import VERSION, require, safe_path, validate_transcript, write_json


def normalize_output(result, duration):
    require(isinstance(result, dict) and isinstance(result.get("text"), str))
    text = " ".join(result["text"].split())
    chunks = result.get("chunks", [])
    require(isinstance(chunks, list) and len(chunks) <= 2048)
    words, warnings = [], []
    for index, chunk in enumerate(chunks):
        require(isinstance(chunk, dict) and isinstance(chunk.get("text"), str))
        times = chunk.get("timestamp")
        require(isinstance(times, (list, tuple)) and len(times) == 2)
        start, end = times
        require(type(start) in (int, float) and math.isfinite(start))
        start = min(duration, max(0.0, float(start)))
        if end is None:
            require(index == len(chunks) - 1)
            warnings.append("tts_corpus.asr.final_word_end_missing")
        else:
            require(type(end) in (int, float) and math.isfinite(end) and end >= times[0])
            end = min(duration, max(start, float(end)))
        word = " ".join(chunk["text"].split())
        if word:
            words.append(dict(text=word, start=start, end=end))
    # Deterministic heuristic, not a confidence or quality score.
    if not text or (duration >= 8 and sum(c.isalnum() for c in text) < duration * 2):
        warnings.append("tts_corpus.asr.sparse_transcript")
    payload = dict(schemaVersion=VERSION, clipId="0" * 32, draftText=text, warningCodes=warnings, words=words)
    validate_transcript(payload, duration)
    return text, words, warnings


def load_pipeline(model_root):
    import torch
    from transformers import AutoModelForSpeechSeq2Seq, AutoProcessor, pipeline
    torch.set_num_threads(2)
    torch.set_num_interop_threads(1)
    torch.use_deterministic_algorithms(True, warn_only=False)
    model = AutoModelForSpeechSeq2Seq.from_pretrained(
        model_root, local_files_only=True, trust_remote_code=False, use_safetensors=True,
        low_cpu_mem_usage=False, torch_dtype=torch.float32, attn_implementation="eager")
    model.to("cpu")
    model.eval()
    processor = AutoProcessor.from_pretrained(model_root, local_files_only=True, trust_remote_code=False)
    return pipeline("automatic-speech-recognition", model=model, tokenizer=processor.tokenizer,
                    feature_extractor=processor.feature_extractor, device=-1, torch_dtype=torch.float32,
                    chunk_length_s=30, stride_length_s=(5, 5))


def transcribe(request, clip, engine):
    import numpy as np
    from prepare import read_wav
    count = clip["endSample"] - clip["startSample"]
    pcm = read_wav(safe_path(request["projectRoot"], clip["audioRelativePath"]), 16000, count)
    waveform = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
    result = engine({"array": waveform, "sampling_rate": 16000}, batch_size=1, num_workers=0,
                    return_timestamps="word", generate_kwargs=dict(language="russian", task="transcribe",
                    num_beams=1, do_sample=False, condition_on_prev_tokens=False))
    text, words, warnings = normalize_output(result, count / 16000)
    relative = request["outputRelativeRoot"] + "/texts/" + clip["id"] + ".json"
    payload = dict(schemaVersion=VERSION, clipId=clip["id"], draftText=text, warningCodes=warnings, words=words)
    sha = write_json(safe_path(request["projectRoot"], relative), payload)
    return dict(plan=clip, textRelativePath=relative, textSha256=sha, draftText=text, warningCodes=warnings, words=words)
