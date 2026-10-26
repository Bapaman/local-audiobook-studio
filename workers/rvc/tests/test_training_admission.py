"""Pure metadata is not native/voice proof; no personal files or GPU."""
from pathlib import Path
import sys
import tempfile
import unittest
import hashlib
import json
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointError
from las_rvc.feature_corpus import CorpusRef
try:
    from las_rvc.training_admission import make_training_split, create_experimental_training_candidate
    from las_rvc.training_admission import finalize_experimental_training_admission
except ImportError:
    make_training_split = create_experimental_training_candidate = finalize_experimental_training_admission = None


class TrainingAdmissionTests(unittest.TestCase):
    @staticmethod
    def manifest():
        return {"source": {"decoded_samples": 15600000}, "clips": [
            {"id": f"clip-{i:06d}", "source_start_sample": i * 136000,
                "source_end_sample": min(i * 136000 + 148000, 15600000)} for i in range(114)]}

    def test_time_holdout_never_shares_source_samples_and_records_boundary_clips(self):
        self.assertIsNotNone(make_training_split, "Training split is not implemented")
        manifest = self.manifest()
        split = make_training_split(manifest, opt_in=True)
        by_id = {c["id"]: c for c in manifest["clips"]}
        train = [by_id[i] for i in split["train_clip_ids"]]
        validation = [by_id[i] for i in split["validation_clip_ids"]]
        self.assertGreaterEqual(len(train), 30)
        self.assertGreaterEqual(len(validation), 3)
        self.assertLessEqual(max(c["source_end_sample"] for c in train), min(c["source_start_sample"] for c in validation))
        self.assertGreater(len(split["boundary_excluded_clip_ids"]), 0)
        union = set(split["train_clip_ids"] + split["validation_clip_ids"] + split["boundary_excluded_clip_ids"])
        self.assertEqual({c["id"] for c in manifest["clips"]}, union)
        expected_union = (len(train) - 1) * 136000 + 148000
        self.assertEqual(expected_union, split["train_unique_context_samples"])
        self.assertEqual("unique-source-intervals-including-context-not-speech", split["coverage_method"])

    def test_missing_opt_in_or_insufficient_holdout_cannot_admit_training(self):
        self.assertIsNotNone(make_training_split, "Training split is not implemented")
        with self.assertRaises(CheckpointError):
            make_training_split(self.manifest(), opt_in=False)
        short = self.manifest()
        short["clips"] = short["clips"][:35]
        with self.assertRaises(CheckpointError):
            make_training_split(short, opt_in=True)

    def test_malformed_source_intervals_and_duplicate_ids_fail_closed(self):
        self.assertIsNotNone(make_training_split, "Training split is not implemented")
        for mutation in ("reversed", "duplicate"):
            manifest = self.manifest()
            if mutation == "reversed":
                manifest["clips"][0]["source_end_sample"] = 0
            else:
                manifest["clips"][1]["id"] = manifest["clips"][0]["id"]
            with self.subTest(mutation=mutation), self.assertRaises(CheckpointError):
                make_training_split(manifest, opt_in=True)

    def test_missing_verified_inputs_fail_without_publication_or_native_import(self):
        self.assertIsNotNone(create_experimental_training_candidate, "Training candidate is not implemented")
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-feature-tests"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="admission-", dir=parent) as temporary:
            root = Path(temporary)
            output = root / "output"
            output.mkdir()
            with patch("las_rvc.feature_batch.build_native_feature_batch") as native:
                with self.assertRaises(CheckpointError):
                    create_experimental_training_candidate(root / "missing-dataset", "a" * 64,
                        CorpusRef(root / "missing-preflight", "b" * 64),
                        CorpusRef(root / "missing-corpus", "c" * 64), root / "source", root / "assets",
                        output, opt_in=True)
                native.assert_not_called()
            self.assertEqual([], list(output.iterdir()))

    def test_published_candidate_cannot_admit_after_timeout_or_nonzero_exit(self):
        self.assertIsNotNone(finalize_experimental_training_admission, "Supervisor acceptance is not implemented")
        parent = Path(__file__).resolve().parents[3] / ".artifacts/rvc-feature-tests"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="candidate-boundary-", dir=parent) as temporary:
            root = Path(temporary)
            candidate = root / "candidate"
            candidate.mkdir()
            # Synthetic completed publication only, not evidence of native ML.
            decision = {"format": 1, "purpose": "experimental-native-rvc-training-candidate",
                "training_admitted": False, "experimental_opt_in": True, "auto_activate_voice": False,
                "quality_established": False, "speech_duration_verified": False,
                "native_validation": {"verified_clips": 1, "clips": [{"id": "synthetic"}]}}
            raw = json.dumps(decision, sort_keys=True, separators=(",", ":")).encode()
            digest = hashlib.sha256(raw).hexdigest()
            from las_rvc.feature_models import _write_bytes
            _write_bytes(candidate / "decision.json", raw)
            _write_bytes(candidate / "READY", digest.encode() + b"\n")
            for exit_code, timed_out in ((0, True), (1, False), (None, False), (False, False)):
                output = root / f"output-{exit_code}-{timed_out}"
                output.mkdir()
                with self.subTest(exit_code=exit_code, timed_out=timed_out), self.assertRaises(CheckpointError):
                    finalize_experimental_training_admission(CorpusRef(candidate, digest), output,
                        worker_exit_code=exit_code, worker_timed_out=timed_out, deadline_ms=60000)
                self.assertEqual([], list(output.iterdir()))
            self.assertEqual(raw, (candidate / "decision.json").read_bytes())
            wrong = root / "wrong-identity"
            wrong.mkdir()
            with self.assertRaises(CheckpointError):
                finalize_experimental_training_admission(CorpusRef(candidate, "d" * 64), wrong,
                    worker_exit_code=0, worker_timed_out=False, deadline_ms=60000)
            self.assertEqual([], list(wrong.iterdir()))
            accepted = root / "successful-supervisor"
            accepted.mkdir()
            result = finalize_experimental_training_admission(CorpusRef(candidate, digest), accepted,
                worker_exit_code=0, worker_timed_out=False, deadline_ms=60000)
            self.assertTrue(result["training_admitted"])
            self.assertTrue(result["supervisor_acceptance"]["accepted_after_native_worker_exit"])
            self.assertEqual(digest, result["supervisor_acceptance"]["candidate_sha256"])
            self.assertFalse(json.loads((candidate / "decision.json").read_bytes())["training_admitted"])
            self.assertEqual({"decision.json", "READY"}, {p.name for p in (accepted / "admission").iterdir()})


if __name__ == "__main__":
    unittest.main()
