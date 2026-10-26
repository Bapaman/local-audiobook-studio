import contextlib
import io
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import fixtures
import protocol
import worker
import shutil


class BootstrapTests(unittest.TestCase):
    def test_gate_imports_no_optional_library_or_pth_before_start(self):
        # A real isolated child. A .pth in explicit dependencySites must not run.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            request = fixtures.request(root)
            sites = root / "sites"
            sites.mkdir()
            marker = root / "PTH_EXECUTED"
            (sites / "hostile.pth").write_text(f"import pathlib; pathlib.Path({str(marker)!r}).touch()\n", encoding="utf-8")
            output = root / request["outputRelativeRoot"]
            output.mkdir(parents=True)
            (output / "START").write_bytes(b"")
            (output / "request.json").write_bytes(fixtures.encoded(request))
            # The test-owned trainer fails if imported before START; after STOP
            # it must never be imported at all. Optional libraries are absent.
            command = [sys.executable, "-I", "-S", "-B", "-u", str(fixtures.WORKER / "worker.py"), "--request", str(output / "request.json"), "--request-sha256", fixtures.sha(fixtures.encoded(request)), "--gate", str(output / "START")]
            # Audit every attempted import in the real entrypoint. In this
            # stopped run none of the optional libraries may be requested.
            audit = "import sys,runpy\ndef audit(event,args):\n if event=='import' and args[0].split('.')[0] in ('torch','numpy','transformers','safetensors','site'): raise RuntimeError('optional import before admission')\nsys.addaudithook(audit)\nsys.argv=" + repr(command[5:]) + "\nrunpy.run_path(sys.argv[0],run_name='__main__')\n"
            command = command[:5] + ["-c", audit]
            child = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            try:
                self.assertEqual(child.stdout.readline().strip(), "voxcpm_training.worker.gated")
                self.assertIsNone(child.poll())
                self.assertFalse(marker.exists())
                (root / "voxcpm-training" / "actions" / request["actionId"] / "executions" / request["executionId"] / "STOP").write_bytes(b"")
                (output / "START").write_bytes(b"START\n")
                stdout, stderr = child.communicate(timeout=15)
                self.assertEqual(child.returncode, 0, stderr)
                self.assertEqual(stderr, "")
                self.assertEqual(stdout.strip(), "voxcpm_training.worker.ready")
                self.assertFalse(marker.exists())
                result = protocol.read_json(output / "result.json")
                self.assertTrue(result["stopped"])
                self.assertIsNone(result["checkpoint"])
            finally:
                if child.poll() is None:
                    child.kill()
                child.communicate(timeout=5)

    def test_invalid_cli_never_prints_argument_or_path(self):
        command = [sys.executable, "-I", "-S", "-B", str(fixtures.WORKER / "worker.py"), "--private-transcript-do-not-print"]
        result = subprocess.run(command, capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stdout, "")
        self.assertEqual(result.stderr.strip(), "voxcpm_training.worker.failed")

    def test_valid_candidate_publication_and_library_streams_are_private(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            request = fixtures.request(root)
            output = root / request["outputRelativeRoot"]
            output.mkdir(parents=True)
            (output / "START").write_bytes(b"START\n")
            modules = root / "worker"
            modules.mkdir()
            for name in ("worker", "protocol", "owned_files", "checkpoint"):
                shutil.copyfile(fixtures.WORKER / (name + ".py"), modules / (name + ".py"))
            data = fixtures.payloads(request)
            # A test-owned pure-stdlib computation double; real wrapper, IO,
            # gate, preflight, stderr/native-fd suppression and READY are used.
            (modules / "trainer.py").write_text("import os,sys\ndef train_one(request, phase):\n    print('PRIVATE TRANSCRIPT')\n    os.write(2,b'PRIVATE PATH')\n    phase('training')\n    return " + repr(dict(payloads=data, metrics=dict(loss=0.5, gradientNorm=0.25))) + "\n", encoding="utf-8")
            payload = fixtures.encoded(request)
            (output / "request.json").write_bytes(payload)
            result = subprocess.run([sys.executable, "-I", "-S", "-B", "-u", str(modules / "worker.py"), "--request", str(output / "request.json"), "--request-sha256", fixtures.sha(payload), "--gate", str(output / "START")], capture_output=True, text=True, timeout=15)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stderr, "")
            self.assertEqual(result.stdout.splitlines(), ["voxcpm_training.worker.gated", "voxcpm_training.worker.loading", "voxcpm_training.worker.training", "voxcpm_training.worker.checkpointing", "voxcpm_training.worker.ready"])
            saved = protocol.read_json(output / "result.json")
            self.assertEqual(saved["checkpoint"]["cursor"], dict(globalStep=2, completedEpochs=1, epochSize=2, nextRetainedOrdinal=0, nextClipId="9" * 32))
            self.assertFalse(saved["stopped"])
            self.assertEqual((output / "READY").read_bytes(), (fixtures.sha((output / "result.json").read_bytes()) + "\n").encode())

    def test_ready_last_and_stopped_empty_has_no_checkpoint(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            request = fixtures.request(root)
            output = root / request["outputRelativeRoot"]
            output.mkdir(parents=True)
            (output / "START").write_bytes(b"START\n")
            stop = output.parents[1] / "STOP"
            stop.write_bytes(b"")
            with contextlib.redirect_stdout(io.StringIO()):
                worker.execute(request, "e" * 64, output / "START")
            self.assertFalse((output / "checkpoints").exists())
            self.assertEqual((output / "READY").read_bytes(), (fixtures.sha((output / "result.json").read_bytes()) + "\n").encode())

    def test_failed_candidate_never_publishes_window_ready(self):
        # Replacing only ML computation is unavoidable in a stdlib-only test.
        import types
        trainer = types.ModuleType("trainer")
        trainer.train_one = lambda request, phase: {"untrusted": "candidate"}
        with tempfile.TemporaryDirectory() as directory, patch.dict(sys.modules, trainer=trainer):
            root = Path(directory)
            request = fixtures.request(root)
            output = root / request["outputRelativeRoot"]
            output.mkdir(parents=True)
            (output / "START").write_bytes(b"START\n")
            with self.assertRaises(Exception), contextlib.redirect_stdout(io.StringIO()):
                worker.execute(request, "e" * 64, output / "START")
            self.assertFalse((output / "READY").exists())
