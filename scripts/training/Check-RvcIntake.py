"""Metadata-only explicit developer preflight; never starts training."""
import argparse
import json
import os
from pathlib import Path
import sys

for key in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS"):
    os.environ[key] = "2"
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "workers/rvc"))
from las_rvc.checkpoints import CheckpointError
from las_rvc.experimental_intake import create_experimental_source_preflight

parser = argparse.ArgumentParser()
parser.add_argument("--dataset-root", type=Path, required=True)
parser.add_argument("--manifest-sha256", required=True)
parser.add_argument("--output-root", type=Path, required=True)
parser.add_argument("--clip-id", action="append", required=True)
parser.add_argument("--experimental", action="store_true")
args = parser.parse_args()
try:
    result = create_experimental_source_preflight(args.dataset_root, args.manifest_sha256,
        args.clip_id, args.output_root, opt_in=args.experimental)
    print(json.dumps({"phase": "source_preflight_saved", "experimental": True,
        "source_preflight_passed": result["source_preflight_passed"], "training_admitted": False,
        "original_preparation_eligible": result["original_preparation"]["eligible"],
        "hard_reasons": result["hard_reasons"], "warnings": result["warnings"],
        "metrics": result["metrics"]}), flush=True)
except CheckpointError as error:
    print(json.dumps({"phase": "failed", "code": error.code}), flush=True)
    sys.exit(1)
