"""Reference-only contract tests; optional tensor/model code stays doubled."""
import contextlib
import copy
import dataclasses
import io
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

from fakes import Model, Tensor, Torch
from test_contract import binding
from test_worker import ENGINE, TRAINING, candidate, protocol, request, worker
import inference_contract as contract
import mapped_backend
from synthesis import synthesize_loaded
from owned_files import io_path


REFERENCE_PROFILE = 'voxcpm2-saved-adapter-reference-cpu-v1'


def reference_values(**changes):
    values = dict(corpus_version_id='1' * 32, corpus_manifest_sha256='2' * 64,
                  corpus_binding_sha256='3' * 64, clip_id='4' * 32, training_ordinal=13,
                  relative_path='tts-corpus/actions/' + '5' * 32 + '/audio/clip.wav',
                  bytes=403404, sha256='6' * 64, sample_rate=16000, samples=201680)
    values.update(changes)
    return values


def wire_reference(**changes):
    values = dict(corpusVersionId='1' * 32, corpusManifestSha256='2' * 64,
                  corpusBindingSha256='3' * 64, clipId='4' * 32, trainingOrdinal=13,
                  relativePath='tts-corpus/actions/' + '5' * 32 + '/audio/clip.wav',
                  bytes=403404, sha256='6' * 64, sampleRate=16000, samples=201680)
    values.update(changes)
    return values


def wave(samples=201680):
    payload = struct.pack('<hh', 100, -100) * (samples // 2) + (struct.pack('<h', 100) if samples % 2 else b'')
    return (b'RIFF' + struct.pack('<I', 36 + len(payload)) + b'WAVEfmt '
            + struct.pack('<IHHIIHH', 16, 1, 1, 16000, 32000, 2, 16)
            + b'data' + struct.pack('<I', len(payload)) + payload)


def reference_request(root):
    value, output = request(root)
    payload = wave()
    item = wire_reference(sha256=protocol.sha256(payload))
    path = io_path(root / item['relativePath'])
    path.parent.mkdir(parents=True)
    path.write_bytes(payload)
    value.update(format=2, profileId=REFERENCE_PROFILE, reference=item)
    return value, output


def reference_candidate(value):
    result = candidate(value)
    result['evidence'].update(profileId=REFERENCE_PROFILE, reference=copy.deepcopy(value['reference']),
                              referencePatches=79, referencePrefixSlots=81,
                              cacheCapacity=127, cacheBytes=9363456)
    return result


class ReferenceModel(Model):
    def __init__(self, torch):
        super().__init__(torch)
        self._encode_sample_rate, self.chunk_size, self.feat_dim = 16000, 640, 64
        self.audio_vae = types.SimpleNamespace(sample_rate=16000, chunk_size=640, latent_dim=64)
        self.encode_inputs = []
        self.encoding = Tensor([0.25], (79, 4, 64))
        self.encode_count = 1
        self.encode_changes = {}
        self.after_encode_failure = False
        self._encode_wav = self.encode

    def encode(self, wav_path, padding_mode='right', trim_silence_vad=False):
        self.encode_inputs.append((wav_path, padding_mode, trim_silence_vad))
        return self.encoding

    def generate(self, **kwargs):
        for _ in range(self.encode_count):
            arguments = dict(wav_path=kwargs['reference_wav_path'], padding_mode='right', trim_silence_vad=False)
            arguments.update(self.encode_changes)
            self._encode_wav(**arguments)
        if self.after_encode_failure:
            raise RuntimeError('synthetic failure after reference encoding')
        return super().generate(**kwargs)


class ReferenceContractTests(unittest.TestCase):
    def make_reference(self, **changes):
        self.assertTrue(hasattr(contract, 'ReferenceBinding'), 'ReferenceBinding is required')
        return contract.ReferenceBinding(**reference_values(**changes))

    def test_reference_binding_is_closed_immutable_and_counts_internal_padding(self):
        item = self.make_reference()
        item.validate()
        self.assertEqual(item.as_dict(), wire_reference())
        self.assertEqual((item.patches, item.prefix_slots), (79, 81))
        with self.assertRaises(dataclasses.FrozenInstanceError):
            item.samples = 80000
        for count, patches in ((80000, 32), (81920, 32), (81921, 33), (256000, 100)):
            item = self.make_reference(samples=count, bytes=44 + 2 * count)
            item.validate()
            self.assertEqual(item.patches, patches)
        audio = contract.ReferenceAudio(self.make_reference(), r'\\?\C:\approved\clip.wav')
        audio.validate()
        with self.assertRaises(dataclasses.FrozenInstanceError):
            audio.path = 'changed'

    def test_reference_validation_rejects_foreign_paths_invalid_lengths_and_scalar_types(self):
        for changes in (dict(corpus_version_id='A' * 32), dict(clip_id='x'), dict(training_ordinal=True),
                        dict(training_ordinal=512), dict(sample_rate=48000), dict(samples=79999, bytes=160042),
                        dict(samples=256001, bytes=512046), dict(bytes=403405), dict(sha256='6' * 63),
                        dict(relative_path='../clip.wav'), dict(relative_path='tts-corpus/actions/x/../clip.wav'),
                        dict(relative_path='external/clip.wav'), dict(relative_path='tts-corpus/actions/' + '5' * 32 + '/CON.wav'),
                        dict(relative_path='tts-corpus/actions/' + '5' * 32 + '/clip.wav:stream')):
            with self.subTest(changes=changes):
                item = self.make_reference(**changes)
                with self.assertRaises(Exception):
                    item.validate()

    def test_reference_budget_includes_prefix_and_keeps_legacy_formula(self):
        item = self.make_reference()
        plan = contract.plan_synthesis('target', [1, 2, 3, 4, 5], 127, item)
        self.assertEqual((plan.patch_limit, plan.cache_capacity), (40, 127))
        self.assertEqual(contract.plan_synthesis('target', [1, 2, 3, 4, 5], 8192).cache_capacity, 46)
        with self.assertRaises(contract.InferenceError):
            contract.plan_synthesis('target', [1, 2, 3, 4, 5], 126, item)
        largest = self.make_reference(samples=256000, bytes=512044)
        self.assertEqual(contract.plan_synthesis('target', [1] * 256, 8192, largest).cache_capacity, 615)


class ReferenceSynthesisTests(unittest.TestCase):
    make_reference = ReferenceContractTests.make_reference
    def setUp(self):
        self.torch = Torch()
        self.model = ReferenceModel(self.torch)
        self.tokenizer, self.encoder = self.model.text_tokenizer, self.model._encode_wav
        decoder = types.ModuleType('audioread')
        decoder.available_backends = lambda: [object]
        modules = patch.dict(sys.modules, audioread=decoder)
        modules.start()
        self.addCleanup(modules.stop)

    def make_audio(self):
        item = self.make_reference()
        return contract.ReferenceAudio(item, r'\\?\C:\approved\clip.wav')

    def assert_restored(self):
        self.assertIs(self.model.text_tokenizer, self.tokenizer)
        self.assertIs(self.model._encode_wav, self.encoder)
        self.assertIsNone(self.model.stop_head.hook)
        self.assertIsNone(self.model.base_lm.kv_cache)
        self.assertIsNone(self.model.residual_lm.kv_cache)
        self.assertFalse(self.torch.inference_active)

    def test_saved_adapter_plus_reference_uses_direct_generate_and_generated_only_eos(self):
        ref = self.make_audio()
        audio, proof = synthesize_loaded(self.model, self.torch, 'target only', binding(), ref)
        self.assertIs(audio, self.model.waveform)
        self.assertEqual(self.model.arguments['reference_wav_path'], ref.path)
        self.assertEqual((self.model.arguments['prompt_text'], self.model.arguments['prompt_wav_path']), ('', ''))
        self.assertEqual(self.model.tokenizer_inputs, ['target only'] * 3)
        self.assertEqual(self.model.encode_inputs, [(ref.path, 'right', False)])
        self.assertEqual(self.model.base_lm.allocations, [(1, 127, 'cpu', 'float32')])
        self.assertEqual((proof['cacheCapacity'], proof['cacheBytes']), (127, 32512))
        self.assertEqual((proof['generatedPatches'], proof['samples']), (4, 4))
        self.assertEqual((proof['referencePatches'], proof['referencePrefixSlots']), (79, 81))
        self.assertEqual(proof['reference'], wire_reference())
        self.assertEqual(proof['profileId'], REFERENCE_PROFILE)
        self.assertEqual(proof['binding'], binding().as_dict())
        self.assert_restored()

    def test_missing_extra_or_changed_encoder_calls_fail_closed_and_restore(self):
        for mode in ('missing', 'extra', 'path', 'left', 'trim', 'failure'):
            self.setUp()
            if mode == 'missing': self.model.encode_count = 0
            elif mode == 'extra': self.model.encode_count = 2
            elif mode == 'path': self.model.encode_changes = dict(wav_path=r'C:\other.wav')
            elif mode == 'left': self.model.encode_changes = dict(padding_mode='left')
            elif mode == 'trim': self.model.encode_changes = dict(trim_silence_vad=True)
            else: self.model.after_encode_failure = True
            with self.subTest(mode=mode), self.assertRaises((contract.InferenceError, RuntimeError)):
                synthesize_loaded(self.model, self.torch, 'target', binding(), self.make_audio())
            self.assert_restored()

    def test_encoder_shape_dtype_device_and_nonfinite_values_are_checked(self):
        for encoding in (Tensor([0.1], (78, 4, 64)), Tensor([0.1], (79, 3, 64)),
                         Tensor([0.1], (79, 4, 63)), Tensor([float('nan')], (79, 4, 64)),
                         Tensor([0.1], (79, 4, 64), dtype='float16'), Tensor([0.1], (79, 4, 64), device='cuda')):
            self.setUp()
            self.model.encoding = encoding
            with self.subTest(shape=encoding.shape), self.assertRaises(contract.InferenceError):
                synthesize_loaded(self.model, self.torch, 'target', binding(), self.make_audio())
            self.assert_restored()

    def test_reference_model_shape_and_expanded_cache_limits_reject_before_allocation(self):
        for mode in ('rate', 'vae_rate', 'chunk', 'vae_chunk', 'patch', 'feat', 'latent', 'model', 'base', 'residual', 'memory'):
            self.setUp()
            if mode == 'rate': self.model._encode_sample_rate = 48000
            elif mode == 'vae_rate': self.model.audio_vae.sample_rate = 48000
            elif mode == 'chunk': self.model.chunk_size = 641
            elif mode == 'vae_chunk': self.model.audio_vae.chunk_size = 641
            elif mode == 'patch': self.model.patch_size = 5
            elif mode == 'feat': self.model.feat_dim = 65
            elif mode == 'latent': self.model.audio_vae.latent_dim = 65
            elif mode == 'model': self.model.config.max_length = 126
            elif mode == 'memory': self.model.base_lm.config.num_hidden_layers = 1024; self.model.base_lm.config.kv_channels = 1024
            else: getattr(self.model, mode + '_lm').config.max_position_embeddings = 126
            with self.subTest(mode=mode), self.assertRaises(contract.InferenceError):
                synthesize_loaded(self.model, self.torch, 'target', binding(), self.make_audio())
            self.assertEqual(self.model.base_lm.allocations, [])
            self.assertEqual(self.model.residual_lm.allocations, [])

    def test_backend_optional_reference_reaches_the_checked_synthesis_path(self):
        ref = self.make_audio()
        with mapped_backend.MappedBackend(self.model, self.torch, binding()) as backend:
            _, proof = backend.synthesize('target', ref)
        self.assertEqual(proof['reference'], wire_reference())

    def test_reference_decoder_suppresses_backend_probe_and_restores_after_success(self):
        audioread = types.ModuleType('audioread')
        calls = []
        def probe():
            calls.append('unadmitted decoder probe')
            return [object]
        audioread.available_backends = probe
        original = self.model._encode_wav
        def encode(*args, **kwargs):
            self.assertEqual(audioread.available_backends(), [])
            return original(*args, **kwargs)
        self.model._encode_wav = self.encoder = encode
        with patch.dict(sys.modules, audioread=audioread):
            synthesize_loaded(self.model, self.torch, 'target', binding(), self.make_audio())
        self.assertEqual(calls, [])
        self.assertIs(audioread.available_backends, probe)
        self.assert_restored()

    def test_reference_decoder_restores_backend_callback_after_decode_failure(self):
        audioread = types.ModuleType('audioread')
        def probe():
            self.fail('unadmitted decoder probe')
        audioread.available_backends = probe
        def decode_failure(*args, **kwargs):
            self.assertEqual(audioread.available_backends(), [])
            # audioread.audio_open uses this same empty callback on soundfile failure.
            raise RuntimeError('canonical decoder failed')
        self.model._encode_wav = self.encoder = decode_failure
        with patch.dict(sys.modules, audioread=audioread), self.assertRaises(RuntimeError):
            synthesize_loaded(self.model, self.torch, 'target', binding(), self.make_audio())
        self.assertIs(audioread.available_backends, probe)
        self.assert_restored()

    def test_legacy_generation_does_not_touch_audioread_callback(self):
        audioread = types.ModuleType('audioread')
        def unavailable():
            self.fail('legacy generation must not touch decoder')
        audioread.available_backends = unavailable
        model = Model(self.torch)
        with patch.dict(sys.modules, audioread=audioread):
            _, proof = synthesize_loaded(model, self.torch, 'target', binding())
        self.assertIs(audioread.available_backends, unavailable)
        self.assertEqual(proof['profileId'], 'voxcpm2-saved-adapter-cpu-v1')
        self.assertNotIn('reference', proof)


class ReferenceWorkerTests(unittest.TestCase):
    def feature(self):
        self.assertTrue(hasattr(worker, 'reference_audio'), 'guarded reference_audio is required')

    def test_format_two_accepts_only_exact_reference_and_profile_fields(self):
        self.feature()
        with tempfile.TemporaryDirectory() as directory:
            value, _ = reference_request(Path(directory))
            self.assertIs(worker.validate_request(value), value)
            for mode in ('format', 'profile', 'missing', 'extra', 'reference_missing', 'reference_extra', 'rate', 'ordinal', 'bytes', 'path'):
                changed = copy.deepcopy(value)
                if mode == 'format': changed['format'] = 1
                elif mode == 'profile': changed['profileId'] = 'voxcpm2-saved-adapter-cpu-v1'
                elif mode == 'missing': changed.pop('profileId')
                elif mode == 'extra': changed['unknown'] = 1
                elif mode == 'reference_missing': changed['reference'].pop('clipId')
                elif mode == 'reference_extra': changed['reference']['text'] = 'untrusted transcript'
                elif mode == 'rate': changed['reference']['sampleRate'] = 48000
                elif mode == 'ordinal': changed['reference']['trainingOrdinal'] = True
                elif mode == 'bytes': changed['reference']['bytes'] -= 1
                else: changed['reference']['relativePath'] = '../elsewhere.wav'
                with self.subTest(mode=mode), self.assertRaises(Exception):
                    worker.validate_request(changed)

    def test_guarded_reference_uses_extended_long_path_and_rejects_wav_or_digest_drift(self):
        self.feature()
        with tempfile.TemporaryDirectory(dir=str(io_path(tempfile.gettempdir()))) as directory:
            root = io_path(Path(directory) / ('long-' * 20) / ('approved-' * 12))
            value, _ = reference_request(root)
            with worker.reference_audio(value) as audio:
                self.assertEqual(audio.binding.as_dict(), value['reference'])
                self.assertEqual(audio.binding.patches, 79)
                self.assertTrue(len(audio.path) > 260)
                if os.name == 'nt': self.assertTrue(audio.path.startswith('\\\\?\\'))
                self.assertEqual(Path(audio.path).read_bytes()[:12], b'RIFF\xc4\x27\x06\x00WAVE')
                if os.name == 'nt':
                    with self.assertRaises(OSError):
                        Path(audio.path).write_bytes(b'drift')
            path = protocol.safe_path(value['projectRoot'], value['reference']['relativePath'])
            original = path.read_bytes()
            for mode in ('digest', 'rate', 'length', 'extra', 'truncated'):
                changed = bytearray(original)
                if mode == 'digest': changed[-1] ^= 1
                elif mode == 'rate': struct.pack_into('<I', changed, 24, 48000)
                elif mode == 'length': struct.pack_into('<I', changed, 40, len(changed))
                elif mode == 'extra': changed.extend(b'x')
                else: changed = changed[:-2]
                path.write_bytes(changed)
                adjusted = copy.deepcopy(value)
                if mode != 'digest': adjusted['reference']['sha256'] = protocol.sha256(changed)
                with self.subTest(mode=mode), self.assertRaises(Exception):
                    with worker.reference_audio(adjusted): pass

    def test_reference_evidence_is_closed_exact_and_recomputes_prefix_budget(self):
        self.feature()
        with tempfile.TemporaryDirectory() as directory:
            value, _ = reference_request(Path(directory))
            result = reference_candidate(value)
            worker.validate_evidence(value, result['evidence'], result['pcm'])
            for mode in ('reference', 'patches', 'prefix', 'capacity', 'bytes', 'legacy_profile', 'extra', 'missing'):
                evidence = copy.deepcopy(result['evidence'])
                if mode == 'reference': evidence['reference']['clipId'] = '9' * 32
                elif mode == 'patches': evidence['referencePatches'] = 78
                elif mode == 'prefix': evidence['referencePrefixSlots'] = 80
                elif mode == 'capacity': evidence['cacheCapacity'] = 46
                elif mode == 'bytes': evidence['cacheBytes'] -= 1
                elif mode == 'legacy_profile': evidence['profileId'] = 'voxcpm2-saved-adapter-cpu-v1'
                elif mode == 'extra': evidence['referenceTranscript'] = 'private'
                else: evidence.pop('reference')
                with self.subTest(mode=mode), self.assertRaises(Exception):
                    worker.validate_evidence(value, evidence, result['pcm'])

    def test_reference_wav_is_validated_after_start_before_producer_and_never_published_on_drift(self):
        self.feature()
        with tempfile.TemporaryDirectory() as directory:
            value, output = reference_request(Path(directory))
            protocol.safe_path(value['projectRoot'], value['reference']['relativePath']).write_bytes(b'changed')
            with patch.object(worker, '_produce', side_effect=AssertionError('backend must not run')), contextlib.redirect_stdout(io.StringIO()), self.assertRaises(Exception):
                worker.execute(value, '8' * 64, output / 'START')
            self.assertFalse((output / 'candidate' / 'READY').exists())

    def test_complete_reference_worker_passes_guarded_audio_and_publishes_bound_ready(self):
        self.feature()
        with tempfile.TemporaryDirectory() as directory:
            value, output = reference_request(Path(directory))
            def produce(request_value, phase, reference):
                self.assertEqual(reference.binding.as_dict(), value['reference'])
                self.assertEqual(reference.path, str(protocol.safe_path(value['projectRoot'], value['reference']['relativePath'])))
                if os.name == 'nt':
                    with self.assertRaises(OSError): Path(reference.path).write_bytes(b'changed')
                phase('generating')
                return reference_candidate(request_value)
            with patch.object(worker, '_produce', side_effect=produce), contextlib.redirect_stdout(io.StringIO()):
                worker.execute(value, '8' * 64, output / 'START')
            root = output / 'candidate'
            manifest = protocol.read_json(root / 'manifest.json')
            self.assertEqual(manifest['evidence']['reference'], value['reference'])
            self.assertEqual(manifest['evidence']['cacheCapacity'], 127)
            self.assertEqual((root / 'READY').read_bytes(), (protocol.sha256((root / 'manifest.json').read_bytes()) + '\n').encode())

    def test_format_two_gate_stays_stdlib_only_and_does_not_read_reference_before_start(self):
        self.feature()
        with tempfile.TemporaryDirectory() as directory:
            value, output = reference_request(Path(directory))
            (output / 'START').write_bytes(b'')
            protocol.safe_path(value['projectRoot'], value['reference']['relativePath']).write_bytes(b'changed')
            raw = protocol.canonical_bytes(value)
            (output / 'request.json').write_bytes(raw)
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


if __name__ == '__main__':
    unittest.main()
