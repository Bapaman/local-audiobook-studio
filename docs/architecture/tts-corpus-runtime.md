# Explicit local TTS corpus runtime

The desktop App accepts one trusted local startup suffix:
`--tts-corpus-runtime-config C:/absolute/path/runtime.json`. It composes with the
existing training/worker suffixes in either order. The launch helper accepts
`-TtsCorpusRuntimeConfig`. Browser requests cannot supply these paths.

The descriptor is a bounded32KiB JSON object with exactly these fields:

```json
{
  "pythonPath": "C:/local/venv/Scripts/python.exe",
  "pythonBaseRoot": "C:/local/Python311",
  "dependencySites": ["C:/local/venv/Lib/site-packages", "C:/local/fallback/Lib/site-packages"],
  "workerRoot": "C:/local/app/TtsCorpusWorker",
  "modelRoot": "C:/local/whisper-large-v3-turbo",
  "modelManifestPath": "C:/local/app/locks/whisper-large-v3-turbo.lock.json",
  "runtimeInventoryPath": "C:/local/runtime-inventory.json"
}
```

Use one or two distinct dependency sites in explicit import precedence order.
All paths must be absolute local drive paths, with safe names and no reparse
ancestors. The descriptor parses without creating directories or starting a
process. No descriptor means unavailable readiness.

The runtime inventory is trusted local configuration, up to64MiB and100000files:

```json
{"schemaVersion":1,"files":[{"path":"C:/local/venv/Scripts/python.exe","bytes":103192,"sha256":"<64 lowercase hex>"}]}
```

The example entry is schematic; the inventory must contain the exact full union
of every file under `pythonBaseRoot`, each `dependencySites` root and `workerRoot`,
plus every file directly beside the configured interpreter. A venv interpreter
under `Scripts` additionally requires its parent `pyvenv.cfg`, whose `home` must
match `pythonBaseRoot`. A non-venv interpreter must belong to `pythonBaseRoot`.
Include all existing `.pyc`, `.pyd`, DLL, package metadata and ordinary assets;
there is no extension-based omission. Missing, duplicate or extra inventory
entries fail closed. Overlapping configured trees are treated as a union.

The worker root must contain exactly `worker.py`, `protocol.py`, `prepare.py` and
`asr.py`, with the product's embedded source SHA256 values. The model manifest
is checked independently of those script pins. A focused managed test verifies every shipped
script's raw SHA256 against the same admission map, catching pin transcription
and checkout/payload byte differences.

The model manifest
must be byte-identical to the shipped public lock for revision
`41f01f3fe87f28c78e2fbf8b568835947dd65ed9`. Its eleven files are independently
size/hash verified. Descriptor, inventory, model manifest, all admitted files and
their directory ancestors are pinned against replacement; file handles also
deny writes and reject hardlinks/reparse points. Execution retains these pins
through native cleanup and independent receipt validation. Readiness only
inspects files and never starts Python, imports ML, downloads, discovers caches
or modifies the existing environment.

Successful readiness DTOs are advisory and cached for at most300seconds, with
concurrent checks coalesced; pins are disposed before caching. Negative results
are never cached. Fingerprint requests and every execution always perform fresh,
serialized admission and retain their own pins, regardless of cached readiness.
Idle coordinator polling does not ask for readiness; the runner admits the
configured runtime only after finding queued work and acquiring shared compute
ownership. The default unavailable runtime keeps its immediate coordinator exit.

The deterministic runtime fingerprint includes configured paths, ordered sites,
all inventory sizes/hashes, the public model lock identity, the worker hashes and
the native/interpreter policy. Resume requires the same fingerprint. The App
publishes the four source files and public lock; Python, dependencies and model
weights remain external and must already exist. It does not install them.

Each launch uses `-I -S -B -u`, offline flags, CPU thread bounds and a
creation-time kill-on-close Job. The optional public launcher memory policy
installs the aggregate13958643712byte cap before CreateProcess; no policy keeps
existing launcher defaults. Readback labels configured cap, flags, affinity and
cumulative peak Job memory explicitly. Peak is not current usage.

Before native creation, available physical RAM must cover cap plus3221225472byte
reserve. During the window, a100ms parent watchdog checks reserve, limits and
the300second deadline. The kernel aggregate cap is the hard bound. The parent
releases exact ASCII `START\n` only after owned limit readback. Every exit path,
including root exit0, requires exact owned Job-zero. Unknown cleanup throws
`CleanupConfirmed=false`, retains the child/runtime/source pins and prevents
another dispatch. Shutdown may retry only that exact retained cleanup owner.
The same typed unknown launch failure also poisons the legacy synthesis engine:
it retains prompt/artifact pins and its staged file, closes the worker pipe
without a reusable terminal/ready state, and retries only that exact owner at
host shutdown. The outer host Job remains the final cleanup boundary.

Managed fake/pure tests do not demonstrate native kernel enforcement, real
dependency compatibility or Whisper inference. Those require separate serialized
product acceptance on the configured machine.
