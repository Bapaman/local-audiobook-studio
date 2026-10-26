# VoxCPM2 training window protocol (Format1)

This is a separate, offline worker. The corpus worker's four-file allowlist is
unchanged. Product packaging will admit exactly worker.py, protocol.py,
owned_files.py, checkpoint.py, runtime.py, frozen_layout.py, mapped_model.py and
trainer.py. The last four implement the tensor computation; the first four use
only Python's standard library. Synthetic protocol tests are not training proof.

## Launch and ownership

The parent launches `python -I -S -B -u worker.py --request <request.json>
--request-sha256 <sha> --gate <START>` inside its creation-time resource Job.
Request and START are under the exact preassigned window directory. Before START
only the pinned own worker directory is added to sys.path. The precreated START
file is opened before `voxcpm_training.worker.gated`; it permits the parent's
writer but denies delete. Only empty bytes or exactly `START\n` are accepted;
the gate times out after300seconds. No site/.pth processing is permitted.

After START, an existing plain single-link execution STOP skips computation.
Otherwise `trainer.train_one(request, phase)` is imported lazily. Explicit
dependency sites/source are admitted by the separate runtime code, not by site
discovery. It returns `None` only if STOP exists before an update, or a dictionary
with exactly `payloads` (five bounded byte strings) and `metrics`. The wrapper,
not the trainer or a tensor library, owns all publication paths. A completed
update can still be published when STOP arrives during it.

Stdout is limited to `voxcpm_training.worker.` followed by gated, loading,
training, checkpointing or ready. Stderr on failure is just the same prefix plus
failed. CLI parser diagnostics, traceback, corrected text and library output
are not forwarded. Python and native fd1/fd2 library streams are suppressed;
the progress callback writes only its checked enum to a separately held fd.

## Strict JSON and identities

All names are case-sensitive camelCase. All objects reject unknown and duplicate
keys, recursively. Integers exclude booleans, float/exponent aliases and negative
zero. Nonfinite numbers, non-normalized/empty text, traversal, reserved Windows
names, drive-relative/UNC/device paths and case-aliased inventory names fail.
IDs are32 lowercase hex; digests64, source/base commits40. Local logical paths
remain in IPC; IO uses extended local Windows spelling without OS policy changes.

`tests/fixtures/VoxCpmTrainingProtocol` contains a complete synthetic request,
result and checkpoint manifest for the mirrored C# acceptor. They describe the
second retained clip (original sourceIndex2), advancing globalStep1 to2 and
completedEpochs0 to1. The two tiny layout entries and inert source38/model7
inventories are fixtures, not the pinned product model. Product runtime admission
requires the real384-tensor layout and exact public inventories/identities.
Golden request SHA is over its canonical UTF-8 sorted-key JSON, without a newline;
real launches hash the exact written request bytes, not a reserialized object.

Request <=256KiB binds these groups (all required):

- format1; request/project/training/action/execution/window/checkpoint IDs;
  projectRoot/outputRelativeRoot; corpusVersionId/ManifestSha256/BindingSha256;
- identity: fingerprint, sourceCommit, baseRevision, sourceLockSha256,
  modelLockSha256, cacheSha256, cacheLayoutSha256, loaderSha256, recipeSha256,
  adapterLayoutSha256;
- clipIds in retained order; cursor; targetCompletedEpochs; selectedClip with
  trainingOrdinal, sourceIndex, id, sample bounds, correctedText/textSha256,
  audioRelativePath/audioSha256; original source audit identity;
- parentCheckpoint or null; dependencySites; sourceRoot/sourceFiles (38),
  modelRoot/modelFiles (7); runtimeInventoryPath/Sha256 (reference only, never
  the full dependency inventory); cache path/bytes/SHA/layout SHA;
  parameterLayout sorted by ordinal ASCII name, shape and F32 dtype.

Cursor has globalStep/completedEpochs/epochSize/nextRetainedOrdinal/nextClipId.
It must satisfy globalStep=completedEpochs*N+ordinal and match clipIds[ordinal].
The only result transition advances by one, rolling ordinal to0 at an epoch
boundary. Parent cursor equals the before cursor; a null parent requires step0.
The product store admits at most512 accepted checkpoints per lineage.

## Exclusive publication and parent acceptance

Window: `voxcpm-training/actions/<action>/executions/<execution>/windows/<window>`.
Candidate: `checkpoints/<preassigned-checkpoint>/` under that window. Exactly:

| File | Upper bound |
|---|---:|
| lora_weights.safetensors | 8MiB |
| optimizer.pth | 16MiB |
| scheduler.pth | 64KiB |
| rng.pth | 64KiB |
| metadata.json | 256KiB |
| manifest.json | 64KiB |
| READY | 65bytes |

Each file is CREATE_NEW/exclusive, flushed/fsynced. Manifest records exact
request SHA and IDs, parent manifest SHA/null, corpus/identity/layout,
before/after cursor, every payload's size/SHA, finite loss and nonnegative finite
gradientNorm. Metadata has exactly format1, optimizerNames in layout order,
recipeSha256 and adapterLayoutSha256. Checkpoint READY is SHA256(exact manifest
bytes)+LF and is written last. The complete bundle is then preflighted.

Only afterwards is result.json (<=1MiB) published, followed by its own READY
(SHA256(exact result bytes)+LF). Result binds the request and identities, plus
checkpoint reference (id, manifestRelativePath/SHA, cursor) or null and stopped.
Null requires stopped=true and does not create a checkpoint directory. A failed
candidate may leave diagnostic orphan files, but never a ready window. There is
no overwrite, largest-directory discovery, fallback or automatic adoption.

READY/stdout alone grants no acceptance. The parent additionally requires exit0,
proven Job-zero, unchanged frozen cache, exact namespace/request/identity and
independent bounded validation before atomically advancing head and cursor.

## Tensor preflight grammar (no unpickling)

Safetensors header <=1MiB, exact expected names/F32/rank2 LoRA shapes, contiguous
byte accounting from offset0 through exact EOF, no overlaps/gaps/trailing bytes,
and all float32 values finite. No __metadata__ or additional tensors are allowed.

Torch archives are ZIP_STORED, unencrypted, at most2048 unique members, with
central/local bounded nonoverlapping ranges and cumulative uncompressed bytes
within the payload budget. No extraction. Torch's UTF-8/data-descriptor flags
0x808 are allowed. Prefix is `archive/`; exact service members are data.pkl,
.format_version=1, .storage_alignment=64, byteorder=little, version=`3\n`, and
.data/serialization_id. Other entries must be exactly the referenced
`data/<canonical decimal storage key>`. CRC/size agreement is checked on reads.

data.pkl <=1MiB is inspected with pickletools, never pickle.load(s). Only
protocol2 primitive construction, memo reads/writes, tuples/lists/dicts,
OrderedDict(), storage BINPERSID and _rebuild_tensor_v2 are interpreted. Allowed
globals: collections.OrderedDict, torch._utils._rebuild_tensor_v2,
torch.FloatStorage/ByteStorage/LongStorage. All other opcodes/globals/reducers
fail; <=200001 instructions, <=8192 stack/container entries, <=32768 memo slots.
The separate memo bound accommodates the real optimizer's7709 entries. LONG1/4
are bounded integers (needed for unsigned MT19937 words), never allocations.

Storage references must be `(storage, allowedStorageGlobal, decimalKey, cpu,
numel)` within the byte budget. Rebuild requires offset0, contiguous allowed
shape/stride, requires_grad=false and empty hooks. Every reference must match one
actual storage of exactly numel*itemsize bytes. No announced oversized storage
is handed to a tensor library.

Optimizer has state and exactly one param_groups entry. Parameter indices are
the full ordered layout; state can be a nonempty subset, each with a unique
finite F32 scalar step in1..globalStep and shape-matched finite exp_avg/exp_avg_sq
(latter nonnegative). Recipe settings are exact. A parameter step need not equal
globalStep. StepLR state has exact fields/settings, last_epoch=globalStep and
_step_count=globalStep+1; LR uses repeated0.99 multiplication at100-step boundaries.

RNG fields are python, torch, numpy_keys, numpy_kind, numpy_pos,
numpy_has_gauss, numpy_cached_gaussian. Python state is version3/tuple625/Gaussian;
Torch is uint8[5056], NumPy keys int64[624] in uint32 range, kind MT19937,
position0..624, Gaussian flag0/1 and finite cached value. This product schema
does not adopt old private checkpoints (which used numpy_position).

Only the later owned trainer may load the admitted bytes with
weights_only=true/map_location=cpu; no weights_only=false fallback. The pinned
base AudioVAE archive is a separate asset, never admitted as a checkpoint.

## Windows handles

Ancestors are pinned root-to-leaf with FILE_LIST_DIRECTORY, READ|WRITE sharing
without DELETE, OPEN_REPARSE_POINT|BACKUP_SEMANTICS. Files are opened with
OPEN_REPARSE_POINT, inspected by handle, reject reparse/directory/hardlink and
normally allow READ sharing only. Newly created checkpoint directories are
pinned before child writes. Mutable START alone uses READ|WRITE sharing. The
separate frozen-cache mapping/lease policy belongs to runtime admission.

## Mapped training implementation

`runtime.install_dependencies` is called only by the post-START backend factory.
It pins the explicit source/model files, appends admitted dependency sites
without processing .pth, sets offline/two-thread environment, and installs
voxcpm/model/training namespace packages. It does not import voxcpm.core or call
the public from_local full-weight loader. Parent admission remains responsible
for full runtime/source/model hashes before releasing START.

`frozen_layout.read_main_header` reads <=1MiB of header and no tensor payload.
It accounts every BF16/F32 base interval through EOF. Public q/k/v/o_proj
weights under base_lm/residual_lm/feat_decoder.estimator independently derive
the384 adapter entries (1130496 float32 scalars). Their sorted ASCII-name order
is the optimizer order; it is intentionally distinct from frozen named-parameter
traversal order. The real pinned base header yields adapter layout SHA
e6adf47f16d1088f4c958f341215ec1e4455816b605165bfb7a1bd2f3b3f143c.

`mapped_model` constructs an FP32 meta model with exactly two static KV setup
calls suppressed; the monkeypatch is restored even on construction failure.
Base keys/shapes, adapter count/layout, persistent/nonpersistent buffers, frozen
traversal digest and9536873992-byte extent must agree before shared mapping.
Every frozen parameter is a nontrainable view into that retained mapping with
the exact expected storage pointer and offset. It is never copied into, rebuilt,
converted wholesale or loaded with generic model.load_state_dict.

Only adapters are allocated. Known small state is reconstructed: three RoPE
modules,192 LoRA scaling buffers and VAE weight_norm derived tensors. The separate
376951122-byte pinned AudioVAE asset is loaded weights_only inside the Job to
validate its inventory and copy only int32[3] sr_bin_boundaries
[20000,30000,40000]. Unexpected buffers or unregistered tensor attributes fail.
Writable-mapping immutability still requires the parent's full before/after cache
SHA and exclusive cross-process lease; requires_grad=false is not that barrier.

`trainer.recipe()` fixes CPU FP32/batch1, seed42, LM+DiT rank2/alpha2/dropout0,
projection-off, AdamW, StepLR, full official packer and no shuffle/truncation.
Canonical recipe bytes are part of runtime identity. The exact retained canonical
mono16k PCM16 bytes are decoded with /32768.0 once, without resampling or other
processing. Text is the request's corrected text. Before the packer, full token
length must fit both config.max_length and LM RoPE capacity; equality fits.
Packer output tracks, feature shape, full loss mask and stop label are checked.

Forward uses only seven tracks, progress0.0/sample_generate=false. Both losses,
gradients, clipped norm, resulting adapters and optimizer state must be finite;
at least one adapter must change. Optimizer update precedes scheduler update;
zero_grad(set_to_none=true) precedes serialization. STOP before optimizer update
discards the unaccepted computation; STOP after it allows that completed step.

Resume validates the exact accepted parent manifest SHA and complete bounded
bundle against the current immutable lineage. It reconstructs the previous
window's binding from that SHA-bound manifest, never from an old mutable request.
Verified immutable payload bytes are loaded through BytesIO using
weights_only=true/map_location=cpu/mmap=false, safetensors through bytes. Only
the exact adapter keys are copied; then optimizer and scheduler are restored
and compared, finally Python/NumPy/Torch RNG restored and compared before packer.
Partial optimizer states are allowed with each parameter's own counter.

`save_training_state` returns five bounded byte payloads for the wrapper's
exclusive/fsynced publication, rather than writing an independently discoverable
directory. It uses BytesIO handles, avoiding tensor-library project path limits.
The root argument binds the preassigned window but is not an alternate output
authority. It runs the same tensor grammar checks before returning payloads.

Offline tests exercise policy, orchestration and restore with injected slow
tensor primitives. They do not establish actual Torch numerical compatibility,
Job peak memory, full-corpus coverage or voice quality; these require the later
serialized real hardware acceptance.

## Explicit product runtime and public lock

Trusted startup descriptor (<=32KiB) has exactly pythonPath, pythonBaseRoot,
dependencySites (1..2 ordered explicit roots), workerRoot, sourceRoot, modelRoot,
trainingLockPath, runtimeInventoryPath and cachePath. Browser requests cannot
choose them. Parsing does not start a process or create a runtime. The descriptor
path and exact raw-byte SHA are retained and rechecked at every admission.

`eng/locks/voxcpm2-training.lock.json` contains only public source/model metadata,
fixed recipe, sorted adapter layout, eight shipped worker files and frozen-cache
identity. It contains no speech, transcript, user path or private checkpoint.
The38 source-file entries are the original exact public subset; LICENSE has its
own separate pinned entry because it was not among those38. The clean source
directory must contain exactly that union, no bytecode or additional importables.
Seven model files are pinned independently; only .gitattributes, README.md,
artifact-lock.json and READY may additionally exist as inert download metadata.
Those names never authorize model loading or checkpoint adoption.

Runtime inventory <=64MiB has exact {schemaVersion:1,files:[{path,bytes,sha256}]}.
It is the closed union of all files under PythonBaseRoot, explicit dependency
sites and the exact eight-file worker root, plus interpreter siblings and venv
pyvenv.cfg. No extension filters or omitted bytecode/native assets. Case aliases,
missing/extra files and new files on a second enumeration fail. A venv home must
equal the explicit base root; non-venv Python must belong to that base root.
External fallback sites are allowed only when explicitly configured/inventoried.
Required package entrypoints are present, but admission never imports them.
Source/model and descriptor/lock/inventory handles are pinned separately.
Per-file FileStream buffering is1byte, not64KiB for each of tens of thousands.

Runtime fingerprint is SHA256 of ASCII `voxcpm2-training-runtime-v1\n` followed by
descriptorSHA, runtimeInventorySHA, trainingLockSHA, recipeSHA, loaderSHA and
adapterLayoutSHA, each followed by LF. SourceLockSHA and ModelLockSHA hash the
canonical sorted JSON subobjects. LoaderSHA hashes ASCII
`voxcpm2-training-loader-v1\n` plus sorted lines `filename|bytes|sha256\n` for the
eight compiled worker pins. LF checkout rules preserve those exact source bytes.
The independently header-derived model layout must match the public lock.

Only positive advisory readiness is coalesced/cached for at most300seconds,
after disposing all pins. Negative checks are not cached. Identity and actual
launch always perform fresh admission. A detected cache mismatch latches that
runtime instance unavailable; it is not silently repaired or rebuilt. A fresh
explicitly configured instance still requires complete cache validation before
any native window can run.

The long-lived cache lease opens exactly cachePath+'.voxcpm-training.lock' with
OPEN_ALWAYS (never truncate), READ|WRITE access and no sharing, validates plain
single-link identity/zero length and pins its ancestors. The existing cache
handle uses generic READ with READ|WRITE sharing, no DELETE, compatible with the
child's writable shared mapping. Full expected size/SHA are checked from that
same held stream before launch and again only after proven Job-zero, including
failed/stopped windows. Runtime/import inventories may not include the cache or
its lock. Unknown native cleanup must retain this lease; Dispose is not a
substitute for Job-zero.

`scripts/voxcpm-training/New-LocalRuntime.ps1` accepts only explicit existing
roots, verifies public source/worker bytes and base header-derived layout,
creates a new clean source copy plus license, inventories installed dependencies
and writes a new descriptor. It never installs, launches Python, processes .pth,
downloads, overwrites old roots, duplicates the base model or rebuilds the cache.
Full model/cache admission remains a fresh product-launch check, not a claim
made by preparation alone.
