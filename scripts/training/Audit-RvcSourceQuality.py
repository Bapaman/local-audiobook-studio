"""Explicit immutable recording audit, CPU2, no native child or ML model."""
import argparse
import json
import os
from pathlib import Path
import sys

for name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS"):
    os.environ[name] = "2"
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "workers/rvc"))
from las_rvc.checkpoints import CheckpointError
from las_rvc.recording_quality import publish_source_quality_audit

parser = argparse.ArgumentParser()
parser.add_argument("--dataset-root", type=Path, required=True)
parser.add_argument("--manifest-sha256", required=True)
parser.add_argument("--output-root", type=Path, required=True)
args = parser.parse_args()
try:
    report = publish_source_quality_audit(args.dataset_root, args.manifest_sha256, args.output_root)
    print(json.dumps({"phase": "source_quality_audit_complete", "clip_count": report["clip_count"],
        "review_clip_count": len(report["review_clip_ids"]), "reason_counts": report["reason_counts"],
        "all_prepared_clips_reproduced_exactly": True, "speech_verified": False,
        "training_admitted": False, "torch_imported": "torch" in sys.modules}), flush=True)
except CheckpointError as error:
    print(json.dumps({"phase": "failed", "code": error.code}), flush=True)
    sys.exit(1)
