"""Cheap known-request/returned-candidate audio acceptance after child exit."""
import argparse
import hashlib
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "workers/rvc"))
from las_rvc.checkpoints import CheckpointError
from las_rvc.owned_files import pinned_file, plain_path
from las_rvc.personal_inference import accept_audio_candidate

parser = argparse.ArgumentParser()
parser.add_argument("--request", type=Path, required=True)
parser.add_argument("--request-sha256", required=True)
parser.add_argument("--output-root", type=Path, required=True)
args = parser.parse_args()
try:
    raw = sys.stdin.buffer.read(16385)
    if len(raw) > 16384: raise CheckpointError("inference.control.invalid")
    with pinned_file(plain_path(args.request)) as stream:
        request_raw = stream.read(65537)
        if len(request_raw) > 65536 or hashlib.sha256(request_raw).hexdigest() != args.request_sha256:
            raise CheckpointError("inference.request.invalid")
        request = json.loads(request_raw)
        if str(plain_path(Path(request["output_root"]))) != str(plain_path(args.output_root)):
            raise CheckpointError("inference.request.invalid")
        comparison = type(request.get("format")) is int and request["format"] == 2
        if comparison:
            if request["purpose"] != "short-personal-base-context-comparison":
                raise CheckpointError("inference.request.invalid")
            from las_rvc.voice_comparison import accept_voice_comparison
            accept_voice_comparison(json.loads(raw), args.output_root, request=request,
                worker_exit_code=0, worker_timed_out=False, deadline_ms=60000)
        else:
            accept_audio_candidate(json.loads(raw), args.output_root, expected_request=request,
                worker_exit_code=0, worker_timed_out=False, deadline_ms=60000)
    print(json.dumps({"phase": "voice_comparison_supervisor_accepted" if comparison else "personal_audio_supervisor_accepted",
        "new_text_synthesis": False, "quality_established": False}), flush=True)
except CheckpointError as error:
    print(json.dumps({"phase": "failed", "code": error.code}), flush=True)
    sys.exit(1)
except Exception:
    print(json.dumps({"phase": "failed", "code": "inference.supervisor.invalid"}), flush=True)
    sys.exit(1)
