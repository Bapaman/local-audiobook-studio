"""One explicitly approved persistent CPU session, no discovery or downloads."""
import argparse
import os
from pathlib import Path
import sys
import json

for name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMBA_NUM_THREADS"):
    os.environ[name] = "2"
os.environ["CUDA_VISIBLE_DEVICES"] = ""
os.environ["RVC_CUDA_GRAPH"] = "0"
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ.pop("LIBROSA_CACHE_DIR", None)
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "workers/rvc"))
from las_rvc.checkpoints import CheckpointError
from las_rvc.personal_session import read_session_request
from las_rvc.persistent_training import run_personal_session

parser = argparse.ArgumentParser()
parser.add_argument("--request", type=Path, required=True)
parser.add_argument("--request-sha256", required=True)
parser.add_argument("--output-root", type=Path, required=True)
args = parser.parse_args()


def emit(event):
    print(json.dumps(event, allow_nan=False), flush=True)


try:
    request, start, ids, _ = read_session_request(args.request, args.request_sha256, args.output_root)
    control = run_personal_session(request, args.request_sha256, emit=emit)
    if control["global_step"] != start + control["completed_updates"]:
        raise CheckpointError("training.session.cursor.invalid")
    emit(control)
except CheckpointError as error:
    emit({"phase": "failed", "code": error.code})
    sys.exit(1)
except Exception:
    emit({"phase": "failed", "code": "training.session.failed"})
    sys.exit(1)
