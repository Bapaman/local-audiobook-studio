"""Owned PCM/array fixtures prove persistence, not learned voice quality."""
import hashlib
import json
from pathlib import Path
import sys
import unittest

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointError
from las_rvc.feature_models import publish_feature_preview
from las_rvc.features import load_prepared_clips
from las_rvc.recording_dataset import DecodedRecording, prepare_pcm_dataset
from test_features import PreparedFixture
try:
    from las_rvc.feature_corpus import PreviewRef, CorpusRef, load_persisted_preview, publish_corpus_snapshot
except ImportError:
    PreviewRef = CorpusRef = load_persisted_preview = publish_corpus_snapshot = None


class PersistedCorpusTests(PreparedFixture):
    def preview(self, *, name="preview", ids=None):
        root = self.root / name
        root.mkdir()
        with load_prepared_clips(self.dataset, self.sha, ids or ["clip-000000"]) as selection:
            def extract(clip):
                n16 = clip.audio16.size
                return (np.full(((n16 - 400) // 320 + 1, 768), .5, dtype=np.float32),
                    np.full(n16 // 160 + 1, 220., dtype=np.float32))
            publish_feature_preview(selection, root, extract)
        return PreviewRef(root / "features", hashlib.sha256((root / "features/manifest.json").read_bytes()).hexdigest())

    def test_load_stored_arrays_matches_prepared_pcm_and_keeps_failed_admission(self):
        self.assertIsNotNone(load_persisted_preview, "Persisted feature reader is not implemented")
        ref = self.preview()
        with load_prepared_clips(self.dataset, self.sha, ["clip-000000"]) as selection:
            with load_persisted_preview(ref, selection) as stored:
                self.assertFalse(stored.manifest["training_admitted"])
                self.assertEqual(1, len(stored.clips))
                clip = stored.clips[0]
                self.assertEqual("clip-000000", clip.id)
                self.assertFalse(clip.phone.flags.writeable or clip.f0.flags.writeable)
                np.testing.assert_array_equal(clip.aligned.wave, selection.clips[0].audio40[:clip.aligned.wave.size])

    def test_new_snapshot_records_full_fixture_and_preserves_original_decision(self):
        self.assertIsNotNone(publish_corpus_snapshot, "Corpus persistence is not implemented")
        ref = self.preview()
        output = self.root / "corpus"
        output.mkdir()
        manifest = publish_corpus_snapshot(self.dataset, self.sha, [ref], output)
        self.assertTrue(manifest["full_dataset_extracted"])
        self.assertFalse(manifest["training_admitted"])
        self.assertFalse(manifest["input"]["training_eligible"])
        self.assertEqual(["clip-000000"], manifest["accepted_clip_ids"])
        self.assertEqual([], manifest["pending_clip_ids"])
        raw = (output / "corpus/manifest.json").read_bytes()
        self.assertEqual(hashlib.sha256(raw).hexdigest().encode() + b"\n", (output / "corpus/READY").read_bytes())

    def test_extension_keeps_known_previous_bytes_and_rejects_duplicate_ids(self):
        self.assertIsNotNone(publish_corpus_snapshot, "Corpus persistence is not implemented")
        ref = self.preview()
        first = self.root / "first-corpus"
        first.mkdir()
        publish_corpus_snapshot(self.dataset, self.sha, [ref], first)
        raw = (first / "corpus/manifest.json").read_bytes()
        previous = CorpusRef(first / "corpus", hashlib.sha256(raw).hexdigest())
        second = self.root / "second-corpus"
        second.mkdir()
        # Reopening without new refs proves persisted resume, not regeneration.
        manifest = publish_corpus_snapshot(self.dataset, self.sha, [], second, previous=previous)
        self.assertEqual(["clip-000000"], manifest["accepted_clip_ids"])
        self.assertEqual(raw, (first / "corpus/manifest.json").read_bytes())
        third = self.root / "duplicate-corpus"
        third.mkdir()
        with self.assertRaises(CheckpointError):
            publish_corpus_snapshot(self.dataset, self.sha, [ref], third, previous=previous)
        self.assertFalse((third / "corpus").exists())

    def test_partial_snapshot_resumes_with_distinct_new_clips_without_regeneration(self):
        self.assertIsNotNone(publish_corpus_snapshot, "Corpus persistence is not implemented")
        audio = (0.07 * np.sin(np.arange(300000) * (2 * np.pi * 220 / 40000))).astype(np.float32)
        metadata = {"source_sha256": hashlib.sha256(self.source.read_bytes()).hexdigest(),
            "source_bytes": 32, "consent_confirmed": True, "sample_rate": 40000,
            "decoded_samples": int(audio.size), "pcm_sha256": hashlib.sha256(audio.tobytes()).hexdigest()}
        prepared = self.root / "many-prepared"
        prepared.mkdir()
        manifest = prepare_pcm_dataset(DecodedRecording(audio, metadata), self.source, prepared)
        self.dataset = prepared / "dataset"
        self.sha = hashlib.sha256((self.dataset / "manifest.json").read_bytes()).hexdigest()
        ids = [c["id"] for c in manifest["clips"]]
        self.assertGreater(len(ids), 1)
        first_ref = self.preview(name="first-preview", ids=ids[:1])
        next_ref = self.preview(name="next-preview", ids=ids[1:])
        first = self.root / "partial-corpus"
        first.mkdir()
        partial = publish_corpus_snapshot(self.dataset, self.sha, [first_ref], first)
        self.assertFalse(partial["full_dataset_extracted"])
        self.assertEqual(ids[1:], partial["pending_clip_ids"])
        old = (first / "corpus/manifest.json").read_bytes()
        second = self.root / "extended-corpus"
        second.mkdir()
        completed = publish_corpus_snapshot(self.dataset, self.sha, [next_ref], second,
            previous=CorpusRef(first / "corpus", hashlib.sha256(old).hexdigest()))
        self.assertTrue(completed["full_dataset_extracted"])
        self.assertEqual(ids, completed["accepted_clip_ids"])
        self.assertFalse(completed["training_admitted"])
        self.assertEqual(old, (first / "corpus/manifest.json").read_bytes())

    def test_changed_array_or_known_preview_digest_fails_before_publication(self):
        self.assertIsNotNone(publish_corpus_snapshot, "Corpus persistence is not implemented")
        ref = self.preview()
        output = self.root / "corpus"
        output.mkdir()
        with self.assertRaises(CheckpointError):
            publish_corpus_snapshot(self.dataset, self.sha, [PreviewRef(ref.root, "c" * 64)], output)
        self.assertEqual([], list(output.iterdir()))
        path = ref.root / "clip-000000/phone.npy"
        path.write_bytes(path.read_bytes() + b"changed")
        with self.assertRaises(CheckpointError):
            publish_corpus_snapshot(self.dataset, self.sha, [ref], output)
        self.assertFalse((output / "corpus").exists())

    def test_self_consistent_aligned_wave_change_is_rejected_against_original_pcm(self):
        self.assertIsNotNone(load_persisted_preview, "Persisted feature reader is not implemented")
        ref = self.preview()
        path = ref.root / "clip-000000/wave.npy"
        np.save(path, np.zeros_like(np.load(path, allow_pickle=False)), allow_pickle=False)
        manifest = json.loads((ref.root / "manifest.json").read_bytes())
        changed = path.read_bytes()
        manifest["files"]["clip-000000/wave.npy"].update(bytes=len(changed), sha256=hashlib.sha256(changed).hexdigest())
        raw = json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode()
        (ref.root / "manifest.json").write_bytes(raw)
        sha = hashlib.sha256(raw).hexdigest()
        (ref.root / "READY").write_bytes(sha.encode() + b"\n")
        with load_prepared_clips(self.dataset, self.sha, ["clip-000000"]) as selection:
            with self.assertRaises(CheckpointError):
                with load_persisted_preview(PreviewRef(ref.root, sha), selection):
                    self.fail("Aligned waveform is not bound to prepared audio")

    def test_changed_previous_digest_and_nonempty_output_fail_closed(self):
        self.assertIsNotNone(publish_corpus_snapshot, "Corpus persistence is not implemented")
        ref = self.preview()
        output = self.root / "corpus"
        output.mkdir()
        publish_corpus_snapshot(self.dataset, self.sha, [ref], output)
        other = self.root / "next-corpus"
        other.mkdir()
        with self.assertRaises(CheckpointError):
            publish_corpus_snapshot(self.dataset, self.sha, [], other,
                previous=CorpusRef(output / "corpus", "c" * 64))
        self.assertEqual([], list(other.iterdir()))
        (other / "belongs-to-user.txt").write_bytes(b"preserve")
        with self.assertRaises(CheckpointError):
            publish_corpus_snapshot(self.dataset, self.sha, [ref], other)
        self.assertEqual(b"preserve", (other / "belongs-to-user.txt").read_bytes())


if __name__ == "__main__":
    unittest.main()
