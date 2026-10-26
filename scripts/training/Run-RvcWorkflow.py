"""Explicit local workflow entrypoint; process owner consumes private controls."""
import argparse
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "workers/rvc"))
from las_rvc.local_workflow import WorkflowError, run_workflow
from las_rvc.runtime_inventory import runtime_inventory


def emit(event):
    print(json.dumps(event, allow_nan=False), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check-runtime", action="store_true")
    parser.add_argument("--request", type=Path)
    parser.add_argument("--request-sha256")
    parser.add_argument("--output-root", type=Path)
    args = parser.parse_args()
    if args.check_runtime:
        try:
            raw = sys.stdin.buffer.read(65537)
            if len(raw) > 65536: raise ValueError("bounded configuration")
            emit(runtime_inventory(json.loads(raw)))
            return 0
        except Exception:
            emit({"ready": False, "code": "training.runtime.not_ready"})
            return 1
    if args.request is None or args.request_sha256 is None or args.output_root is None:
        parser.error("request, request-sha256 and output-root are required")
    try:
        emit(run_workflow(args.request, args.request_sha256, args.output_root, emit=emit))
        return 0
    except WorkflowError as error:
        emit({"phase": "failed", "code": error.code})
        return 1


if __name__ == "__main__":
    sys.exit(main())
