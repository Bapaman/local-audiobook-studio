"""Captured controls and publication tests; native DSP/model boundaries are doubled."""
import contextlib
import copy
import io
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'src' / 'VoxCpmInferenceWorker'))
import inference_contract as contract
import mapped_backend
from fakes import Model, Torch
from test_contract import binding
from test_reference import reference_candidate, reference_request
from test_worker import ENGINE, TRAINING, candidate, protocol, request, worker
from synthesis import synthesize_loaded

PROFILE = 'voxcpm2-saved-adapter-controls-cpu-v1'


def controls(**changes):
    value = dict(seed=42, timesteps=10, cfg=2, tempoPercent=100,
                 useReference=False, referenceClipId=None)
    value.update(changes)
    return value


def configured_request(root, reference=False, **changes):
    value, output = reference_request(root) if reference else request(root)
    value.update(format=3, profileId=PROFILE,
                 reference=value.get('reference'), controls=controls(useReference=reference, **changes))
    return value, output


def configured_candidate(value):
    result = reference_candidate(value) if value['reference'] else candidate(value)
    result['sourcePcm'] = result['pcm']
    count = 5 if value['controls']['tempoPercent'] == 80 else 4
    if count == 5:
        result['pcm'] = struct.pack('<hhhhh', 0, 8192, -4096, 0, 0)
    result['evidence'].update(profileId=PROFILE, seed=value['controls']['seed'],
                              controls=copy.deepcopy(value['controls']),
                              tempo=dict(algorithm='identity-v1' if count == 4 else 'librosa-phase-vocoder-v1',
                                         percent=value['controls']['tempoPercent'], rawSamples=4, samples=count, peak=0.25))
    return result


class ControlsTests(unittest.TestCase):
    def test_controls_reach_actual_generate_without_changing_raw_eos_or_cache_budget(self):
        torch = Torch(); model = Model(torch)
        options = controls(seed=2147483647, timesteps=4, cfg=1, tempoPercent=80)
        audio, proof = synthesize_loaded(model, torch, 'whole text', binding(), None, options)
        self.assertIs(audio, model.waveform)
        self.assertEqual((model.arguments['seed'], model.arguments['inference_timesteps'], model.arguments['cfg_value']),
                         (2147483647, 4, 1))
        self.assertEqual((model.arguments['min_len'], model.arguments['max_len'], model.arguments['retry_badcase']), (2, 256, False))
        self.assertEqual((proof['profileId'], proof['samples'], proof['generatedPatches'], proof['cacheCapacity']),
                         (PROFILE, 4, 4, 46))
        self.assertEqual(proof['controls'], options)

    def test_format_three_accepts_auto_explicit_and_off_reference_but_rejects_bad_controls_before_model(self):
        for has_reference in (False, True):
            with self.subTest(reference=has_reference), tempfile.TemporaryDirectory() as directory:
                value, _ = configured_request(Path(directory), has_reference)
                self.assertIs(worker.validate_request(value), value)
                if has_reference:
                    value['controls']['referenceClipId'] = value['reference']['clipId']
                    self.assertIs(worker.validate_request(value), value)
                bad = [('seed', True), ('seed', -1), ('seed', 2147483648), ('seed', 1.0),
                       ('timesteps', False), ('timesteps', 3), ('timesteps', 21), ('timesteps', 10.0),
                       ('cfg', True), ('cfg', 0.99), ('cfg', 3.01), ('cfg', float('nan')), ('cfg', float('inf')),
                       ('tempoPercent', True), ('tempoPercent', 79), ('tempoPercent', 121), ('tempoPercent', 100.0),
                       ('useReference', 1), ('useReference', not has_reference),
                       ('referenceClipId', '9' * 32), ('referenceClipId', 'A' * 32)]
                for key, replacement in bad:
                    changed = copy.deepcopy(value); changed['controls'][key] = replacement
                    with self.subTest(key=key, replacement=replacement), self.assertRaises(Exception):
                        worker.validate_request(changed)
                for mode in ('missing', 'extra', 'profile', 'reference'):
                    changed = copy.deepcopy(value)
                    if mode == 'missing': changed['controls'].pop('referenceClipId')
                    elif mode == 'extra': changed['controls']['pitch'] = 1
                    elif mode == 'profile': changed['profileId'] = contract.PROFILE
                    else: changed.pop('reference')
                    with self.subTest(mode=mode), self.assertRaises(Exception): worker.validate_request(changed)
        options = controls(cfg=float('nan'))
        torch = Torch(); model = Model(torch)
        with self.assertRaises(Exception): synthesize_loaded(model, torch, 'whole text', binding(), None, options)
        self.assertEqual(model.tokenizer_inputs, [])
        self.assertEqual(model.base_lm.allocations, [])

    def test_backend_passes_controls_through_single_use_generation(self):
        torch = Torch(); model = Model(torch)
        options = controls(seed=19, timesteps=20, cfg=3)
        with mapped_backend.MappedBackend(model, torch, binding()) as backend:
            _, proof = backend.synthesize('whole text', None, options)
        self.assertEqual((proof['seed'], model.arguments['inference_timesteps'], model.arguments['cfg_value']), (19, 20, 3))
        with self.assertRaises(Exception): backend.synthesize('again', None, options)

    def test_publication_preserves_raw_waveform_and_binds_final_tempo_separately(self):
        for percent, reference in ((100, False), (80, False), (100, True)):
            with self.subTest(percent=percent, reference=reference), tempfile.TemporaryDirectory() as directory:
                value, output = configured_request(Path(directory), reference, tempoPercent=percent)
                result = configured_candidate(value)
                with patch.object(worker, '_produce', return_value=result), contextlib.redirect_stdout(io.StringIO()):
                    worker.execute(value, '8' * 64, output / 'START')
                root = output / 'candidate'
                self.assertEqual({item.name for item in root.iterdir()}, {'raw.wav', 'final.wav', 'manifest.json', 'READY'})
                manifest = protocol.read_json(root / 'manifest.json')
                self.assertEqual(manifest['format'], 2)
                self.assertEqual(manifest['evidence']['samples'], 4)
                self.assertEqual(manifest['sourceAudio'], dict(relativePath='candidate/raw.wav', bytes=52,
                                 sha256=protocol.sha256((root / 'raw.wav').read_bytes()), samples=4))
                self.assertEqual(manifest['audio']['samples'], 5 if percent == 80 else 4)
                self.assertEqual((root / 'raw.wav').read_bytes()[44:], result['sourcePcm'])
                if percent == 100: self.assertEqual((root / 'raw.wav').read_bytes(), (root / 'final.wav').read_bytes())
                self.assertEqual((root / 'READY').read_bytes(), (protocol.sha256((root / 'manifest.json').read_bytes()) + '\n').encode())

    def test_actual_producer_encodes_raw_and_calls_configured_adapter_with_captured_reference(self):
        for has_reference in (False, True):
            with self.subTest(reference=has_reference), tempfile.TemporaryDirectory() as directory:
                value, output = configured_request(Path(directory), has_reference, seed=19, timesteps=20, cfg=3)
                raw_proof = configured_candidate(value)['evidence']; raw_proof.pop('tempo')
                seen = []
                class Backend:
                    def __enter__(self): return self
                    def __exit__(self, *args): pass
                    def synthesize(self, text, reference, captured_controls):
                        seen.append((text, reference.binding.as_dict() if reference else None, captured_controls))
                        return [types.SimpleNamespace(tolist=lambda: [0.0, 0.25, -0.125, 0.0])], raw_proof
                with patch.object(mapped_backend, 'load_mapped_backend', return_value=Backend()), contextlib.redirect_stdout(io.StringIO()):
                    worker.execute(value, '8' * 64, output / 'START')
                self.assertEqual(seen, [(value['text'], value['reference'], value['controls'])])
                manifest = protocol.read_json(output / 'candidate' / 'manifest.json')
                self.assertEqual(manifest['evidence']['tempo']['peak'], 0.25)
                self.assertEqual((output / 'candidate' / 'raw.wav').read_bytes(), (output / 'candidate' / 'final.wav').read_bytes())

    def test_format_three_gate_imports_no_optional_library_before_start(self):
        with tempfile.TemporaryDirectory() as directory:
            value, output = configured_request(Path(directory), tempoPercent=80)
            (output / 'START').write_bytes(b'')
            (Path(directory) / value['adapter']['relativePath']).write_bytes(b'changed')
            raw = protocol.canonical_bytes(value); (output / 'request.json').write_bytes(raw)
            argv = [str(ENGINE / 'worker.py'), '--training-worker-root', str(TRAINING), '--request', str(output / 'request.json'),
                    '--request-sha256', protocol.sha256(raw), '--gate', str(output / 'START')]
            code = ("import sys,runpy\ndef audit(event,args):\n"
                    " if event=='import' and args[0].split('.')[0] in ('torch','numpy','librosa','soundfile','audioread','numba','voxcpm','transformers','safetensors','site'): raise RuntimeError('optional import')\n"
                    "sys.addaudithook(audit)\nsys.argv=" + repr(argv) + "\nrunpy.run_path(sys.argv[0],run_name='__main__')\n")
            child = subprocess.Popen([sys.executable, '-I', '-S', '-B', '-u', '-c', code], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            try:
                self.assertEqual(child.stdout.readline().strip(), 'voxcpm_inference.worker.gated')
                self.assertIsNone(child.poll())
                (output / 'START').write_bytes(b'START\n')
                _, stderr = child.communicate(timeout=15)
                self.assertEqual(child.returncode, 1)
                self.assertEqual(stderr.strip(), 'voxcpm_inference.worker.failed')
                self.assertFalse((output / 'candidate' / 'READY').exists())
            finally:
                if child.poll() is None: child.kill()
                child.communicate(timeout=5)

    def test_mismatched_controls_raw_proof_or_tempo_never_publish_ready(self):
        for mode in ('controls', 'seed', 'eos', 'raw', 'length', 'percent', 'algorithm', 'rawSamples', 'peak', 'extra', 'clipping', 'identity'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as directory:
                value, output = configured_request(Path(directory))
                result = configured_candidate(value)
                if mode == 'controls': result['evidence']['controls']['cfg'] = 3
                elif mode == 'seed': result['evidence']['seed'] += 1
                elif mode == 'eos': result['evidence']['naturalEos'] = False
                elif mode == 'raw': result['sourcePcm'] = bytes(8)
                elif mode == 'length': result['pcm'] += bytes(2)
                elif mode in ('percent', 'rawSamples', 'peak'): result['evidence']['tempo'][mode] += 1
                elif mode == 'algorithm': result['evidence']['tempo']['algorithm'] = 'librosa-phase-vocoder-v1'
                elif mode == 'extra': result['evidence']['tempo']['pitch'] = 0
                elif mode == 'clipping': result['pcm'] = struct.pack('<hhhh', 0, 32767, -4096, 0)
                else: result['pcm'] = struct.pack('<hhhh', 1, 8192, -4096, 0)
                with patch.object(worker, '_produce', return_value=result), contextlib.redirect_stdout(io.StringIO()), self.assertRaises(Exception):
                    worker.execute(value, '8' * 64, output / 'START')
                self.assertFalse((output / 'candidate' / 'READY').exists())

    def test_tempo_count_uses_ties_to_even_and_bounds_only_expand_new_profile(self):
        self.assertEqual(contract.tempo_sample_count(1966080, 80), 2457600)
        self.assertEqual(contract.tempo_sample_count(42, 112), 38)
        self.assertEqual(contract.tempo_sample_count(14, 112), 12)
        self.assertEqual(contract.tempo_sample_count(3, 120), 2)
        with self.assertRaises(Exception): worker.encode_pcm([0.1] * 1966081, 1966081)


class Array:
    def __init__(self, values, dtype='float32', shape=None):
        self.values = list(values); self.dtype = dtype
        self.shape = (len(self.values),) if shape is None else shape
    def tolist(self): return list(self.values)


class TempoTests(unittest.TestCase):
    def test_identity_never_imports_dsp_and_is_exact_pcm(self):
        raw = worker.encode_pcm([0.0, 0.5, -0.25, 0.0], 4)
        with patch.dict(sys.modules, {'numpy': None, 'librosa': None, 'librosa.effects': None}):
            final, evidence = worker.render_tempo([0.0, 0.5, -0.25, 0.0], raw, 100)
        self.assertIs(final, raw)
        self.assertEqual(evidence, dict(algorithm='identity-v1', percent=100, rawSamples=4, samples=4, peak=0.5))

    def test_declared_dsp_receives_fixed_parameters_and_preserves_final_length(self):
        seen = []
        def stretch(values, **kwargs):
            seen.append((values.values, kwargs))
            return Array([0.0, 0.5, -0.25, 0.0, 0.0])
        numpy = types.SimpleNamespace(asarray=lambda values, dtype: Array(values, dtype), float32='float32', ndarray=Array)
        effects = types.ModuleType('librosa.effects'); effects.time_stretch = stretch
        librosa = types.ModuleType('librosa'); librosa.effects = effects
        raw = worker.encode_pcm([0.0, 0.5, -0.25, 0.0], 4)
        with patch.dict(sys.modules, {'numpy': numpy, 'librosa': librosa, 'librosa.effects': effects}):
            final, proof = worker.render_tempo([0.0, 0.5, -0.25, 0.0], raw, 80)
        self.assertEqual(seen, [([0.0, 0.5, -0.25, 0.0], dict(rate=0.8, n_fft=2048, hop_length=512,
                                win_length=2048, window='hann', center=True))])
        self.assertEqual(final, raw + b'\0\0')
        self.assertEqual(proof, dict(algorithm='librosa-phase-vocoder-v1', percent=80, rawSamples=4, samples=5, peak=0.5))

    def test_dsp_rejects_nonfinite_overshoot_wrong_shape_length_and_rails_without_fallback(self):
        numpy = types.SimpleNamespace(asarray=lambda values, dtype: Array(values, dtype), float32='float32', ndarray=Array)
        for output in (Array([]), Array([0.1] * 4), Array([float('nan')] * 5), Array([1.01] * 5),
                       Array([1.0] * 5), Array([0.0] * 5), Array([0.1] * 5, dtype='float64'),
                       Array([0.1] * 5, shape=(1, 5))):
            effects = types.ModuleType('librosa.effects'); effects.time_stretch = lambda values, **kwargs: output
            librosa = types.ModuleType('librosa'); librosa.effects = effects
            with self.subTest(output=output.values[:1]), patch.dict(sys.modules, {'numpy': numpy, 'librosa': librosa, 'librosa.effects': effects}), self.assertRaises(Exception):
                worker.render_tempo([0.0, 0.5, -0.25, 0.0], b'\0' * 8, 80)


if __name__ == '__main__': unittest.main()
