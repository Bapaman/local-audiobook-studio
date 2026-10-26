# Local TTS corpus worker protocol v1

This worker prepares a draft corpus from the project's accepted PCM16 mono48k
WAV. It does not train a voice, approve text, assess quality, upload audio, or
install dependencies. The native parent owns project/source/runtime pins and the
shared ComputeGate for the complete execution, including output validation and
confirmed Job-zero. The JSON below is trusted server IPC, never browser input.

## Launch and publication

The parent creates a fresh owned directory under
`tts-corpus/actions/{actionId}/executions/{executionId}/windows/{windowId}` and writes `request.json`
plus an empty `START` file there. The parent supplies the SHA256 of the exact
request bytes, launches the pinned interpreter with
`-I -S -B -u <workerRoot>/worker.py --request <absoluteRequestPath> --request-sha256 <sha> --gate <absoluteStartPath>`,
and assigns creation-time native Job limits before releasing START. Python opens
the pre-created gate and emits `tts_corpus.worker.gated`. The parent writes exactly
the ASCII bytes `START\n` to the same file and flushes it; do not replace the gate
file or write a BOM/CRLF. Gate waiting has a 300-second upper bound. Parent native
limits remain authoritative: CPU affinity <=2, memory13958643712 bytes,
reserve3221225472 bytes, deadline300 seconds, kill-on-close Job.

Before START, only the stdlib and the parent's pinned worker directory are
importable. After START, the worker appends one or two explicitly supplied local
dependency sites with `sys.path.append`; it never calls `site.addsitedir` or runs
`.pth` files. Model files are size/hash checked before ML import. Offline flags
and CPU thread environment variables are set before optional library imports.
The parent must independently validate/pin the interpreter, scripts, dependency
inventory and exact public model lock; request-supplied hashes alone do not admit
an arbitrary runtime. `eng/locks/whisper-large-v3-turbo.lock.json` contains only
public provenance and the pinned eleven-file model inventory, with no machine
paths or personal corpus claims. Its `files` array maps directly to `modelFiles`.

The product native parent adds `-B` to prevent bytecode/cache writes (`-I`
ignores `PYTHONDONTWRITEBYTECODE`). Its configured worker root must contain exactly
the four shipped `.py` files, without `__pycache__`. Existing bytecode anywhere
in the configured Python base or dependency sites is included in the full pinned
inventory. This extra interpreter flag does not change the v1 IPC schema.

All worker outputs use exclusive creation, flush and `fsync`. The worker publishes
`result.json` then `READY` last. READY contains the lowercase SHA256 of the exact
result bytes and one LF. JSON produced by the worker is UTF-8 without BOM, keys
sorted, compact separators, non-ASCII preserved. Parent request JSON need not use
the same serialization: its hash always binds the exact bytes supplied.

Each invocation publishes one preparation or one ASR window of at most eight
clips. Its output directory must be fresh: exclusive files cannot be reused by a
second invocation. Action ID is the immutable corpus payload namespace. Execution
ID is stable throughout a start/resume run and rotates on resume. Native executor
creates a fresh windowId for each RunAsync call. The shared STOP file lives at
`tts-corpus/actions/{actionId}/executions/{executionId}/STOP`, fencing every window
in that execution. Preparation references can point to one exact earlier window
in any execution of the same action. There is no latest-file scan or cross-action
adoption. A resumed ASR request never reprocesses accepted clips.

The only stdout lines are `tts_corpus.worker.gated`, optionally
`tts_corpus.worker.preparing` or `tts_corpus.worker.transcribing`, and
`tts_corpus.worker.ready`. On worker failure, stderr contains only
`tts_corpus.worker.failed` and the exit code is1. Optional libraries' Python
streams and native fd1/fd2 writes are suppressed. Parent must still bound both
streams and reject unexpected output/oversized lines. Full text lives only in
owned JSON payloads. Invalid command-line parsing exits nonzero; normal parent
arguments are fixed by this contract.

## Request

All fields are required, camelCase, and unknown fields are rejected. Maximum
request/plan JSON size is1 MiB. All IDs are canonical lowercase Guid N: exactly32
hexadecimal characters without hyphens, matching Application record-store IDs.
SHA values/fingerprints are64 lowercase hexadecimal characters. JSON duplicate
keys, non-finite constants, and booleans in integer fields fail closed.

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Integer1 |
| `requestId` | Fresh opaque invocation identity, bound in result |
| `projectId`, `corpusId`, `actionId`, `executionId` | Application identities |
| `windowId` | Fresh native executor invocation identity |
| `projectRoot` | Trusted absolute local project directory |
| `outputRelativeRoot` | Exactly `tts-corpus/actions/{actionId}/executions/{executionId}/windows/{windowId}` |
| `sourceRelativePath` | Exact accepted project-relative source WAV, outside `tts-corpus/` |
| `sourceRecordingId`, `sourceSha256` | Accepted source identity and exact WAV file SHA |
| `sourceSampleCount` | Original mono48k sample count, positive integer |
| `runtimeFingerprint` | Parent's pinned stable runtime SHA |
| `preparedPlan` | Null for preparation, otherwise exact typed prepared-plan object below |
| `nextClipIndex` | Zero for preparation; next unaccepted zero-based plan index for ASR |
| `requestedClipIds` | Empty for preparation, otherwise exact next `min(8, remaining)` plan IDs |
| `dependencySites` | One or two trusted absolute directories in explicit precedence order |
| `modelRoot` | Trusted absolute local model directory |
| `modelFiles` | Exact public lock inventory: `{filename, bytes, sha256}` entries |

All payload paths use forward slashes relative to projectRoot. Absolute paths,
backslashes, empty/dot/traversal components, streams, trailing dots/spaces,
Windows device aliases and reparse points in existing ancestors are rejected.
Application ownership pins are still required to prevent replacement races.

On Windows, filesystem IO uses the extended local-drive spelling (`\\?\C:\...`)
inside the worker, allowing nested immutable clip/text paths beyond MAX_PATH
without changing Windows settings. JSON root/relative paths, filenames, identities
and hashes keep their original logical representation. CLI request/gate paths
are converted to that same filesystem spelling before exact window comparisons.
This conversion rejects relative/drive-relative paths, traversal and UNC/device
namespaces; it neither resolves reparse links nor relaxes existing path guards.
The helper lives in protocol.py, retaining the four-file worker inventory.

`preparedPlan` mirrors `TtsCorpusPreparedPlan`:

```json
{"planRelativePath":"tts-corpus/actions/A/executions/E/windows/W/plan.json","sha256":"<plan-file-sha>","sourceSha256":"<source-sha>","runtimeFingerprint":"<runtime-sha>","totalSamples":16000,"clips":[{"id":"69314b00a4ac5cfaaae2b22660942123","index":0,"startSample":0,"endSample":16000,"audioRelativePath":"tts-corpus/actions/A/executions/E/windows/W/clips/69314b00a4ac5cfaaae2b22660942123.wav","audioSha256":"<wav-file-sha>"}]}
```

Examples use `A`, `E`, `W` and other capital letters as schematic identity
placeholders; real requests use32 lowercase hex characters. Clip IDs are
`uuid.uuid5(uuid.UUID(hex=actionId), f"clip:{index}").hex`, stable across windows
and resumes of the same action. A different action yields different clip IDs.
For actionId `44444444444444444444444444444444`, index0 gives
`69314b00a4ac5cfaaae2b22660942123`. Human-readable ordering uses the separate
zero-based `index`, never an invented textual clip ID. Every clip index matches
its array index. Offsets are half-open samples of the clean mono16k source, contiguous
from0 through totalSamples, each positive and <=256000 samples. totalSamples is
exactly `(sourceSampleCount+2)//3`, including non-divisible original counts.
There are at most512 clips. `planRelativePath` names one exact same-action
preparation window; all its clip paths belong to that same window.

## Preparation

Preparation reads only PCM16 mono48k uncompressed WAV matching the supplied hash
and exact sample count. After START it uses local NumPy and
`scipy.signal.resample_poly(signal,1,3)` to produce mono16k PCM16. Polyphase
antialiasing is part of resampling; there is no extra denoise, high-pass,
normalization, gain adjustment or source rewrite. Values are rounded and clipped
to the PCM16 range once. The clean saved PCM drives RMS planning.

Planning uses10ms frames, RMS threshold0.008 and pauses >=100ms. A pause midpoint
nearest14 seconds is preferred when it falls8..15.5 seconds after the previous
boundary. Without a suitable pause the boundary targets14 seconds. Clips have a
16-second hard cap and preserve all samples, including silence. A final clip of
at least4 seconds is retained whenever the complete source is at least4 seconds;
the preceding fallback boundary moves earlier when necessary. A source shorter
than16 seconds can remain one clip. Sources requiring more than512 clips fail
without publication. Clips are exact byte slices from the saved PCM16 clean WAV.

Files: `clean.wav`, `clips/{clipId}.wav`, `plan.json`. On-disk plan fields are
`schemaVersion`, `actionId`, `sourceSha256`, `runtimeFingerprint`, `totalSamples`,
`clips`, `cleanAudioRelativePath`, `cleanAudioSha256`. The public prepared-plan
object adds `planRelativePath` and its file `sha256`, while omitting the clean-audio
fields/schema/action (already bound independently). Hashes cover complete WAV
files, not PCM bytes. All plan/clean/clip files are validated before READY.

## ASR, STOP and transcript payload

The local pinned Whisper large-v3-turbo model loads with `local_files_only=True`,
`trust_remote_code=False`, safetensors, FP32, eager attention, no Accelerate or
device_map, and CPU. Torch intra/inter threads are2/1. Pipeline arguments:
batch1, num_workers0, chunk30/stride(5,5), word timestamps, Russian/transcribe,
num_beams1, do_sampleFalse, condition_on_prev_tokensFalse. It receives an explicit
mono16k float32 waveform, never a path/URL handed to an implicit decoder.

STOP is the existence of the exact current execution-root owned `STOP` file (one
level above `windows`, shared by all windows in the execution). A window-local
STOP file is ignored. The execution STOP is
checked before loading optional dependencies/model, between clips and after the
last clip. Inference is not interrupted within a clip; parent deadline/termination
bounds it. Preparation is one indivisible publication: STOP during preparation
can return the complete preparation with `stopped:true`. STOP before preparation
can return no plan and no clips. Successful STOP may return an empty ASR prefix.
Any raised error produces nonzero exit and no READY; partial audio/text files
from that failed invocation are never accepted.

Each finished clip gets `texts/{clipId}.json`:

```json
{"schemaVersion":1,"clipId":"69314b00a4ac5cfaaae2b22660942123","draftText":"Точный черновой текст.","warningCodes":[],"words":[{"text":"Точный","start":0.2,"end":0.9}]}
```

Text is the actual model output with whitespace normalized, maximum4000 Unicode
code points. Words are actual model chunks, at most2048, with finite approximate
seconds relative to the clip. Times are clipped to0..duration; ordering/reversed
or malformed times fail validation. The last word's missing end remains `null`
with `tts_corpus.asr.final_word_end_missing`. A missing nonfinal end fails rather
than inventing a time. Empty text, or a clip >=8 seconds with fewer than2
alphanumeric transcript characters per second, gets
`tts_corpus.asr.sparse_transcript`. This heuristic only requests human review.
No reviewed/excluded/trained/quality property is produced.

## Result and independent acceptance

Result size is bounded to8 MiB. All fields are required:

```json
{"schemaVersion":1,"requestId":"R","projectId":"P","corpusId":"C","actionId":"A","executionId":"E","windowId":"W","sourceRecordingId":"S","sourceSha256":"<sha>","runtimeFingerprint":"<sha>","requestSha256":"<exact-request-sha>","planSha256":null,"preparedPlan":null,"clips":[],"stopped":true}
```

Preparation returns its preparedPlan and no accepted clips. An early preparation
STOP has preparedPlan/planSha256 null. ASR always returns preparedPlan null and
planSha256 equal to the requested preparedPlan SHA. Its `clips` contains typed
`TtsCorpusAcceptedClip` objects:

```json
{"plan":{"id":"69314b00a4ac5cfaaae2b22660942123","index":0,"startSample":0,"endSample":16000,"audioRelativePath":"<exact-planned-path>","audioSha256":"<sha>"},"textRelativePath":"tts-corpus/actions/A/executions/E/windows/W/texts/69314b00a4ac5cfaaae2b22660942123.json","textSha256":"<sha>","draftText":"Слово","warningCodes":[],"words":[{"text":"Слово","start":0.0,"end":1.0}]}
```

Parent acceptance requires exit0, confirmed Job-zero, bounded safe streams,
READY/result hash, all request identities/source/runtime/plan bindings, all
referenced file hashes and PCM shapes/sample limits, exact plan equality for
accepted clips, and transcript payload equality. Each disk-plan and nested result
copy is independently schema-validated before comparison; equality of duplicated
values uses canonical JSON bytes so Python numeric aliases cannot bypass types
or silently change a duplicated number's representation. IDs must be the exact requested
prefix: without STOP it is the complete requested window; with STOP it may be
any prefix including empty. Ordering, skipping, duplication, extra clips or
cross-window text output paths fail. Preparation cannot be combined with ASR
in one result. `cleanupConfirmed` is a parent-owned fact and is deliberately
absent from worker JSON. Failed/terminated/cleanup-unknown windows are not
accepted even if result/READY happen to exist. Already accepted earlier windows
remain immutable and available for resume.

## Offline tests

Run base Python3.11 with `-I -S -m unittest discover -s tests/TtsCorpusWorker.Tests -v`.
Tests explicitly add only the own source directory and assert that NumPy,
SciPy, Torch and Transformers were not imported. They exercise real WAV/JSON
files, byte-exact clip slices, gate subprocesses, native fd suppression,
STOP/failure behavior and tampering rejection. They do not demonstrate native
Job enforcement, dependency/runtime admission, model inference or audio quality;
the root-owned product acceptance supplies that evidence separately.
