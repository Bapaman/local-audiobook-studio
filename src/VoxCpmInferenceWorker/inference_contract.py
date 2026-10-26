"""Dependency-free identity and completeness rules for direct adapter synthesis."""
from dataclasses import dataclass
import hashlib
import json
import math
import re

PROFILE = 'voxcpm2-saved-adapter-cpu-v1'
REFERENCE_PROFILE = 'voxcpm2-saved-adapter-reference-cpu-v1'
CONTROLS_PROFILE = 'voxcpm2-saved-adapter-controls-cpu-v1'
MAX_KV_BYTES = 64 * 1024**2
MAX_RAW_SAMPLES = 7680 * 256
MAX_FINAL_SAMPLES = 2457600


class InferenceError(ValueError):
    def __init__(self):
        # No user text, paths, tensors or private identities in exception text.
        super().__init__('voxcpm_inference.invalid')


def require(condition):
    if not condition:
        raise InferenceError()


def integer(value, minimum, maximum):
    require(type(value) is int and minimum <= value <= maximum)
    return value


def digest(value, length=64):
    require(type(value) is str and re.fullmatch('[0-9a-f]{' + str(length) + '}', value) is not None)
    return value


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'),
                      ensure_ascii=False, allow_nan=False).encode('utf-8')


def sha256(payload):
    return hashlib.sha256(payload).hexdigest()


@dataclass(frozen=True)
class CheckpointBinding:
    project_id: str
    checkpoint_id: str
    global_step: int
    manifest_sha256: str
    adapter_sha256: str
    runtime_fingerprint: str
    recipe_sha256: str
    layout_sha256: str

    def validate(self):
        digest(self.project_id, 32)
        digest(self.checkpoint_id, 32)
        integer(self.global_step, 1, 512)
        for value in (self.manifest_sha256, self.adapter_sha256, self.runtime_fingerprint,
                      self.recipe_sha256, self.layout_sha256):
            digest(value)

    def as_dict(self):
        self.validate()
        return dict(projectId=self.project_id, checkpointId=self.checkpoint_id,
                    globalStep=self.global_step, manifestSha256=self.manifest_sha256,
                    adapterSha256=self.adapter_sha256, runtimeFingerprint=self.runtime_fingerprint,
                    recipeSha256=self.recipe_sha256, adapterLayoutSha256=self.layout_sha256)


@dataclass(frozen=True)
class ReferenceBinding:
    corpus_version_id: str
    corpus_manifest_sha256: str
    corpus_binding_sha256: str
    clip_id: str
    training_ordinal: int
    relative_path: str
    bytes: int
    sha256: str
    sample_rate: int
    samples: int

    def validate(self):
        digest(self.corpus_version_id, 32)
        digest(self.clip_id, 32)
        for value in (self.corpus_manifest_sha256, self.corpus_binding_sha256, self.sha256):
            digest(value)
        integer(self.training_ordinal, 0, 511)
        integer(self.sample_rate, 16000, 16000)
        integer(self.samples, 80000, 256000)
        require(integer(self.bytes, 160044, 512044) == 44 + 2 * self.samples)
        path = self.relative_path
        require(type(path) is str and 0 < len(path) <= 2048
                and not any(c in path for c in '\\:<>"|?*'))
        parts = path.split('/')
        reserved = {'CON', 'PRN', 'AUX', 'NUL', *("COM" + str(i) for i in range(1, 10)),
                    *("LPT" + str(i) for i in range(1, 10))}
        require(len(parts) >= 4 and parts[:2] == ['tts-corpus', 'actions'])
        digest(parts[2], 32)
        require(all(p not in ('', '.', '..') and p[-1] not in ('.', ' ')
                    and p.split('.')[0].upper() not in reserved and not any(ord(c) < 32 for c in p)
                    for p in parts))

    def as_dict(self):
        self.validate()
        return dict(corpusVersionId=self.corpus_version_id, corpusManifestSha256=self.corpus_manifest_sha256,
                    corpusBindingSha256=self.corpus_binding_sha256, clipId=self.clip_id,
                    trainingOrdinal=self.training_ordinal, relativePath=self.relative_path,
                    bytes=self.bytes, sha256=self.sha256, sampleRate=self.sample_rate, samples=self.samples)

    @property
    def patches(self):
        self.validate()
        return (self.samples + 2559) // 2560

    @property
    def prefix_slots(self):
        return self.patches + 2


@dataclass(frozen=True)
class ReferenceAudio:
    binding: ReferenceBinding
    path: str

    def validate(self):
        require(type(self.binding) is ReferenceBinding)
        self.binding.validate()
        require(type(self.path) is str and 0 < len(self.path) <= 32700
                and not any(ord(c) < 32 for c in self.path))
        logical = self.path[4:] if self.path.startswith('\\\\?\\') else self.path
        require(logical.startswith('/') or re.match(r'^[A-Za-z]:[\\/]', logical) is not None)


def validate_controls(value, reference=None):
    require(type(value) is dict and set(value) == {
        'seed', 'timesteps', 'cfg', 'tempoPercent', 'useReference', 'referenceClipId'})
    integer(value['seed'], 0, 2147483647)
    integer(value['timesteps'], 4, 20)
    cfg = value['cfg']
    require(type(cfg) in (int, float) and math.isfinite(cfg) and 1 <= cfg <= 3)
    integer(value['tempoPercent'], 80, 120)
    require(type(value['useReference']) is bool and value['useReference'] == (reference is not None))
    clip = value['referenceClipId']
    if reference is None:
        require(clip is None)
    else:
        require(type(reference) is ReferenceBinding)
        reference.validate()
        require(clip is None or digest(clip, 32) == reference.clip_id)
    return value


def tempo_sample_count(samples, percent):
    """Exact rational rounding, ties to even, shared with the host contract."""
    integer(samples, 1, MAX_RAW_SAMPLES)
    integer(percent, 80, 120)
    whole, remainder = divmod(samples * 100, percent)
    result = whole + int(2 * remainder > percent or (2 * remainder == percent and whole % 2 == 1))
    return integer(result, 1, MAX_FINAL_SAMPLES)


@dataclass(frozen=True)
class SynthesisPlan:
    text: str
    tokens: tuple
    text_sha256: str
    tokens_sha256: str
    patch_limit: int
    cache_capacity: int


def validate_text(text):
    require(type(text) is str and 0 < len(text) <= 512 and bool(text.strip()))
    require(all(c in '\t\r\n' or not (ord(c) < 32 or 127 <= ord(c) <= 159) for c in text))
    try:
        payload = text.encode('utf-8')
    except UnicodeError as error:
        raise InferenceError() from error
    require(len(payload) <= 2048)
    return payload


def plan_synthesis(text, tokens, position_limit, reference=None):
    payload = validate_text(text)
    require(type(tokens) is list and 1 <= len(tokens) <= 256)
    for token in tokens:
        integer(token, 0, 2**31 - 1)
    limit = min(256, len(tokens) * 6 + 10)
    # The public loop also executes forward_step after a non-EOS last patch.
    capacity = len(tokens) + 1 + limit
    if reference is not None:
        require(type(reference) is ReferenceBinding)
        capacity += reference.prefix_slots
    require(capacity <= integer(position_limit, 1, 65536))
    return SynthesisPlan(text, tuple(tokens), sha256(payload), sha256(canonical(tokens)), limit, capacity)


def kv_cache_bytes(config, capacity):
    capacity = integer(capacity, 1, 65536)
    layers = integer(getattr(config, 'num_hidden_layers', None), 1, 1024)
    heads = integer(getattr(config, 'num_key_value_heads', None), 1, 1024)
    width = integer(getattr(config, 'hidden_size', None), 1, 65536)
    attention = integer(getattr(config, 'num_attention_heads', None), 1, 1024)
    channels = getattr(config, 'kv_channels', None)
    if channels is None:
        require(width % attention == 0)
        channels = width // attention
    channels = integer(channels, 1, 65536)
    return 2 * layers * heads * capacity * channels * 4


class StopEvidence:
    def __init__(self, limit):
        self.limit = integer(limit, 4, 256)
        self.count = 0
        self.ended = False

    def observe(self, flag):
        integer(flag, 0, 1)
        require(not self.ended and self.count < self.limit)
        self.count += 1
        # Pinned upstream uses zero-based i > min_len (2), not i >= 2.
        self.ended = self.count >= 4 and flag == 1

    def finish(self):
        require(self.ended)
        return self.count
