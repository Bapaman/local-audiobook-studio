import copy
import tempfile
import unittest
from pathlib import Path
import fixtures
import protocol


class ProtocolTests(unittest.TestCase):
    def test_empty_public_package_marker_is_valid_but_negative_or_float_sizes_are_not(self):
        value = fixtures.request()
        marker = next(iter(value['sourceFiles'].values()))
        marker.update(bytes=0, sha256=protocol.sha256(b''))
        self.assertIs(protocol.validate_request(value), value)
        for invalid in (-1, 0.0, False):
            marker['bytes'] = invalid
            with self.subTest(bytes=invalid), self.assertRaises(protocol.ProtocolError):
                protocol.validate_request(value)

    def test_unknown_duplicate_numeric_alias_nonfinite_and_traversal_rejected(self):
        value = fixtures.request()
        self.assertIs(protocol.validate_request(value), value)
        changes = [("format", True), ("format", 1.0), ("unknown", 0), ("outputRelativeRoot", "../escape"),
                   ("projectRoot", "C:/root/../escape"), ("targetCompletedEpochs", float("inf"))]
        for key, item in changes:
            with self.subTest(key=key, item=item), self.assertRaises(protocol.ProtocolError):
                protocol.validate_request(dict(value, **{key: item}))
        for key, item in (("trainingOrdinal", False), ("correctedText", "two  spaces"), ("textSha256", "f" * 64), ("endSample", 400000)):
            changed = copy.deepcopy(value)
            changed["selectedClip"][key] = item
            with self.subTest(key=key), self.assertRaises(protocol.ProtocolError):
                protocol.validate_request(changed)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.json"
            for payload in (b'{"a":1,"a":2}', b'{"a":NaN}', b'{"a":1e999}', b'{"a":-0}', b'{"a":true,"b":{"x":1,"x":2}}'):
                path.write_bytes(payload)
                with self.subTest(payload=payload), self.assertRaises(protocol.ProtocolError):
                    protocol.read_json(path)

    def test_parent_cursor_and_selected_clip_cannot_be_substituted(self):
        value = fixtures.request()
        for mutate in (lambda r: r["selectedClip"].update(id="f" * 32),
                       lambda r: r["parentCheckpoint"]["cursor"].update(globalStep=0),
                       lambda r: r["identity"].update(adapterLayoutSha256="0" * 64),
                       lambda r: r.update(parentCheckpoint=None)):
            changed = copy.deepcopy(value)
            mutate(changed)
            with self.assertRaises(protocol.ProtocolError):
                protocol.validate_request(changed)

    def test_oversized_request_and_case_alias_inventory_fail_closed(self):
        value = fixtures.request()
        changed = copy.deepcopy(value)
        changed["dependencySites"] = ["C:/" + "x" * 270000]
        with self.assertRaises(protocol.ProtocolError):
            protocol.validate_request(changed)
        changed = copy.deepcopy(value)
        entry = changed["sourceFiles"].pop("src/fixture_1.py")
        changed["sourceFiles"]["SRC/FIXTURE_0.PY"] = entry
        with self.assertRaises(protocol.ProtocolError):
            protocol.validate_request(changed)

    def test_hardlink_and_directory_junction_rejected_and_read_pin_blocks_replace(self):
        import os
        import subprocess
        import owned_files
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "original"
            source.write_bytes(b"immutable")
            with owned_files.open_file(source) as stream:
                if os.name == "nt":
                    with self.assertRaises(OSError):
                        source.write_bytes(b"changed")
                self.assertEqual(stream.read(), b"immutable")
            linked = root / "linked"
            os.link(source, linked)
            with self.assertRaises(protocol.ProtocolError):
                protocol.read_bytes(source, 30)
            linked.unlink()
            target = root / "target"
            target.mkdir()
            (target / "data").write_bytes(b"plain")
            alias = root / "alias"
            if os.name == "nt":
                result = subprocess.run(["cmd.exe", "/d", "/c", "mklink", "/J", str(alias), str(target)], capture_output=True, timeout=10)
                self.assertEqual(result.returncode, 0)
            else:
                alias.symlink_to(target, target_is_directory=True)
            try:
                with self.assertRaises(protocol.ProtocolError):
                    protocol.read_bytes(alias / "data", 30)
            finally:
                os.rmdir(alias) if os.name == "nt" else alias.unlink()

    def test_golden_result_and_checkpoint_transition_cannot_claim_other_work(self):
        value = fixtures.request()
        request_sha = fixtures.sha(fixtures.encoded(value))
        result = protocol.read_json(fixtures.GOLDEN / "result.json")
        meta = protocol.read_json(fixtures.GOLDEN / "checkpoint-manifest.json")
        protocol.validate_result(value, request_sha, result)
        protocol.validate_manifest(meta, value, request_sha)
        for mutate in (lambda r: r["checkpoint"]["cursor"].update(globalStep=3),
                       lambda r: r["checkpoint"].update(id="e" * 32),
                       lambda r: r.update(format=True),
                       lambda r: r.update(stopped=0)):
            changed = copy.deepcopy(result)
            mutate(changed)
            with self.assertRaises(protocol.ProtocolError):
                protocol.validate_result(value, request_sha, changed)

    def test_manifest_unknown_fields_and_integer_aliases_are_rejected(self):
        value = fixtures.request()
        meta = fixtures.manifest(value, "e" * 64, fixtures.payloads(value))
        for mutate in (lambda m: m.update(extra=1), lambda m: m["afterCursor"].update(completedEpochs=True),
                       lambda m: m["files"]["scheduler.pth"].update(bytes=1453.0),
                       lambda m: m["metrics"].update(loss=float("nan"))):
            changed = copy.deepcopy(meta)
            mutate(changed)
            with self.assertRaises(protocol.ProtocolError):
                protocol.validate_manifest(changed, value, "e" * 64)
