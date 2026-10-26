"""Metadata-only acceptance of the native child's private returned point."""
import argparse
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "workers/rvc"))
from las_rvc.checkpoints import CheckpointError
from las_rvc.personal_update import publish_update_acceptance, read_expected_window

parser = argparse.ArgumentParser()
parser.add_argument("--output-root", type=Path, required=True)
parser.add_argument("--request", type=Path, required=True)
parser.add_argument("--request-sha256", required=True)
parser.add_argument("--worker-exit-code", type=int, required=True)
parser.add_argument("--worker-deadline-ms", type=int, required=True)
parser.add_argument("--worker-timed-out", action="store_true")
args = parser.parse_args()
try:
    raw = sys.stdin.buffer.read(16385)
    if len(raw) > 16384:
        raise CheckpointError("training.personal.control.invalid")
    request, start_step, maximum, epoch_size = read_expected_window(args.request, args.request_sha256, args.output_root)
    control = json.loads(raw)
    if (control["run_id"] != request["run_id"] or control["admission_sha256"] != request["inputs"]["admission"]["sha256"]):
        raise CheckpointError("training.personal.control.invalid")
    receipt = publish_update_acceptance(control, args.output_root,
        worker_exit_code=args.worker_exit_code, worker_timed_out=args.worker_timed_out,
        deadline_ms=args.worker_deadline_ms, expected_start_step=start_step, max_updates=maximum,
        request_sha256=args.request_sha256, expected_epoch_size=epoch_size, expected_previous=request["previous"])
    print(json.dumps({"phase": "personal_update_supervisor_accepted",
        "global_step": receipt["point"]["global_step"], "completed_updates": receipt["point"]["completed_updates"], "quality_established": False,
        "auto_activate_voice": False}), flush=True)
except CheckpointError as error:
    print(json.dumps({"phase": "failed", "code": error.code}), flush=True)
    sys.exit(1)
except Exception:
    print(json.dumps({"phase": "failed", "code": "training.personal.control.invalid"}), flush=True)
    sys.exit(1)
