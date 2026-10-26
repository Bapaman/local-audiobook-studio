"""Breaks caught: partial/foreign/tampered checkpoints becoming resumable."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
try:
    from las_rvc.checkpoints import CheckpointStore, RunIdentity, CheckpointError
except ModuleNotFoundError:
    CheckpointStore = RunIdentity = CheckpointError = None


class CheckpointTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(CheckpointStore, "Atomic owned checkpoint store is not implemented")
        parent = Path(__file__).resolve().parents[3] / ".artifacts" / "rvc-state-tests"
        parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="checkpoint-", dir=parent)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "run"
        self.root.mkdir()
        self.identity = RunIdentity(
            run_id="1" * 32, dataset_sha256="2" * 64, runtime_sha256="3" * 64,
            config_sha256="4" * 64, rvc_commit="81eed5e8f68b6bed1789f682fe78cdd324495afc",
            preset_id="rvc-cpu-dev-v1", batch_size=1, sample_rate=40000, f0=True,
        )
        self.store = CheckpointStore(self.root, self.identity)
        self.addCleanup(self.store.close)

    @staticmethod
    def write_payload(stage):
        for name, data in (("generator.pt", b"generator-v1"),
                           ("discriminator.pt", b"discriminator-v1"),
                           ("training.pt", b"training-state-v1")):
            with (stage / name).open("xb") as stream:
                stream.write(data)

    def assert_code(self, code, action):
        with self.assertRaises(CheckpointError) as raised:
            action()
        self.assertEqual(code, raised.exception.code)
        self.assertNotIn(str(self.root), str(raised.exception))

    def test_publishes_a_complete_three_file_checkpoint(self):
        point = self.store.publish(1, 7, self.write_payload)
        latest = self.store.latest()
        self.assertEqual((1, 2, 7), (latest.completed_epoch, latest.next_epoch, latest.global_step))
        self.assertEqual(point.directory, latest.directory)
        manifest_bytes = (point.directory / "manifest.json").read_bytes()
        manifest = json.loads(manifest_bytes)
        self.assertEqual(hashlib.sha256(manifest_bytes).hexdigest(),
                         (point.directory / "READY").read_text(encoding="ascii"))
        self.assertEqual({"generator.pt", "discriminator.pt", "training.pt"}, set(manifest["files"]))
        with latest.open_payload("generator.pt") as stream:
            self.assertEqual(b"generator-v1", stream.read())
        self.assertEqual([], list((self.root / "staging").iterdir()))

    def test_failed_writer_preserves_previous_checkpoint(self):
        first = self.store.publish(1, 7, self.write_payload)
        def interrupted(stage):
            (stage / "generator.pt").write_bytes(b"half-saved")
            raise RuntimeError("interrupted")
        self.assert_code("training.checkpoint.write_failed", lambda: self.store.publish(2, 14, interrupted))
        self.assertEqual(first.directory, self.store.latest().directory)
        self.assertEqual(1, len(list((self.root / "staging").iterdir())))
        self.assertFalse(any(x.name == "READY" for x in (self.root / "staging").rglob("*")))

    def test_corrupt_newest_rolls_back(self):
        first = self.store.publish(1, 7, self.write_payload)
        second = self.store.publish(2, 14, self.write_payload)
        (second.directory / "discriminator.pt").write_bytes(b"corrupt")
        self.assertEqual(first.directory, self.store.latest().directory)
        self.assert_code("training.checkpoint.invalid", lambda: second.open_payload("discriminator.pt"))

    def test_changed_dataset_is_rejected(self):
        self.store.publish(1, 7, self.write_payload)
        from dataclasses import replace
        with CheckpointStore(self.root, replace(self.identity, dataset_sha256="5" * 64)) as other:
            self.assert_code("training.resume.incompatible", other.latest)

    def test_payload_paths_and_symlinks_cannot_escape(self):
        point = self.store.publish(1, 7, self.write_payload)
        self.assert_code("training.checkpoint.invalid", lambda: point.open_payload("../sentinel.pt"))
        outside = Path(self.temp.name) / "outside"
        outside.mkdir()
        sentinel = outside / "sentinel.pt"
        sentinel.write_bytes(b"untouched")
        escaped = Path(self.temp.name) / "escaped-run"
        escaped.mkdir()
        junction = escaped / "checkpoints"
        if os.name == "nt":
            result = subprocess.run(["cmd.exe", "/c", "mklink", "/J", str(junction), str(outside)],
                                    capture_output=True, check=False)
            self.assertEqual(0, result.returncode, result.stderr.decode(errors="replace"))
            self.addCleanup(lambda: os.rmdir(junction))
        else:
            junction.symlink_to(outside, target_is_directory=True)
            self.addCleanup(junction.unlink)
        self.assert_code("training.path.invalid", lambda: CheckpointStore(escaped, self.identity))
        self.assertEqual(b"untouched", sentinel.read_bytes())
        self.assertEqual(["sentinel.pt"], [p.name for p in outside.iterdir()])

    def test_duplicate_epoch_does_not_overwrite(self):
        first = self.store.publish(1, 7, self.write_payload)
        self.assert_code("training.checkpoint.exists", lambda: self.store.publish(1, 99, self.write_payload))
        self.assertEqual(7, self.store.latest().global_step)
        with first.open_payload("training.pt") as stream:
            self.assertEqual(b"training-state-v1", stream.read())

    def test_closed_store_cannot_publish_or_discover(self):
        self.store.close()
        self.assert_code("training.store.closed", self.store.latest)
        self.assert_code("training.store.closed", lambda: self.store.publish(1, 7, self.write_payload))

    def test_corrupt_epoch_can_be_republished_without_overwriting_it(self):
        self.store.publish(1, 7, self.write_payload)
        corrupt = self.store.publish(2, 14, self.write_payload)
        (corrupt.directory / "generator.pt").write_bytes(b"corrupt point retained")
        self.assertEqual(1, self.store.latest().completed_epoch)
        try:
            replacement = self.store.publish(2, 14, self.write_payload)
        except CheckpointError:
            replacement = None
        self.assertIsNotNone(replacement, "Rollback must allow replay of the corrupt logical epoch")
        self.assertNotEqual(corrupt.directory, replacement.directory)
        self.assertEqual(b"corrupt point retained", (corrupt.directory / "generator.pt").read_bytes())
        self.assertEqual(replacement.directory, self.store.latest().directory)

    def test_writer_error_is_path_free(self):
        def private_error(stage):
            (stage / "generator.pt").write_bytes(b"partial")
            raise OSError("synthetic-private-path/voice.wav")
        try:
            self.store.publish(1, 7, private_error)
        except Exception as error:
            self.assertIsInstance(error, CheckpointError)
            self.assertEqual("training.checkpoint.write_failed", error.code)
            self.assertNotIn("synthetic-private-path", str(error))
            self.assertIsNone(error.__cause__)
        else:
            self.fail("Failed writer must not publish")

    def test_staging_is_pinned_before_the_writer_can_redirect_it(self):
        outside = Path(self.temp.name) / "outside-write"
        outside.mkdir()
        prevented = []
        def redirect(stage):
            if os.name != "nt":
                # Windows-only anti-rename guarantee; no skipped portable tests.
                self.write_payload(stage)
                return
            try:
                stage.rename(stage.with_name(stage.name + "-moved"))
            except OSError:
                prevented.append(True)
                self.write_payload(stage)
                return
            prevented.append(False)
            result = subprocess.run(["cmd.exe", "/c", "mklink", "/J", str(stage), str(outside)],
                                    capture_output=True, check=False)
            self.assertEqual(0, result.returncode)
            self.addCleanup(lambda: os.rmdir(stage))
            self.write_payload(stage)
        try:
            point = self.store.publish(1, 7, redirect)
        except CheckpointError:
            point = None
        self.assertIsNotNone(point, "Writer must use an already protected staging directory")
        if os.name == "nt":
            self.assertEqual([True], prevented)
        self.assertEqual([], list(outside.iterdir()))


if __name__ == "__main__":
    unittest.main()
