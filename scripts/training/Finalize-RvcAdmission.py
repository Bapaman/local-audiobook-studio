"""Cheap metadata finalization, invoked only after the supervisor observed exit0."""
import argparse
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "workers/rvc"))
from las_rvc.checkpoints import CheckpointError
from las_rvc.feature_corpus import CorpusRef
from las_rvc.training_admission import finalize_experimental_training_admission

parser = argparse.ArgumentParser()
parser.add_argument("--candidate-root", type=Path, required=True)
parser.add_argument("--candidate-sha256", required=True)
parser.add_argument("--output-root", type=Path, required=True)
parser.add_argument("--worker-exit-code", type=int, required=True)
parser.add_argument("--worker-deadline-ms", type=int, required=True)
parser.add_argument("--worker-timed-out", action="store_true")
args = parser.parse_args()
try:
    result = finalize_experimental_training_admission(CorpusRef(args.candidate_root, args.candidate_sha256),
        args.output_root, worker_exit_code=args.worker_exit_code,
        worker_timed_out=args.worker_timed_out, deadline_ms=args.worker_deadline_ms)
    print(json.dumps({"phase": "experimental_training_admission_saved", "training_admitted": True,
        "training_started": False, "native_verified_clips": result["native_validation"]["verified_clips"],
        "train_clips": len(result["split"]["train_clip_ids"]),
        "validation_clips": len(result["split"]["validation_clip_ids"]),
        "boundary_excluded_clips": len(result["split"]["boundary_excluded_clip_ids"]),
        "warnings": result["warnings"], "quality_established": False, "auto_activate_voice": False}), flush=True)
except CheckpointError as error:
    print(json.dumps({"phase": "failed", "code": error.code}), flush=True)
    sys.exit(1)
