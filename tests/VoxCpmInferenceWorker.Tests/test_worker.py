import contextlib
import copy
import importlib.util
import io
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
ENGINE = REPO / 'src' / 'VoxCpmInferenceWorker'
TRAINING = REPO / 'src' / 'VoxCpmTrainingWorker'
sys.path.insert(0, str(ENGINE))
sys.path.append(str(TRAINING))
spec = importlib.util.spec_from_file_location('inference_worker', ENGINE / 'worker.py')
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)
import protocol
from test_backend import bundle


def request(root):
    public = json.loads((REPO / 'eng' / 'locks' / 'voxcpm2-training.lock.json').read_text(encoding='utf-8'))
    payload = bundle(public['adapterLayout'])
    identity = dict(fingerprint='a' * 64, sourceCommit=public['sourceCommit'], baseRevision=public['baseRevision'],
                    sourceLockSha256=protocol.sha256(protocol.canonical_bytes(public['sourceFiles'])),
                    modelLockSha256=protocol.sha256(protocol.canonical_bytes(public['modelFiles'])),
                    cacheSha256=public['cache']['sha256'], cacheLayoutSha256=public['cache']['layoutSha256'],
                    loaderSha256='b' * 64, recipeSha256=public['recipeSha256'], adapterLayoutSha256=public['adapterLayoutSha256'])
    value = dict(format=1, projectId='a' * 32, jobId='b' * 32, segmentId='c' * 32, attemptId='d' * 32,
                 projectRoot=str(root), outputRelativeRoot='segments/' + 'b' * 32 + '/' + 'd' * 32 + '-vox',
                 text='  Новый полный текст.\n', textSha256=protocol.sha256('  Новый полный текст.\n'.encode()),
                 binding=dict(projectId='a' * 32, checkpointId='e' * 32, globalStep=29, manifestSha256='c' * 64,
                              adapterSha256=protocol.sha256(payload), runtimeFingerprint=identity['fingerprint'],
                              recipeSha256=identity['recipeSha256'], adapterLayoutSha256=identity['adapterLayoutSha256']),
                 runtime=dict(dependencySites=[str(root / 'sites')], sourceRoot=str(root / 'source'),
                              sourceFiles=public['sourceFiles'], modelRoot=str(root / 'models'), modelFiles=public['modelFiles'],
                              runtimeInventoryPath=str(root / 'inventory.json'), runtimeInventorySha256='f' * 64,
                              cache=dict(path=str(root / 'frozen.bin'), **public['cache']), identity=identity,
                              parameterLayout=public['adapterLayout']),
                 adapter=dict(relativePath='voxcpm-training/actions/' + '1' * 32 + '/executions/' + '2' * 32
                              + '/windows/' + '3' * 32 + '/checkpoints/' + 'e' * 32 + '/lora_weights.safetensors',
                              bytes=len(payload), sha256=protocol.sha256(payload)))
    adapter = root / value['adapter']['relativePath']
    adapter.parent.mkdir(parents=True)
    adapter.write_bytes(payload)
    output = root / value['outputRelativeRoot']
    output.mkdir(parents=True)
    (output / 'START').write_bytes(b'START\n')
    return value, output


def candidate(value):
    evidence = dict(profileId='voxcpm2-saved-adapter-cpu-v1', seed=42, binding=copy.deepcopy(value['binding']),
                    textSha256=value['textSha256'], tokensSha256='7' * 64, textCharacters=len(value['text']),
                    tokenCount=5, cacheCapacity=46, cacheBytes=3391488, effectivePatchLimit=40,
                    generatedPatches=4, naturalEos=True, sampleRate=48000, samples=4, peak=0.25)
    return dict(pcm=struct.pack('<hhhh', 0, 8192, -4096, 0), evidence=evidence)


class WorkerTests(unittest.TestCase):
    def test_pcm_encoding_preserves_sample_count_and_does_not_normalize_or_clip(self):
        self.assertEqual(worker.encode_pcm([0.0, 0.5, -0.25, 0.0], 4), struct.pack('<hhhh', 0, 16384, -8192, 0))
        for samples, count in (([0.5], 2), ([float('nan')], 1), ([1.01], 1), ([True], 1)):
            with self.subTest(samples=samples), self.assertRaises(Exception):
                worker.encode_pcm(samples, count)

    def test_request_is_strict_and_rejects_text_or_adapter_or_namespace_substitution(self):
        with tempfile.TemporaryDirectory() as directory:
            value, output = request(Path(directory))
            self.assertIs(worker.validate_request(value), value)
            for mode in ('text', 'binding', 'adapter', 'namespace', 'counter', 'unknown'):
                changed = copy.deepcopy(value)
                if mode == 'text': changed['text'] = 'shortened'
                elif mode == 'binding': changed['binding']['runtimeFingerprint'] = '9' * 64
                elif mode == 'adapter': changed['adapter']['relativePath'] = '../lora_weights.safetensors'
                elif mode == 'namespace': changed['outputRelativeRoot'] += '/other'
                elif mode == 'counter': changed['binding']['globalStep'] = True
                else: changed['unknown'] = 'private'
                with self.subTest(mode=mode), self.assertRaises(Exception):
                    worker.validate_request(changed)

    def test_real_wrapper_publishes_only_bound_pcm_and_ready_is_last(self):
        with tempfile.TemporaryDirectory() as directory:
            value, output = request(Path(directory))
            writes = []
            original = protocol.write_bytes
            def write(path, payload):
                writes.append(Path(path).name)
                return original(path, payload)
            def produce(r, phase):
                self.assertEqual(r['text'], '  Новый полный текст.\n')
                phase('generating')
                return candidate(r)
            with patch.object(worker, '_produce', side_effect=produce), patch.object(protocol, 'write_bytes', side_effect=write), contextlib.redirect_stdout(io.StringIO()):
                worker.execute(value, '8' * 64, output / 'START')
            root = output / 'candidate'
            manifest = protocol.read_json(root / 'manifest.json')
            self.assertEqual(writes[-1], 'READY')
            self.assertEqual(manifest['requestSha256'], '8' * 64)
            self.assertEqual(manifest['evidence']['binding'], value['binding'])
            self.assertEqual(manifest['audio']['samples'], 4)
            self.assertEqual((root / 'final.wav').read_bytes()[44:], struct.pack('<hhhh', 0, 8192, -4096, 0))
            self.assertEqual((root / 'READY').read_bytes(), (protocol.sha256((root / 'manifest.json').read_bytes()) + '\n').encode())

    def test_invalid_eos_or_evidence_or_pcm_never_publishes_ready(self):
        for mode in ('eos', 'cache', 'tokens', 'text', 'binding', 'length', 'clipping', 'silent'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as directory:
                value, output = request(Path(directory))
                result = candidate(value)
                if mode == 'eos': result['evidence']['naturalEos'] = False
                elif mode == 'cache': result['evidence']['cacheBytes'] += 4
                elif mode == 'tokens': result['evidence']['tokenCount'] = 5.0
                elif mode == 'text': result['evidence']['textCharacters'] -= 1
                elif mode == 'binding': result['evidence']['binding']['checkpointId'] = '9' * 32
                elif mode == 'length': result['pcm'] += b'xx'
                elif mode == 'clipping': result['pcm'] = struct.pack('<hhhh', 0, 32767, -4096, 0)
                else: result['pcm'] = bytes(8)
                with patch.object(worker, '_produce', return_value=result), contextlib.redirect_stdout(io.StringIO()), self.assertRaises(Exception):
                    worker.execute(value, '8' * 64, output / 'START')
                self.assertFalse((output / 'candidate' / 'READY').exists())

    def test_wrong_gate_or_changed_adapter_fails_before_backend(self):
        with tempfile.TemporaryDirectory() as directory:
            value, output = request(Path(directory))
            with patch.object(worker, '_produce', side_effect=AssertionError('backend must not run')):
                with self.assertRaises(Exception):
                    worker.execute(value, '8' * 64, output / 'different')
                (Path(directory) / value['adapter']['relativePath']).write_bytes(b'changed')
                with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(Exception):
                    worker.execute(value, '8' * 64, output / 'START')

    def test_real_isolated_gate_attempts_no_optional_import_before_start(self):
        with tempfile.TemporaryDirectory() as directory:
            value, output = request(Path(directory))
            (output / 'START').write_bytes(b'')
            sites = Path(directory) / 'sites'; sites.mkdir()
            marker = Path(directory) / 'pth-executed'
            (sites / 'hostile.pth').write_text(f'import pathlib; pathlib.Path({str(marker)!r}).touch()\n')
            # After START, intentionally corrupt adapter: root-owned child exits
            # before optional imports or real ML, under an import audit guard.
            (Path(directory) / value['adapter']['relativePath']).write_bytes(b'changed')
            raw = protocol.canonical_bytes(value)
            (output / 'request.json').write_bytes(raw)
            argv = [str(ENGINE / 'worker.py'), '--training-worker-root', str(TRAINING), '--request', str(output / 'request.json'),
                    '--request-sha256', protocol.sha256(raw), '--gate', str(output / 'START')]
            audit = ("import sys,runpy\ndef audit(event,args):\n"
                     " if event=='import' and args[0].split('.')[0] in ('torch','numpy','transformers','safetensors','site'): raise RuntimeError('optional import')\n"
                     "sys.addaudithook(audit)\nsys.argv=" + repr(argv) + "\nrunpy.run_path(sys.argv[0],run_name='__main__')\n")
            child = subprocess.Popen([sys.executable, '-I', '-S', '-B', '-u', '-c', audit],
                                     stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            try:
                self.assertEqual(child.stdout.readline().strip(), 'voxcpm_inference.worker.gated')
                self.assertIsNone(child.poll()); self.assertFalse(marker.exists())
                (output / 'START').write_bytes(b'START\n')
                stdout, stderr = child.communicate(timeout=15)
                self.assertEqual(child.returncode, 1)
                self.assertNotIn('RuntimeError', stderr)
                self.assertEqual(stderr.strip(), 'voxcpm_inference.worker.failed')
                self.assertFalse(marker.exists())
            finally:
                if child.poll() is None: child.kill()
                child.communicate(timeout=5)

    def test_invalid_cli_is_fixed_and_never_echoes_private_arguments(self):
        result = subprocess.run([sys.executable, '-I', '-S', '-B', str(ENGINE / 'worker.py'), '--PRIVATE-ARGUMENT'],
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stdout, '')
        self.assertEqual(result.stderr.strip(), 'voxcpm_inference.worker.failed')


if __name__ == '__main__':
    unittest.main()
