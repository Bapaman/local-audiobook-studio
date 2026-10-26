"""Offline contract tests: never import the optional ML runtime."""
import array
import copy
import importlib
import contextlib
import io
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
import uuid
from unittest import mock

SOURCE = pathlib.Path(__file__).resolve().parents[2] / "src" / "TtsCorpusWorker"
sys.path.insert(0, str(SOURCE))
ACTION = "4" * 32
EXECUTION = "5" * 32
WINDOW = "6" * 32
RESUMED = "8" * 32
CLIP = "69314b00a4ac5cfaaae2b22660942123"
CLIP2 = "e84a50b2572f5e999005c6e63f69d4d4"
OUTPUT = f"tts-corpus/actions/{ACTION}/executions/{EXECUTION}/windows/{WINDOW}"


class WorkerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.temp.name)
        # Remove only this test's exact owned sandbox even when a descendant
        # exceeds MAX_PATH; keep logical root unprefixed for IPC assertions.
        if os.name == "nt":
            self.temp.name = "\\\\?\\" + self.temp.name
        self.addCleanup(self.temp.cleanup)

    def module(self, name):
        # Missing implementation is an explicit RED assertion, not a discovery error.
        self.assertTrue((SOURCE / (name + ".py")).is_file(), "worker implementation missing: " + name)
        return importlib.import_module(name)

    def request(self):
        return dict(schemaVersion=1, requestId="1" * 32, projectId="2" * 32, corpusId="3" * 32,
                    actionId=ACTION, executionId=EXECUTION, windowId=WINDOW, projectRoot=str(self.root),
                    outputRelativeRoot=OUTPUT,
                    sourceRelativePath="source.wav", sourceRecordingId="7" * 32,
                    sourceSha256="a" * 64, sourceSampleCount=48000, runtimeFingerprint="b" * 64,
                    preparedPlan=None, nextClipIndex=0, requestedClipIds=[],
                    dependencySites=[str(self.root / "dependencies")], modelRoot=str(self.root / "model"),
                    modelFiles=[dict(filename="config.json", bytes=2, sha256="c" * 64)])

    def test_request_rejects_escape_identity_window_and_invalid_hash(self):
        p = self.module("protocol")
        p.validate_request(self.request())
        for key, value in (("outputRelativeRoot", OUTPUT.replace(ACTION, "9" * 32)),
                           ("sourceRelativePath", "../source.wav"), ("sourceRelativePath", "C:/source.wav"),
                           ("sourceSha256", "A" * 64), ("sourceSampleCount", True),
                           ("requestId", "request1"), ("corpusId", "A" * 32),
                           ("sourceRecordingId", "clip0000"),
                           ("executionId", "../x"), ("requestedClipIds", ["x"] * 9)):
            with self.subTest(key=key, value=value), self.assertRaises(p.ProtocolError):
                p.validate_request(dict(self.request(), **{key: value}))

    def test_paths_reject_symlink_and_windows_aliases(self):
        p = self.module("protocol")
        for path in ("x/../file", "x\\file", "x//file", "x/file:", "x/NUL", "x/name.", "x/name "):
            with self.subTest(path=path), self.assertRaises(p.ProtocolError):
                p.safe_path(self.root, path)
        outside = self.root / "outside"
        outside.mkdir()
        link = self.root / "link"
        try:
            link.symlink_to(outside, target_is_directory=True)
        except OSError:
            return  # Windows without symlink privilege; aliases above always run.
        with self.assertRaises(p.ProtocolError):
            p.safe_path(self.root, "link/a")

    def test_exclusive_hash_bound_json_refuses_overwrite_and_nonfinite(self):
        p = self.module("protocol")
        path = self.root / "result.json"
        sha = p.write_json(path, {"value": "русский"})
        self.assertEqual(p.read_json(path, sha), {"value": "русский"})
        with self.assertRaises(p.ProtocolError):
            p.read_json(path, "0" * 64)
        with self.assertRaises(FileExistsError):
            p.write_json(path, {})
        with self.assertRaises((ValueError, p.ProtocolError)):
            p.write_json(self.root / "bad.json", {"value": float("nan")})

    def test_plan_preserves_exact_coverage_hard_limit_and_four_second_tail(self):
        prep = self.module("prepare")
        for samples in (1, 63999, 256000, 272001, 500000, 16000 * 91 + 7):
            spans = prep.plan_clips([1.0] * ((samples + 159) // 160), samples)
            self.assertEqual(spans[0][0], 0)
            self.assertEqual(spans[-1][1], samples)
            self.assertTrue(all(0 < b - a <= 256000 for a, b in spans))
            self.assertTrue(all(spans[i][1] == spans[i + 1][0] for i in range(len(spans) - 1)))
            if samples >= 64000:
                self.assertGreaterEqual(spans[-1][1] - spans[-1][0], 64000)

    def test_pause_near_target_wins_over_fixed_boundary(self):
        prep = self.module("prepare")
        rms = [0.1] * 3000
        rms[1300:1320] = [0.0] * 20
        self.assertEqual(prep.plan_clips(rms, 480000)[0], (0, 209600))

    def test_wav_validation_and_exact_pcm_slice(self):
        prep = self.module("prepare")
        p = self.module("protocol")
        samples = array.array("h", [100, -200, 300, -400])
        path = self.root / "clip.wav"
        prep.write_wav(path, samples.tobytes(), 16000)
        self.assertEqual(prep.read_wav(path, 16000, 4), samples.tobytes())
        with self.assertRaises(p.ProtocolError):
            prep.read_wav(path, 48000, 4)
        with self.assertRaises(p.ProtocolError):
            prep.read_wav(path, 16000, 5)

    def test_prepared_pcm_clips_are_exact_slices_without_processing(self):
        prep = self.module("prepare")
        self.assertTrue(hasattr(prep, "publish_pcm_plan"), "pure PCM publication seam missing")
        req = dict(self.request(), sourceSampleCount=816001)
        output = self.root / req["outputRelativeRoot"]
        output.mkdir(parents=True)
        pcm = (array.array("h", [1234, -4321, 0, 32767, -32768]).tobytes() * 54401)[:544002]
        plan = prep.publish_pcm_plan(req, output, pcm, [0.1] * 1701)
        self.assertEqual(plan["totalSamples"], 272001)
        self.assertEqual([c["id"] for c in plan["clips"]], [CLIP, CLIP2])
        for clip in plan["clips"]:
            self.assertRegex(clip["id"], r"\A[0-9a-f]{32}\Z")
            self.assertEqual(uuid.UUID(hex=clip["id"]).hex, clip["id"])
        self.assertEqual([(c["startSample"], c["endSample"]) for c in plan["clips"]], [(0, 208001), (208001, 272001)])
        for clip in plan["clips"]:
            actual = prep.read_wav(self.root / clip["audioRelativePath"], 16000,
                                   clip["endSample"] - clip["startSample"])
            self.assertEqual(actual, pcm[clip["startSample"] * 2:clip["endSample"] * 2])

    @unittest.skipUnless(os.name == "nt", "Windows extended-length IO")
    def test_preparation_publishes_and_verifies_exact_272_character_clip_and_long_text(self):
        p, prep = self.module("protocol"), self.module("prepare")
        suffix = pathlib.Path(OUTPUT) / "clips" / (CLIP + ".wav")
        padding = 272 - len(str(self.root / suffix)) - 1
        self.assertGreater(padding, 0)
        self.root = self.root / ("r" * padding)
        req = self.request()
        output = self.root / OUTPUT
        output.mkdir(parents=True)
        clip_path = self.root / suffix
        self.assertEqual(len(str(clip_path)), 272)
        pcm = b"\x7b\x00" * 16000
        plan = prep.publish_pcm_plan(req, output, pcm, [0.1] * 100)
        disk = p.verify_plan_files(req, plan)
        self.assertEqual(disk["clips"], plan["clips"])
        self.assertEqual(plan["clips"][0]["audioRelativePath"], OUTPUT + "/clips/" + CLIP + ".wav")
        self.assertEqual(prep.read_wav(clip_path, 16000, 16000), pcm)
        text_relative = OUTPUT + "/texts/" + CLIP + ".json"
        texts = p.safe_path(self.root, OUTPUT + "/texts")
        texts.mkdir()
        payload = dict(schemaVersion=1, clipId=CLIP, draftText="Слово", warningCodes=[], words=[])
        sha = p.write_json(p.safe_path(self.root, text_relative), payload)
        self.assertEqual(p.read_json(self.root / text_relative, sha), payload)
        self.assertGreaterEqual(len(str(self.root / text_relative)), 272)

    @unittest.skipUnless(os.name == "nt", "Windows CLI path aliases")
    def test_long_request_and_gate_plain_extended_aliases_keep_exact_window_binding(self):
        p, worker = self.module("protocol"), self.module("worker")
        self.root = self.root / ("r" * 100)
        for window, request_extended, gate_extended in ((WINDOW, False, True), ("9" * 32, True, False)):
            with self.subTest(request_extended=request_extended, gate_extended=gate_extended):
                relative = OUTPUT.replace(WINDOW, window)
                req = dict(self.request(), windowId=window, outputRelativeRoot=relative)
                output = self.root / relative
                extended = pathlib.Path("\\\\?\\" + str(output))
                extended.mkdir(parents=True)
                (extended / "START").write_bytes(b"START\n")
                (extended.parents[1] / "STOP").touch(exist_ok=True)
                sha = p.write_json(extended / "request.json", req)
                request_path = (extended if request_extended else output) / "request.json"
                gate_path = (extended if gate_extended else output) / "START"
                self.assertGreaterEqual(len(str(output / "request.json")), 272)
                stdout, stderr = io.StringIO(), io.StringIO()
                with mock.patch.object(sys, "argv", ["worker.py", "--request", str(request_path), "--request-sha256", sha, "--gate", str(gate_path)]), \
                        contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
                    status = worker.main()
                self.assertEqual(status, 0, stderr.getvalue())
                self.assertEqual(stdout.getvalue().splitlines(), ["tts_corpus.worker.gated", "tts_corpus.worker.ready"])
                result = p.read_json(output / "result.json", (extended / "READY").read_text().strip())
                self.assertTrue(result["stopped"])
                self.assertEqual(result["windowId"], window)
                self.assertEqual(req["projectRoot"], str(self.root))

    @unittest.skipUnless(os.name == "nt", "Windows extended guard aliases")
    def test_extended_filesystem_root_does_not_relax_relative_escape_or_reparse_guards(self):
        p = self.module("protocol")
        extended = pathlib.Path("\\\\?\\" + str(self.root))
        for relative in ("../outside", "clips/../outside", "clips\\x", "clips/NUL", "clips/file:"):
            with self.subTest(relative=relative), self.assertRaises(p.ProtocolError):
                p.safe_path(extended, relative)
        # Exercise the existing Win32 reparse attribute guard without requiring
        # symlink privileges or creating any external/native process.
        stat = type("ReparseStat", (), {"st_mode": 0o040755, "st_file_attributes": 0x400})()
        with mock.patch.object(pathlib.Path, "lstat", return_value=stat), self.assertRaises(p.ProtocolError):
            p.safe_path(extended, "clips/file.wav")
        for path in ("relative/file", "C:relative", r"\\server\share\file", r"\\.\C:\file", r"\\?\GLOBALROOT\file", str(self.root / ".." / "outside")):
            with self.subTest(path=path), self.assertRaises(p.ProtocolError):
                p.io_path(path)

    @unittest.skipUnless(os.name == "nt", "Windows CLI binding guards")
    def test_extended_aliases_do_not_admit_foreign_request_or_gate_window(self):
        p, worker = self.module("protocol"), self.module("worker")
        req = self.request()
        output = self.root / OUTPUT
        output.mkdir(parents=True)
        foreign = self.root / OUTPUT.replace(WINDOW, "9" * 32)
        foreign.mkdir(parents=True)
        sha = p.write_json(output / "request.json", req)
        p.write_json(foreign / "request.json", req)
        (output / "START").write_bytes(b"START\n")
        (foreign / "START").write_bytes(b"START\n")
        (output.parents[1] / "STOP").touch()
        for request_path, gate_path in ((foreign / "request.json", output / "START"), (output / "request.json", foreign / "START")):
            with self.subTest(request=request_path.name, gate=gate_path.parent.name), \
                    mock.patch.object(sys, "argv", ["worker.py", "--request", "\\\\?\\" + str(request_path), "--request-sha256", sha, "--gate", str(gate_path)]), \
                    contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(worker.main(), 1)
                self.assertFalse((output / "READY").exists())

    def test_materialized_clip_uuid_is_stable_across_windows_and_distinct_per_action(self):
        prep = self.module("prepare")
        observed = []
        for action, window in ((ACTION, WINDOW), (ACTION, "9" * 32), ("a" * 32, WINDOW)):
            req = dict(self.request(), actionId=action, windowId=window,
                       outputRelativeRoot=f"tts-corpus/actions/{action}/executions/{EXECUTION}/windows/{window}")
            output = self.root / req["outputRelativeRoot"]
            output.mkdir(parents=True)
            plan = prep.publish_pcm_plan(req, output, b"\0\0" * 16000, [0.1] * 100)
            observed.append(plan["clips"][0]["id"])
        self.assertEqual(observed[:2], [CLIP, CLIP])
        self.assertNotEqual(observed[0], observed[2])

    def test_asr_clips_actual_words_preserves_null_end_and_flags_sparse(self):
        asr = self.module("asr")
        text, words, warnings = asr.normalize_output({"text": "  Привет  ", "chunks": [
            {"text": " Привет", "timestamp": (0.2, None)}]}, 14)
        self.assertEqual(text, "Привет")
        self.assertEqual(words, [{"text": "Привет", "start": 0.2, "end": None}])
        self.assertIn("tts_corpus.asr.final_word_end_missing", warnings)
        self.assertIn("tts_corpus.asr.sparse_transcript", warnings)
        _, words, _ = asr.normalize_output({"text": "x", "chunks": [
            {"text": "x", "timestamp": (-0.1, 20)}]}, 2)
        self.assertEqual(words, [{"text": "x", "start": 0.0, "end": 2.0}])
        with self.assertRaises(self.module("protocol").ProtocolError):
            asr.normalize_output({"text": "x", "chunks": [{"text": "x", "timestamp": (float("nan"), 1)}]}, 2)

    def test_prefix_acceptance_and_empty_stop(self):
        p = self.module("protocol")
        request = {"requestedClipIds": ["c1", "c2", "c3"]}
        p.validate_prefix(request, ["c1", "c2"], True)
        p.validate_prefix(request, [], True)
        p.validate_prefix(request, ["c1", "c2", "c3"], False)
        for ids, stopped in (([], False), (["c2"], True), (["c1"], False), (["c1", "c1"], True)):
            with self.subTest(ids=ids, stopped=stopped), self.assertRaises(p.ProtocolError):
                p.validate_prefix(request, ids, stopped)

    def test_prepared_plan_rejects_gaps_wrong_namespace_and_clip_hash(self):
        p = self.module("protocol")
        req = self.request()
        relative = req["outputRelativeRoot"]
        clip = dict(id=CLIP, index=0, startSample=0, endSample=16000,
                    audioRelativePath=relative + "/clips/" + CLIP + ".wav", audioSha256="d" * 64)
        plan = dict(planRelativePath=relative + "/plan.json", sha256="e" * 64,
                    sourceSha256=req["sourceSha256"], runtimeFingerprint=req["runtimeFingerprint"],
                    totalSamples=16000, clips=[clip])
        p.validate_plan(req, plan)
        for changed in (dict(clip, startSample=1), dict(clip, audioSha256="bad"),
                        dict(clip, audioRelativePath=relative.replace(ACTION, "9" * 32) + "/clips/" + CLIP + ".wav"),
                        dict(clip, id="clip0000"), dict(clip, id=CLIP.upper())):
            with self.assertRaises(p.ProtocolError):
                p.validate_plan(req, dict(plan, clips=[changed]))
        # A resume may point to the exact earlier execution in this same action.
        p.validate_request(dict(req, executionId=RESUMED, outputRelativeRoot=
                                OUTPUT.replace(EXECUTION, RESUMED), preparedPlan=plan,
                                requestedClipIds=[CLIP]))
        with self.assertRaises(p.ProtocolError):
            p.validate_request(dict(req, preparedPlan=plan, requestedClipIds=["wrong"]))

    def test_result_binding_and_payload_hashes_reject_tampering(self):
        p = self.module("protocol")
        req = self.request()
        result = p.result_envelope(req, "f" * 64, None, [], True)
        p.validate_result(req, "f" * 64, result)
        for key, value in (("executionId", "other"), ("windowId", "other"), ("requestSha256", "0" * 64),
                           ("runtimeFingerprint", "0" * 64), ("stopped", False)):
            with self.subTest(key=key), self.assertRaises(p.ProtocolError):
                p.validate_result(req, "f" * 64, dict(result, **{key: value}))

    def test_stop_is_checked_between_clips_and_never_swallows_failure(self):
        worker = self.module("worker")
        stop = self.root / "STOP"
        def transcribe(clip):
            stop.touch()
            return clip + "-done"
        clips, stopped = worker.run_window(["c1", "c2"], stop, transcribe)
        self.assertEqual((clips, stopped), (["c1-done"], True))
        self.assertEqual(worker.run_window(["c1"], stop, transcribe), ([], True))
        stop.unlink()
        def fail(clip):
            raise RuntimeError("failure")
        with self.assertRaises(RuntimeError):
            worker.run_window(["c1"], stop, fail)

    def test_bootstrap_waits_without_importing_dependency_site(self):
        p = self.module("protocol")
        self.module("worker")
        request = self.request()
        output = self.root / request["outputRelativeRoot"]
        output.mkdir(parents=True)
        gate = output / "START"
        gate.touch()
        request_path = output / "request.json"
        sha = p.write_json(request_path, request)
        child = subprocess.Popen([sys.executable, "-I", "-S", "-u", str(SOURCE / "worker.py"),
                                  "--request", str(request_path), "--request-sha256", sha, "--gate", str(gate)],
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            # Gated process has validated request and can wait without installed ML.
            self.assertEqual(child.stdout.readline().strip(), b"tts_corpus.worker.gated")
            self.assertIsNone(child.poll())
            (output.parents[1] / "STOP").touch()
            gate.write_bytes(b"START\n")
            out, err = child.communicate(timeout=10)
            self.assertEqual(child.returncode, 0, err.decode())
            self.assertEqual(err, b"")
            result = p.read_json(output / "result.json", (output / "READY").read_text().strip())
            self.assertTrue(result["stopped"])
            self.assertEqual(result["clips"], [])
            self.assertIsNone(result["preparedPlan"])
            self.assertEqual(result["executionId"], EXECUTION)
        finally:
            if child.poll() is None:
                child.kill()
                child.communicate()

    def prepared(self):
        p = self.module("protocol")
        prep = self.module("prepare")
        req = self.request()
        output = self.root / req["outputRelativeRoot"]
        (output / "clips").mkdir(parents=True)
        pcm = b"\0\0" * 16000
        audio_sha = prep.write_wav(output / ("clips/" + CLIP + ".wav"), pcm)
        clean_sha = prep.write_wav(output / "clean.wav", pcm)
        clip = dict(id=CLIP, index=0, startSample=0, endSample=16000,
                    audioRelativePath=req["outputRelativeRoot"] + "/clips/" + CLIP + ".wav", audioSha256=audio_sha)
        disk = dict(schemaVersion=1, actionId=ACTION, sourceSha256=req["sourceSha256"],
                    runtimeFingerprint=req["runtimeFingerprint"], totalSamples=16000, clips=[clip],
                    cleanAudioRelativePath=req["outputRelativeRoot"] + "/clean.wav", cleanAudioSha256=clean_sha)
        plan_sha = p.write_json(output / "plan.json", disk)
        plan = dict(planRelativePath=req["outputRelativeRoot"] + "/plan.json", sha256=plan_sha,
                    sourceSha256=req["sourceSha256"], runtimeFingerprint=req["runtimeFingerprint"],
                    totalSamples=16000, clips=[clip])
        return req, plan, output

    def test_plan_and_accepted_text_files_are_verified_against_exact_hashes(self):
        p = self.module("protocol")
        req, plan, output = self.prepared()
        p.validate_result(req, "f" * 64, p.result_envelope(req, "f" * 64, plan, [], False))
        resumed = dict(req, executionId=RESUMED, outputRelativeRoot=
                       OUTPUT.replace(EXECUTION, RESUMED), preparedPlan=plan,
                       requestedClipIds=[CLIP])
        (self.root / resumed["outputRelativeRoot"] / "texts").mkdir(parents=True)
        payload = dict(schemaVersion=1, clipId=CLIP, draftText="Слово", warningCodes=[],
                       words=[dict(text="Слово", start=0.0, end=1.0)])
        text_path = resumed["outputRelativeRoot"] + "/texts/" + CLIP + ".json"
        text_sha = p.write_json(self.root / text_path, payload)
        accepted = dict(plan=plan["clips"][0], textRelativePath=text_path, textSha256=text_sha,
                        draftText=payload["draftText"], warningCodes=[], words=payload["words"])
        result = p.result_envelope(resumed, "f" * 64, None, [accepted], False)
        p.validate_result(resumed, "f" * 64, result)
        with self.assertRaises(p.ProtocolError):
            p.validate_result(resumed, "f" * 64, dict(result, clips=[dict(accepted, draftText="Wrong")]))
        (self.root / text_path).write_bytes(b"{}")
        with self.assertRaises(p.ProtocolError):
            p.validate_result(resumed, "f" * 64, result)
        (output / ("clips/" + CLIP + ".wav")).write_bytes(b"tampered")
        with self.assertRaises(p.ProtocolError):
            p.verify_plan_files(req, plan)

    def test_plan_file_rejects_unknown_fields_even_with_rebound_sha(self):
        p = self.module("protocol")
        req, plan, output = self.prepared()
        disk = p.read_json(output / "plan.json")
        disk["untrustedExtra"] = "not part of protocol"
        payload = p.canonical_bytes(disk)
        (output / "plan.json").write_bytes(payload)
        import hashlib
        with self.assertRaises(p.ProtocolError):
            p.verify_plan_files(req, dict(plan, sha256=hashlib.sha256(payload).hexdigest()))

    def test_disk_plan_rejects_numeric_aliases_with_valid_rebound_hash(self):
        p = self.module("protocol")
        req, plan, output = self.prepared()
        original = p.read_json(output / "plan.json")
        for field, value in (("totalSamples", 16000.0), ("index", False), ("index", 0.0),
                             ("startSample", False), ("startSample", 0.0), ("endSample", 16000.0)):
            with self.subTest(field=field, value=value):
                changed = copy.deepcopy(original)
                target = changed if field == "totalSamples" else changed["clips"][0]
                target[field] = value
                (output / "plan.json").write_bytes(p.canonical_bytes(changed))
                changed_plan = dict(plan, sha256=p.sha256_file(output / "plan.json"))
                with self.assertRaises(p.ProtocolError):
                    p.verify_plan_files(req, changed_plan)

    def asr_result(self):
        p = self.module("protocol")
        req, plan, _ = self.prepared()
        req = dict(req, executionId=RESUMED, outputRelativeRoot=OUTPUT.replace(EXECUTION, RESUMED),
                   preparedPlan=plan, requestedClipIds=[CLIP])
        (self.root / req["outputRelativeRoot"] / "texts").mkdir(parents=True)
        payload = dict(schemaVersion=1, clipId=CLIP, draftText="Слово", warningCodes=[],
                       words=[dict(text="Слово", start=0.0, end=1.0)])
        text_path = req["outputRelativeRoot"] + "/texts/" + CLIP + ".json"
        sha = p.write_json(self.root / text_path, payload)
        accepted = dict(plan=copy.deepcopy(plan["clips"][0]), textRelativePath=text_path, textSha256=sha,
                        draftText=payload["draftText"], warningCodes=[], words=copy.deepcopy(payload["words"]))
        return req, p.result_envelope(req, "f" * 64, None, [accepted], False)

    def test_accepted_result_plan_rejects_numeric_aliases(self):
        p = self.module("protocol")
        req, result = self.asr_result()
        p.validate_result(req, "f" * 64, result)
        for field, value in (("index", False), ("index", 0.0), ("startSample", False),
                             ("startSample", 0.0), ("endSample", 16000.0)):
            with self.subTest(field=field, value=value):
                changed = copy.deepcopy(result)
                changed["clips"][0]["plan"][field] = value
                with self.assertRaises(p.ProtocolError):
                    p.validate_result(req, "f" * 64, changed)

    def test_accepted_result_words_reject_boolean_and_representation_aliases(self):
        p = self.module("protocol")
        req, result = self.asr_result()
        for field, value in (("start", False), ("end", True), ("start", 0), ("end", 1)):
            with self.subTest(field=field, value=value):
                changed = copy.deepcopy(result)
                changed["clips"][0]["words"][0][field] = value
                with self.assertRaises(p.ProtocolError):
                    p.validate_result(req, "f" * 64, changed)

    def test_json_duplicate_keys_nonfinite_and_oversized_inputs_fail_closed(self):
        p = self.module("protocol")
        path = self.root / "bad.json"
        for data in (b'{"a":1,"a":2}', b'{"a":NaN}', b'{"a":Infinity}', b'x' * 20):
            path.write_bytes(data)
            with self.assertRaises(p.ProtocolError):
                p.read_json(path, limit=19)

    def test_schema_versions_reject_bool_alias_for_one(self):
        p = self.module("protocol")
        result = p.result_envelope(self.request(), "f" * 64, None, [], True)
        with self.assertRaises(p.ProtocolError):
            p.validate_result(self.request(), "f" * 64, dict(result, schemaVersion=True))
        payload = dict(schemaVersion=True, clipId=CLIP, draftText="", warningCodes=[], words=[])
        with self.assertRaises(p.ProtocolError):
            p.validate_transcript(payload, 1)

    def test_native_library_streams_are_suppressed(self):
        self.module("worker")
        # Exercise actual fd writes, which Python redirect_stdout alone cannot hide.
        code = "import sys,os;sys.path.insert(0,sys.argv[1]);from worker import quiet_libraries\nwith quiet_libraries():\n os.write(1,b'private text');os.write(2,b'private path')\nprint('safe')"
        child = subprocess.run([sys.executable, "-I", "-S", "-c", code, str(SOURCE)],
                               capture_output=True, timeout=10)
        self.assertEqual(child.returncode, 0, child.stderr.decode(errors="replace"))
        self.assertEqual(child.stdout.strip(), b"safe")
        self.assertEqual(child.stderr, b"")

    @classmethod
    def tearDownClass(cls):
        leaked = {"numpy", "torch", "scipy", "transformers"}.intersection(sys.modules)
        if leaked:
            raise AssertionError("ML dependency imported in stdlib suite: " + str(leaked))

    def test_invalid_dependency_after_gate_fails_without_ready_or_diagnostics(self):
        p = self.module("protocol")
        self.module("worker")
        req = self.request()
        output = self.root / req["outputRelativeRoot"]
        output.mkdir(parents=True)
        (output / "START").write_bytes(b"START\n")
        # Only execution-root STOP is authoritative; a window-local file is ignored.
        (output / "STOP").touch()
        sha = p.write_json(output / "request.json", req)
        child = subprocess.run([sys.executable, "-I", "-S", "-u", str(SOURCE / "worker.py"),
                                "--request", str(output / "request.json"), "--request-sha256", sha,
                                "--gate", str(output / "START")], capture_output=True, timeout=10)
        self.assertEqual(child.returncode, 1)
        self.assertEqual(child.stderr.strip(), b"tts_corpus.worker.failed")
        self.assertFalse((output / "READY").exists())
        self.assertFalse((output / "result.json").exists())


if __name__ == "__main__":
    unittest.main()
