import copy
import tempfile
import unittest
from pathlib import Path
import fixtures
import checkpoint
import protocol
import struct
import io
import zipfile


class CheckpointTests(unittest.TestCase):
    def test_checkpoint_budget_archive_storage_bomb_rejected(self):
        value = fixtures.request()
        expected = dict(request=value, requestSha256="e" * 64)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "valid"
            meta = fixtures.write_bundle(root, value)
            checkpoint.preflight_bundle(root, meta, expected)
            bad_root = Path(directory) / "bomb"
            bad = fixtures.write_bundle(bad_root, value, bomb=True)
            with self.assertRaises(protocol.ProtocolError):
                checkpoint.preflight_bundle(bad_root, bad, expected)
            wrong = copy.deepcopy(meta)
            wrong["files"]["optimizer.pth"]["bytes"] = 16 * 1024 * 1024 + 1
            with self.assertRaises(protocol.ProtocolError):
                checkpoint.preflight_bundle(root, wrong, expected)
            (root / "extra.pth").write_bytes(b"unexpected")
            with self.assertRaises(protocol.ProtocolError):
                checkpoint.preflight_bundle(root, meta, expected)

    def test_forged_layout_nonfinite_tensor_and_arbitrary_global_rejected(self):
        value = fixtures.request()
        for name, corrupt in (("lora_weights.safetensors", lambda b: b[:-4] + b"\x00\x00\x80\x7f"),
                              ("optimizer.pth", lambda _: fixtures.archive({"x": 1}, {}, {"evil": b"x"})),
                              ("scheduler.pth", lambda _: fixtures.archive({"bad": 1}, {}))):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory) / "bundle"
                data = fixtures.payloads(value)
                data[name] = corrupt(data[name])
                root.mkdir()
                for key, payload in data.items():
                    (root / key).write_bytes(payload)
                meta = fixtures.manifest(value, "e" * 64, data)
                (root / "manifest.json").write_bytes(fixtures.encoded(meta))
                (root / "READY").write_text(fixtures.sha(fixtures.encoded(meta)) + "\n", encoding="ascii")
                with self.assertRaises(protocol.ProtocolError):
                    checkpoint.preflight_bundle(root, meta, dict(request=value, requestSha256="e" * 64))

    def test_extended_output_paths_round_trip(self):
        import owned_files
        # Python3.11's shutil also needs extended spelling during test cleanup;
        # the product must work without enabling Windows long-path policy.
        with tempfile.TemporaryDirectory(dir=str(owned_files.io_path(tempfile.gettempdir()))) as directory:
            root = Path(directory).joinpath(*(["synthetic-long-path-segment"] * 12))
            owned_files.io_path(root).mkdir(parents=True)
            path = root / "checkpoint.json"
            protocol.write_bytes(path, b"bounded bytes")
            self.assertEqual(protocol.read_bytes(path, 20), b"bounded bytes")
            with self.assertRaises((OSError, protocol.ProtocolError)):
                protocol.write_bytes(path, b"must never replace")
            self.assertEqual(protocol.read_bytes(path, 20), b"bounded bytes")
            value = fixtures.request()
            bundle = owned_files.io_path(root / "bundle")
            meta = fixtures.write_bundle(bundle, value)
            checkpoint.preflight_bundle(bundle, meta, dict(request=value, requestSha256="e" * 64))

    def test_arbitrary_pickle_global_never_executes_and_compressed_archive_rejected(self):
        with self.assertRaises(protocol.ProtocolError):
            checkpoint._symbolic_pickle(b"\x80\x02cos\nsystem\nX\x03\x00\x00\x00bad\x85R.", 65536)
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as z:
            for i in range(6):
                z.writestr("archive/data/" + str(i), bytes(1000))
        with self.assertRaises(protocol.ProtocolError):
            checkpoint._archive(output.getvalue(), 65536)

    def test_scheduler_after_multiple_decays_and_partial_optimizer_keep_real_counters(self):
        # StepLR multiplies the previous LR, not a freshly rounded power.
        rate = 0.0001
        for _ in range(5):
            rate *= 0.99
        payload = fixtures.archive(dict(step_size=100, gamma=0.99, base_lrs=[0.0001], last_epoch=500, _step_count=501,
                                        _is_initial=False, _get_lr_called_within_step=False, _last_lr=[rate]), {})
        checkpoint._scheduler(payload, 500)
        value = fixtures.request()
        state = {0: dict(step=fixtures.Tensor(0, ()), exp_avg=fixtures.Tensor(1, [2, 2]), exp_avg_sq=fixtures.Tensor(2, [2, 2]))}
        group = dict(fixtures.optimizer_settings(), params=[0, 1])
        partial = fixtures.archive(dict(state=state, param_groups=[group]), {"0": struct.pack("<f", 1), "1": bytes(16), "2": bytes(16)})
        checkpoint._optimizer(partial, value, 2)

    def test_pickle_data_descriptor_flags_and_large_memo_are_accepted(self):
        # Real Torch archives have 0x808 flags; pickle memo exceeds2048 slots.
        class Stream(io.BytesIO):
            def seekable(self):
                return False
            def seek(self, *args):
                raise OSError("nonseekable fixture")
        output = Stream()
        with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_STORED) as z:
            for name, payload in {"data.pkl": b"\x80\x02}.", ".format_version": b"1", ".storage_alignment": b"64", "byteorder": b"little", "version": b"3\n", ".data/serialization_id": b"0"}.items():
                info = zipfile.ZipInfo("archive/" + name)
                # Non-ASCII prefix would change the admitted grammar; bit0x800
                # is tested independently below in raw central/local records.
                z.writestr(info, payload)
        data = bytearray(output.getvalue())
        for marker, offset in ((b"PK\x03\x04", 6), (b"PK\x01\x02", 8)):
            start = 0
            while True:
                start = data.find(marker, start)
                if start < 0:
                    break
                flags = struct.unpack_from("<H", data, start + offset)[0]
                struct.pack_into("<H", data, start + offset, flags | 0x800)
                start += 4
        decoded, _ = checkpoint._archive(bytes(data), 65536)
        self.assertEqual(decoded, {})
        memo = b"\x80\x02" + b"".join(b"N" + b"r" + struct.pack("<I", i) for i in range(2100))
        # Put all memorized values into one tuple, producing one final root.
        memo = b"\x80\x02(" + memo[2:] + b"t."
        result, _ = checkpoint._symbolic_pickle(memo, 65536)
        self.assertEqual(len(result), 2100)
