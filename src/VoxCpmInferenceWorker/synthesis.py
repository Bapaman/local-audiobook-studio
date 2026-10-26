"""One direct adapter segment; no imports, model loading or file publication."""
from inference_contract import (CONTROLS_PROFILE, MAX_KV_BYTES, PROFILE, REFERENCE_PROFILE, ReferenceAudio,
                                StopEvidence, integer, kv_cache_bytes, plan_synthesis, require,
                                validate_controls, validate_text)


def _cpu_f32(tensor, torch):
    require(isinstance(tensor, torch.Tensor) and tensor.dtype == torch.float32
            and tensor.device.type == 'cpu')


def synthesize_loaded(model, torch, text, binding, reference=None, controls=None):
    """Caller owns admitted model/runtime/Job; return audio only on natural EOS."""
    binding.validate()
    validate_text(text)  # Reject before invoking any tokenizer/library code.
    if controls is not None:
        require(reference is None or type(reference) is ReferenceAudio)
        controls = dict(validate_controls(controls, reference.binding if reference else None))
    encoder = None
    if reference is not None:
        require(type(reference) is ReferenceAudio)
        reference.validate()
        for value, expected in ((model._encode_sample_rate, 16000), (model.audio_vae.sample_rate, 16000),
                                (model.chunk_size, 640), (model.audio_vae.chunk_size, 640),
                                (model.patch_size, 4), (model.feat_dim, 64), (model.audio_vae.latent_dim, 64)):
            integer(value, expected, expected)
        encoder = model._encode_wav
        require(callable(encoder))
    require(model.training is False and all(p.requires_grad is False for p in model.parameters()))
    lms = (model.base_lm, model.residual_lm)
    require(all(lm.kv_cache is None for lm in lms))
    limits = [integer(model.config.max_length, 1, 65536)]
    limits.extend(integer(lm.config.max_position_embeddings, 1, 65536) for lm in lms)
    tokenizer = model.text_tokenizer
    plan = plan_synthesis(text, tokenizer(text), min(limits), reference.binding if reference else None)
    sizes = [kv_cache_bytes(lm.config, plan.cache_capacity) for lm in lms]
    require(sum(sizes) <= MAX_KV_BYTES)
    require(type(model.sample_rate) is int and model.sample_rate == 48000)
    samples_per_patch = (integer(model.patch_size, 1, 64)
                         * integer(model._decode_chunk_size, 1, 48000))
    observed = StopEvidence(plan.patch_limit)
    token_calls, encode_calls, hook = 0, 0, None

    def checked_tokenizer(target):
        nonlocal token_calls
        require(type(target) is str and target == plan.text and token_calls < 2)
        tokens = tokenizer(target)
        require(type(tokens) is list and all(type(t) is int for t in tokens)
                and tuple(tokens) == plan.tokens)
        token_calls += 1
        return tokens

    def stopped(module, inputs, logits):
        _cpu_f32(logits, torch)
        require(tuple(logits.shape) == (1, 2) and bool(torch.isfinite(logits).all().item()))
        observed.observe(logits.detach().argmax(dim=-1)[0].item())

    def checked_encoder(wav_path, padding_mode='right', trim_silence_vad=False):
        nonlocal encode_calls
        require(type(wav_path) is str and wav_path == reference.path and encode_calls == 0
                and padding_mode == 'right' and trim_silence_vad is False)
        encode_calls += 1
        # Librosa probes audioread before preferring soundfile, even for WAV.
        # Keep the admitted canonical decoder and fail closed on its failure.
        import audioread
        backends = audioread.available_backends
        require(callable(backends))
        audioread.available_backends = lambda flush_cache=False: []
        try:
            features = encoder(wav_path, padding_mode=padding_mode, trim_silence_vad=trim_silence_vad)
        finally:
            audioread.available_backends = backends
        _cpu_f32(features, torch)
        require(tuple(features.shape) == (reference.binding.patches, 4, 64)
                and bool(torch.isfinite(features).all().item()))
        return features

    try:
        for lm, size in zip(lms, sizes):
            lm.setup_cache(batch_size=1, max_length=plan.cache_capacity,
                           device='cpu', dtype=torch.float32)
            cache = lm.kv_cache
            require(cache is not None and cache.max_length == plan.cache_capacity)
            _cpu_f32(cache.kv_cache, torch)
            require(cache.kv_cache.numel() * cache.kv_cache.element_size() == size)
        model.text_tokenizer = checked_tokenizer
        if reference is not None:
            model._encode_wav = checked_encoder
        hook = model.stop_head.register_forward_hook(stopped)
        with torch.inference_mode():
            audio = model.generate(
                target_text=plan.text, prompt_text='', prompt_wav_path='',
                reference_wav_path=reference.path if reference else '',
                min_len=2, max_len=256,
                inference_timesteps=controls['timesteps'] if controls is not None else 10,
                cfg_value=controls['cfg'] if controls is not None else 2.0,
                retry_badcase=False, retry_badcase_max_times=1,
                retry_badcase_ratio_threshold=6.0, trim_silence_vad=False,
                seed=controls['seed'] if controls is not None else 42)
        patches = observed.finish()
        require(token_calls == 2)
        require(encode_calls == (1 if reference else 0))
        _cpu_f32(audio, torch)
        require(len(audio.shape) == 2 and audio.shape[0] == 1
                and 0 < audio.shape[1] <= patches * samples_per_patch)
        require(bool(torch.isfinite(audio).all().item()))
        peak = float(audio.detach().abs().max().item())
        require(0 < peak <= 1.0)
        evidence = dict(profileId=REFERENCE_PROFILE if reference else PROFILE, seed=42, binding=binding.as_dict(),
                        textSha256=plan.text_sha256, tokensSha256=plan.tokens_sha256,
                        textCharacters=len(plan.text), tokenCount=len(plan.tokens),
                        cacheCapacity=plan.cache_capacity, cacheBytes=sum(sizes),
                        effectivePatchLimit=plan.patch_limit, generatedPatches=patches,
                        naturalEos=True, sampleRate=model.sample_rate,
                        samples=audio.shape[1], peak=peak)
        if reference is not None:
            evidence.update(reference=reference.binding.as_dict(), referencePatches=reference.binding.patches,
                            referencePrefixSlots=reference.binding.prefix_slots)
        if controls is not None:
            evidence.update(profileId=CONTROLS_PROFILE, seed=controls['seed'], controls=controls)
        return audio.detach(), evidence
    finally:
        try:
            if hook is not None:
                hook.remove()
        finally:
            model.text_tokenizer = tokenizer
            if reference is not None:
                model._encode_wav = encoder
            for lm in lms:
                lm.kv_cache = None
