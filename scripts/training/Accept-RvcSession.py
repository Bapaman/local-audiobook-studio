"""Metadata/payload hashes only; accepts the owned child only after exit zero."""
import argparse
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "workers/rvc"))
from las_rvc.checkpoints import CheckpointError
from las_rvc.personal_session import accept_session

parser = argparse.ArgumentParser()
parser.add_argument("--request", type=Path, required=True)
parser.add_argument("--request-sha256", required=True)
parser.add_argument("--output-root", type=Path, required=True)
parser.add_argument("--worker-exit-code", type=int, required=True)
parser.add_argument("--worker-deadline-ms", type=int, required=True)
args = parser.parse_args()
try:
    raw = sys.stdin.buffer.read(16385)
    if len(raw) > 16384: raise CheckpointError("training.session.control.invalid")
    receipt, sha = accept_session(args.request, args.request_sha256, args.output_root, json.loads(raw),
        worker_exit_code=args.worker_exit_code, worker_timed_out=False, deadline_ms=args.worker_deadline_ms)
    # Explicit returned identity for subsequent resume/inference, not directory scanning.
    print(json.dumps({"phase": "personal_session_accepted", "selection_root": str(args.output_root / "selection"),
        "selection_sha256": sha, "global_step": receipt["selected_point"]["global_step"],
        "completed_updates": receipt["control"]["completed_updates"], "stop_reason": receipt["control"]["stop_reason"],
        "quality_established": False, "auto_activate_voice": False}), flush=True)
except CheckpointError as error:
    print(json.dumps({"phase": "failed", "code": error.code}), flush=True)
    sys.exit(1)
except Exception:
    print(json.dumps({"phase": "failed", "code": "training.session.acceptance_failed"}), flush=True)
    sys.exit(1)
