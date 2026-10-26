"""Product-owned isolated/gated inference entrypoint; no optional imports before START."""
import argparse
from array import array
import contextlib
import os
from pathlib import Path
import struct
import sys
import time

if __name__ == '__main__':
    sys.path.insert(0, str(Path(__file__).absolute().parent))
from inference_contract import (CONTROLS_PROFILE, MAX_FINAL_SAMPLES, MAX_RAW_SAMPLES, CheckpointBinding,
                                PROFILE, REFERENCE_PROFILE, ReferenceAudio, ReferenceBinding,
                                tempo_sample_count, validate_controls, validate_text)

EVIDENCE_KEYS = ('profileId', 'seed', 'binding', 'textSha256', 'tokensSha256', 'textCharacters',
                 'tokenCount', 'cacheCapacity', 'cacheBytes', 'effectivePatchLimit', 'generatedPatches',
                 'naturalEos', 'sampleRate', 'samples', 'peak')
REFERENCE_KEYS = ('corpusVersionId', 'corpusManifestSha256', 'corpusBindingSha256', 'clipId',
                  'trainingOrdinal', 'relativePath', 'bytes', 'sha256', 'sampleRate', 'samples')


def binding_object(value):
    import protocol as p
    p.keys(value, ('projectId', 'checkpointId', 'globalStep', 'manifestSha256', 'adapterSha256',
                   'runtimeFingerprint', 'recipeSha256', 'adapterLayoutSha256'))
    result = CheckpointBinding(value['projectId'], value['checkpointId'], value['globalStep'],
                               value['manifestSha256'], value['adapterSha256'], value['runtimeFingerprint'],
                               value['recipeSha256'], value['adapterLayoutSha256'])
    result.validate()
    return result


def reference_object(value):
    import protocol as p
    p.keys(value, REFERENCE_KEYS)
    result = ReferenceBinding(value['corpusVersionId'], value['corpusManifestSha256'],
                              value['corpusBindingSha256'], value['clipId'], value['trainingOrdinal'],
                              value['relativePath'], value['bytes'], value['sha256'],
                              value['sampleRate'], value['samples'])
    result.validate()
    return result


@contextlib.contextmanager
def reference_audio(request):
    """After START, hold immutable canonical reference bytes through library reopen."""
    import protocol as p
    from owned_files import io_path, open_file
    if request['format'] == 1 or (request['format'] == 3 and request['reference'] is None):
        yield None
        return
    p.require((request['format'] == 2 and request['profileId'] == REFERENCE_PROFILE)
              or (request['format'] == 3 and request['profileId'] == CONTROLS_PROFILE))
    binding = reference_object(request['reference'])
    path = p.safe_path(request['projectRoot'], binding.relative_path)
    with open_file(path) as stream:
        p.require(os.fstat(stream.fileno()).st_size == binding.bytes)
        payload = stream.read(binding.bytes + 1)
        p.require(len(payload) == binding.bytes and p.sha256(payload) == binding.sha256)
        expected = (b'RIFF' + struct.pack('<I', 36 + 2 * binding.samples) + b'WAVEfmt '
                    + struct.pack('<IHHIIHH', 16, 1, 1, 16000, 32000, 2, 16)
                    + b'data' + struct.pack('<I', 2 * binding.samples))
        p.require(payload[:44] == expected)
        del payload
        audio = ReferenceAudio(binding, str(io_path(path)))
        audio.validate()
        yield audio


def validate_request(value):
    import protocol as p
    from mapped_backend import _layout
    p.require(type(value) is dict and type(value.get('format')) is int and value['format'] in (1, 2, 3))
    fields = ('format', 'projectId', 'jobId', 'segmentId', 'attemptId', 'projectRoot',
              'outputRelativeRoot', 'text', 'textSha256', 'binding', 'runtime', 'adapter')
    extra = ('profileId', 'reference', 'controls') if value['format'] == 3 else (('profileId', 'reference') if value['format'] == 2 else ())
    p.keys(value, fields + extra)
    p.require(len(p.canonical_bytes(value)) <= 262144)
    if value['format'] == 2:
        p.require(value['profileId'] == REFERENCE_PROFILE)
        reference = reference_object(value['reference'])
        p.safe_path(value['projectRoot'], reference.relative_path)
    elif value['format'] == 3:
        p.require(value['profileId'] == CONTROLS_PROFILE)
        reference = reference_object(value['reference']) if value['reference'] is not None else None
        validate_controls(value['controls'], reference)
        if reference is not None:
            p.safe_path(value['projectRoot'], reference.relative_path)
    for key in ('projectId', 'jobId', 'segmentId', 'attemptId'):
        p.identity(value[key])
    p.require(value['outputRelativeRoot'] == 'segments/' + value['jobId'] + '/' + value['attemptId'] + '-vox')
    p.safe_path(value['projectRoot'], value['outputRelativeRoot'])
    p.require(p.sha256(validate_text(value['text'])) == p.digest(value['textSha256']))
    binding = binding_object(value['binding'])
    p.require(binding.project_id == value['projectId'])
    r = value['runtime']
    p.keys(r, ('dependencySites', 'sourceRoot', 'sourceFiles', 'modelRoot', 'modelFiles',
               'runtimeInventoryPath', 'runtimeInventorySha256', 'cache', 'identity', 'parameterLayout'))
    p.validate_identity(r['identity'])
    p.require(r['identity']['fingerprint'] == binding.runtime_fingerprint
              and r['identity']['recipeSha256'] == binding.recipe_sha256
              and r['identity']['adapterLayoutSha256'] == binding.layout_sha256)
    p.validate_inventory(r['sourceRoot'], r['sourceFiles'], 38)
    p.validate_inventory(r['modelRoot'], r['modelFiles'], 7)
    p.require(type(r['dependencySites']) is list and 1 <= len(r['dependencySites']) <= 2
              and all(type(s) is str for s in r['dependencySites'])
              and len({s.casefold() for s in r['dependencySites']}) == len(r['dependencySites']))
    for site in r['dependencySites']:
        p.io_path(site)
    p.io_path(r['runtimeInventoryPath'])
    p.digest(r['runtimeInventorySha256'])
    p.keys(r['cache'], ('path', 'bytes', 'sha256', 'layoutSha256'))
    p.io_path(r['cache']['path'])
    p.require(type(r['cache']['bytes']) is int and r['cache']['bytes'] == 9536873992)
    p.require(r['cache']['sha256'] == r['identity']['cacheSha256']
              and r['cache']['layoutSha256'] == r['identity']['cacheLayoutSha256'])
    p.validate_layout(r['parameterLayout'], binding.layout_sha256)
    _layout(r['parameterLayout'])
    a = value['adapter']
    p.keys(a, ('relativePath', 'bytes', 'sha256'))
    p.integer(a['bytes'], 9, 8 * 1024**2)
    p.require(p.digest(a['sha256']) == binding.adapter_sha256)
    parts = p.relative_parts(a['relativePath'])
    p.require(len(parts) == 10 and parts[:2] == ['voxcpm-training', 'actions']
              and parts[3] == 'executions' and parts[5] == 'windows' and parts[7] == 'checkpoints'
              and parts[8] == binding.checkpoint_id and parts[9] == 'lora_weights.safetensors')
    for i in (2, 4, 6):
        p.identity(parts[i])
    p.safe_path(value['projectRoot'], a['relativePath'])
    return value


def validate_evidence(value, evidence, pcm, final_pcm=None):
    import protocol as p
    configured = value['format'] == 3
    reference = reference_object(value['reference']) if value['format'] in (2, 3) and value['reference'] is not None else None
    extra = ('controls', 'tempo') if configured else ()
    p.keys(evidence, EVIDENCE_KEYS + extra + (('reference', 'referencePatches', 'referencePrefixSlots') if reference else ()))
    profile = CONTROLS_PROFILE if configured else (REFERENCE_PROFILE if reference else PROFILE)
    seed = 42
    if configured:
        controls = validate_controls(value['controls'], reference)
        seed = controls['seed']
        p.require(p.canonical_bytes(evidence['controls']) == p.canonical_bytes(controls))
    p.require(evidence['profileId'] == profile and type(evidence['seed']) is int and evidence['seed'] == seed)
    if reference:
        p.require(value['profileId'] == profile
                  and p.canonical_bytes(evidence['reference']) == p.canonical_bytes(reference.as_dict()))
        p.require(p.integer(evidence['referencePatches'], 32, 100) == reference.patches
                  and p.integer(evidence['referencePrefixSlots'], 34, 102) == reference.prefix_slots)
    p.require(p.canonical_bytes(evidence['binding']) == p.canonical_bytes(value['binding']))
    p.require(evidence['textSha256'] == value['textSha256'])
    p.digest(evidence['tokensSha256'])
    p.require(p.integer(evidence['textCharacters'], 1, 512) == len(value['text']))
    tokens = p.integer(evidence['tokenCount'], 1, 256)
    limit = min(256, tokens * 6 + 10)
    capacity = tokens + 1 + limit + (reference.prefix_slots if reference else 0)
    p.require(p.integer(evidence['effectivePatchLimit'], 4, 256) == limit
              and p.integer(evidence['cacheCapacity'], 1, 615 if reference else 513) == capacity
              and p.integer(evidence['cacheBytes'], 1, 64 * 1024**2) == capacity * 73728)
    patches = p.integer(evidence['generatedPatches'], 4, limit)
    p.require(evidence['naturalEos'] is True and type(evidence['sampleRate']) is int and evidence['sampleRate'] == 48000)
    samples = p.integer(evidence['samples'], 1, 7680 * patches)
    peak = p.finite(evidence['peak'], 0, 1)
    p.require(peak > 0)
    encoded_peak = pcm_peak(pcm, samples)
    p.require(abs(encoded_peak - peak) <= 0.0001)
    if configured:
        tempo = evidence['tempo']
        p.keys(tempo, ('algorithm', 'percent', 'rawSamples', 'samples', 'peak'))
        percent = controls['tempoPercent']
        p.require(p.integer(tempo['percent'], 80, 120) == percent
                  and p.integer(tempo['rawSamples'], 1, MAX_RAW_SAMPLES) == samples)
        expected = tempo_sample_count(samples, percent)
        p.require(p.integer(tempo['samples'], 1, MAX_FINAL_SAMPLES) == expected)
        algorithm = 'identity-v1' if percent == 100 else 'librosa-phase-vocoder-v1'
        p.require(tempo['algorithm'] == algorithm)
        final_peak = pcm_peak(final_pcm, expected)
        p.require(p.finite(tempo['peak'], 0, 1) == final_peak)
        if percent == 100:
            p.require(final_pcm == pcm)
    else:
        p.require(final_pcm is None)


def pcm_peak(pcm, sample_count):
    """Inspect encoded PCM: no silence, rail values, odd or mismatched lengths."""
    import protocol as p
    p.require(type(pcm) is bytes and type(sample_count) is int and sample_count > 0
              and len(pcm) == sample_count * 2)
    peak = 0
    for (number,) in struct.iter_unpack('<h', pcm):
        p.require(-32768 < number < 32767)
        peak = max(peak, abs(number))
    p.require(peak > 0)
    return peak / 32768.0


@contextlib.contextmanager
def quiet_libraries():
    sys.stdout.flush()
    sys.stderr.flush()
    with open(os.devnull, 'w', encoding='utf-8') as sink:
        stdout_fd, stderr_fd = os.dup(1), os.dup(2)
        try:
            os.dup2(sink.fileno(), 1)
            os.dup2(sink.fileno(), 2)
            with contextlib.redirect_stdout(sink), contextlib.redirect_stderr(sink):
                yield
        finally:
            sink.flush()
            os.dup2(stdout_fd, 1)
            os.dup2(stderr_fd, 2)
            os.close(stdout_fd)
            os.close(stderr_fd)


def encode_pcm(values, sample_count, maximum=MAX_RAW_SAMPLES):
    import protocol as p
    p.require(sys.byteorder == 'little' and type(values) is list
              and maximum in (MAX_RAW_SAMPLES, MAX_FINAL_SAMPLES)
              and len(values) == p.integer(sample_count, 1, maximum))
    return array('h', (round(p.finite(v, -1, 1) * 32767) for v in values)).tobytes()


def render_tempo(values, raw_pcm, percent):
    """Declared export processing only; imports run after the owned START gate."""
    import protocol as p
    samples = p.integer(len(values), 1, MAX_RAW_SAMPLES)
    expected = tempo_sample_count(samples, percent)
    pcm_peak(raw_pcm, samples)
    if percent == 100:
        pcm, algorithm = raw_pcm, 'identity-v1'
    else:
        import numpy as np
        from librosa.effects import time_stretch
        source = np.asarray(values, dtype=np.float32)
        output = time_stretch(source, rate=percent / 100.0, n_fft=2048, hop_length=512,
                              win_length=2048, window='hann', center=True)
        p.require(isinstance(output, np.ndarray) and output.dtype == np.float32
                  and tuple(output.shape) == (expected,))
        # Encoding validates finite values and rejects overshoot; PCM inspection
        # then rejects silent/rail output. No normalization, clipping or fallback.
        pcm = encode_pcm(output.tolist(), expected, MAX_FINAL_SAMPLES)
        algorithm = 'librosa-phase-vocoder-v1'
    peak = pcm_peak(pcm, expected)
    return pcm, dict(algorithm=algorithm, percent=percent, rawSamples=samples, samples=expected, peak=peak)


def _produce(request, phase, reference=None):
    import protocol as p
    from mapped_backend import load_mapped_backend
    p.require((request['format'] == 1 and reference is None)
              or (request['format'] == 2 and type(reference) is ReferenceAudio
                  and reference.binding.as_dict() == request['reference'])
              or (request['format'] == 3
                  and ((reference is None and request['reference'] is None)
                       or (type(reference) is ReferenceAudio and reference.binding.as_dict() == request['reference']))))
    payload = p.read_bytes(p.safe_path(request['projectRoot'], request['adapter']['relativePath']), 8 * 1024**2)
    p.require(len(payload) == request['adapter']['bytes'])
    with load_mapped_backend(request['runtime'], binding_object(request['binding']), payload) as backend:
        phase('generating')
        if request['format'] == 3:
            audio, evidence = backend.synthesize(request['text'], reference, request['controls'])
        elif reference is None:
            audio, evidence = backend.synthesize(request['text'])
        else:
            audio, evidence = backend.synthesize(request['text'], reference)
    values = audio[0].tolist()
    pcm = encode_pcm(values, evidence['samples'])
    if request['format'] == 3:
        final_pcm, tempo = render_tempo(values, pcm, request['controls']['tempoPercent'])
        evidence['tempo'] = tempo
        return dict(pcm=final_pcm, sourcePcm=pcm, evidence=evidence)
    # Legacy profiles perform required file encoding only, with no DSP.
    return dict(pcm=pcm, evidence=evidence)


def _publish(request, request_sha, result, output):
    import protocol as p
    from owned_files import pin_directories
    configured = request['format'] == 3
    p.keys(result, ('pcm', 'sourcePcm', 'evidence') if configured else ('pcm', 'evidence'))
    if configured:
        validate_evidence(request, result['evidence'], result['sourcePcm'], result['pcm'])
    else:
        validate_evidence(request, result['evidence'], result['pcm'])
    pcm = result['pcm']
    wave = (b'RIFF' + struct.pack('<I', 36 + len(pcm)) + b'WAVEfmt '
            + struct.pack('<IHHIIHH', 16, 1, 1, 48000, 96000, 2, 16)
            + b'data' + struct.pack('<I', len(pcm)) + pcm)
    root = output / 'candidate'
    root.mkdir(exist_ok=False)
    with pin_directories(root):
        wave_sha = p.write_bytes(root / 'final.wav', wave)
        manifest = {key: request[key] for key in ('projectId', 'jobId', 'segmentId', 'attemptId')}
        samples = result['evidence']['tempo']['samples'] if configured else result['evidence']['samples']
        manifest.update(format=2 if configured else 1, requestSha256=p.digest(request_sha), evidence=result['evidence'],
                        audio=dict(relativePath='candidate/final.wav', bytes=len(wave), sha256=wave_sha,
                                   samples=samples))
        if configured:
            raw_pcm = result['sourcePcm']
            raw_wave = (b'RIFF' + struct.pack('<I', 36 + len(raw_pcm)) + b'WAVEfmt '
                        + struct.pack('<IHHIIHH', 16, 1, 1, 48000, 96000, 2, 16)
                        + b'data' + struct.pack('<I', len(raw_pcm)) + raw_pcm)
            raw_sha = p.write_bytes(root / 'raw.wav', raw_wave)
            manifest['sourceAudio'] = dict(relativePath='candidate/raw.wav', bytes=len(raw_wave),
                                          sha256=raw_sha, samples=result['evidence']['samples'])
        manifest_sha = p.write_json(root / 'manifest.json', manifest, 16384)
        p.write_bytes(root / 'READY', (manifest_sha + '\n').encode('ascii'))


def execute(request, request_sha, gate_path):
    import protocol as p
    from owned_files import pin_directories, open_file
    from mapped_backend import validate_adapter_payload
    validate_request(request)
    output = p.safe_path(request['projectRoot'], request['outputRelativeRoot'])
    p.require(p.io_path(gate_path) == output / 'START')
    with pin_directories(output), open_file(gate_path, mutable=True) as gate:
        print('voxcpm_inference.worker.gated', flush=True)
        deadline = time.monotonic() + 300
        while True:
            gate.seek(0)
            value = gate.read(7)
            if value == b'START\n':
                break
            p.require(value == b'' and time.monotonic() < deadline)
            time.sleep(0.05)
        adapter = p.read_bytes(p.safe_path(request['projectRoot'], request['adapter']['relativePath']), 8 * 1024**2)
        p.require(len(adapter) == request['adapter']['bytes'])
        validate_adapter_payload(adapter, request['runtime']['parameterLayout'], request['adapter']['sha256'])
        del adapter
        with reference_audio(request) as reference:
            print('voxcpm_inference.worker.loading', flush=True)
            progress_fd = os.dup(1)
            try:
                def phase(name):
                    p.require(name == 'generating')
                    os.write(progress_fd, b'voxcpm_inference.worker.generating\n')
                with quiet_libraries():
                    result = _produce(request, phase) if reference is None else _produce(request, phase, reference)
            finally:
                os.close(progress_fd)
            _publish(request, request_sha, result, output)
        print('voxcpm_inference.worker.ready', flush=True)


def main():
    try:
        class SafeParser(argparse.ArgumentParser):
            def error(self, message):
                raise ValueError()
        parser = SafeParser(add_help=False, exit_on_error=False)
        for name in ('training-worker-root', 'request', 'request-sha256', 'gate'):
            parser.add_argument('--' + name, required=True)
        args = parser.parse_args()
        root = Path(args.training_worker_root)
        if not root.is_absolute() or (os.name == 'nt' and (len(root.drive) != 2 or root.drive[1] != ':')):
            raise ValueError()
        sys.path.append(str(root))  # Parent has pinned this unchanged eight-file directory.
        import protocol as p
        request = validate_request(p.read_json(args.request, args.request_sha256, 262144))
        output = p.safe_path(request['projectRoot'], request['outputRelativeRoot'])
        p.require(p.io_path(args.request) == output / 'request.json')
        execute(request, args.request_sha256, args.gate)
        return 0
    except (Exception, SystemExit):
        print('voxcpm_inference.worker.failed', file=sys.stderr, flush=True)
        return 1


if __name__ == '__main__':
    sys.exit(main())
