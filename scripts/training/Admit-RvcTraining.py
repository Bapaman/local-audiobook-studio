"""Explicit full-corpus developer admission, no learning or automatic activation."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys

for name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMBA_NUM_THREADS"):
    os.environ[name] = "2"
os.environ["CUDA_VISIBLE_DEVICES"] = ""
os.environ["RVC_CUDA_GRAPH"] = "0"
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ.pop("LIBROSA_CACHE_DIR", None)
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "workers/rvc"))
from las_rvc.checkpoints import CheckpointError
from las_rvc.feature_corpus import CorpusRef
from las_rvc.training_admission import create_experimental_training_candidate, _raw

parser = argparse.ArgumentParser()
parser.add_argument("--dataset-root", type=Path, required=True)
parser.add_argument("--manifest-sha256", required=True)
parser.add_argument("--preflight-root", type=Path, required=True)
parser.add_argument("--preflight-sha256", required=True)
parser.add_argument("--corpus-root", type=Path, required=True)
parser.add_argument("--corpus-sha256", required=True)
parser.add_argument("--source-root", type=Path, required=True)
parser.add_argument("--base-asset-root", type=Path, required=True)
parser.add_argument("--output-root", type=Path, required=True)
parser.add_argument("--experimental", action="store_true")
args = parser.parse_args()
try:
    result = create_experimental_training_candidate(args.dataset_root, args.manifest_sha256,
        CorpusRef(args.preflight_root, args.preflight_sha256), CorpusRef(args.corpus_root, args.corpus_sha256),
        args.source_root, args.base_asset_root, args.output_root, opt_in=args.experimental)
    # Private control result captured by the owning supervisor; not echoed to UI.
    print(json.dumps({"phase": "experimental_training_candidate_saved", "experimental": True,
        "candidate_sha256": hashlib.sha256(_raw(result)).hexdigest(),
        "training_admitted": result["training_admitted"], "training_started": False,
        "native_verified_clips": result["native_validation"]["verified_clips"],
        "train_clips": len(result["split"]["train_clip_ids"]),
        "validation_clips": len(result["split"]["validation_clip_ids"]),
        "boundary_excluded_clips": len(result["split"]["boundary_excluded_clip_ids"]),
        "original_preparation_eligible": result["original_preparation"]["eligible"],
        "warnings": result["warnings"], "quality_established": False, "auto_activate_voice": False}), flush=True)
except CheckpointError as error:
    print(json.dumps({"phase": "failed", "code": error.code}), flush=True)
    sys.exit(1)
except Exception:
    print(json.dumps({"phase": "failed", "code": "training.admission.invalid"}), flush=True)
    sys.exit(1)
