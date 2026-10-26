# Saved VoxCPM2 adapter inference engine

This note documents the first saved-adapter inference path, including immutable input binding and output acceptance. Later corpus-reference and configurable-generation work extends this protocol. Current feature status and known quality limits are described in the [project overview](../PROJECT.md).

## Modules

| Module | Owns | Does not own |
|---|---|---|
| `src/VoxCpmInferenceWorker/inference_contract.py` | Immutable checkpoint binding, unchanged text/token budget, KV byte calculation, natural-EOS decision | Checkpoint adoption, files, model loading |
| `src/VoxCpmInferenceWorker/synthesis.py` | Two bounded inference KV buffers, tokenizer/EOS observations, direct generation and waveform inspection | Reference/RVC generation, final WAV publication |
| `src/VoxCpmInferenceWorker/mapped_backend.py` | Immutable adapter-byte preflight/copy, eval/no-grad model and one-segment mapping pin | Windows Job, full runtime inventory, exclusive frozen-cache lease |

The admitted training worker is reused read-only: `runtime.install_dependencies` supplies its closed offline namespaces and `mapped_model.load_training_model` reconstructs frozen FP32 views. No training-worker file, optimizer, scheduler, RNG checkpoint state or main-weight materializer is changed or used to synthesize.

## Product host call sequence

This internal API is called by the separate gated `worker.py`, only inside `VoxCpmInferenceNativeExecutor`, after the parent has admitted and pinned the runtime, exact accepted checkpoint, corpus/runtime identity and immutable payload bytes. It is not a private ML probe. Four exact scripts and a separate byte-pinned lock are packaged outside the unchanged eight-file training worker root.

1. Capture a distinct VoxCPM saved-model binding; do not reinterpret the RVC `SavedVoiceBinding` or resolve mutable “latest”.
2. Acquire the existing shared compute slot, exact runtime file pins and exclusive frozen-cache lease. Verify its full hash. Admit/check the Windows Job before opening the worker START gate.
3. Use isolated Python `-I -S -B`; the host explicitly makes only its admitted worker modules importable. No `PYTHONPATH`, `.pth` execution, dependency install, network or full weight load.
4. Call `load_mapped_backend(runtime_request, binding, adapter_payload)` and `backend.synthesize(whole_segment_text)`. The backend is single-use and closes its mapping pin on success/failure. `with`/explicit `close()` are idempotent lifetime options if generation has not started.
5. Accept the result only after worker exit 0, proven owned Job-zero, unchanged full cache hash and independent waveform/evidence validation. Then publish the WAV through existing atomic artifact ownership. Unknown cleanup retains every owner; failure must not fall back to plain base TTS.

`runtime_request` supplies the existing admitted loader fields: source/model roots and inventories, dependency sites, FP32 cache metadata, parameter layout and training runtime identity. `CheckpointBinding` additionally captures project/checkpoint/step, exact accepted manifest/adapter SHA and runtime/recipe/layout SHA. The engine checks agreement before any heavy import, but the **parent is still the authority for accepted lineage/READY and full file/runtime admission**.

The host preserves the exact policy: 2 logical CPUs; aggregate cap 13958643712 bytes (13 GiB); 300 seconds per native window; at least 16 GiB free before launch and 3 GiB reserve. Synthetic tests are not evidence that real inference fits that policy.

Filesystem SHA verification uses one pooled temporary1MiB block per active hash, preserving full-byte checks and unbuffered retained handles. Successful fresh identity capture primes the shared300s advisory readiness snapshot; inference readiness still pins its exact scripts. Native admission remains fresh, and the shared cache-changed latch overrides advisory readiness. The HTTP acceptance helper bounds Preview preparation separately at900s (metadata300s), without extending the native300s window.

## Completeness and quality

Profile `voxcpm2-saved-adapter-cpu-v1` uses one unchanged short segment, the selected LoRA directly, seed 42, 10 timesteps and CFG 2.0. It uses no prompt recording, RVC, automatic retry, denoise, resampling or clipping. Overlong segments fail rather than being shortened; a parent book segmenter must preserve/split the complete text.

Both public tokenizer observations must match the full input and exact token list. EOS is observed at the pinned public stop head, including the valid final-patch EOS case. Exhausting the patch budget without natural EOS is failure even if the public library returned a waveform. Cache allocation uses the exact prefill plus effective patch limit, never the public 8192-position default.

Evidence contains hashes/counts, selected checkpoint identity, natural termination and finite bounded 48 kHz waveform statistics. It contains no raw book text. This proves input and generation boundaries, **not** that every word is audibly correct or that the voice sounds good. Those remain a real normal-product run plus listening/transcription; 29 training updates alone do not establish quality.

## Safe tests

From the repository root, use an existing Python 3.11 executable:

```powershell
python -I -S -B -m unittest discover -s tests/VoxCpmInferenceWorker.Tests -v
python -I -S -B -m unittest discover -s tests/VoxCpmTrainingWorker.Tests -v
```

These suites use stdlib and explicit tensor/model doubles only. A fresh isolated import checks that Torch, NumPy, safetensors and VoxCPM were not imported. They do not load user weights/recordings, start an App, or produce actual audio.

This note describes the first saved-adapter inference path. Later reference conditioning and configurable generation extend that protocol. See the [project overview](../PROJECT.md) for the current scope and limitations.
