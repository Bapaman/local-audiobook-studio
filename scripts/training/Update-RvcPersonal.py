"""Explicit private request, one actual CPU update/save. No stock train CLI."""
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
from las_rvc.owned_files import pinned_file, plain_path
from las_rvc.personal_update import TrainingInputs, run_personal_window, make_update_control, read_expected_window

parser = argparse.ArgumentParser()
parser.add_argument("--request", type=Path, required=True)
parser.add_argument("--request-sha256", required=True)
parser.add_argument("--output-root", type=Path, required=True)
args = parser.parse_args()


def emit(value):
    print(json.dumps(value, allow_nan=False), flush=True)


try:
    with pinned_file(plain_path(args.request)) as stream:
        raw = stream.read(65537)
        if len(raw) > 65536 or hashlib.sha256(raw).hexdigest() != args.request_sha256:
            raise CheckpointError("training.personal.request.invalid")
        request = json.loads(raw)
        expected_request, start_step, maximum, _ = read_expected_window(args.request, args.request_sha256, args.output_root)
        if request != expected_request:
            raise CheckpointError("training.personal.request.invalid")
        fields = request["inputs"]
        if set(fields) != {"dataset_root", "dataset_sha256", "preflight", "corpus", "candidate", "admission", "source_root", "base_asset_root"}:
            raise CheckpointError("training.personal.request.invalid")
        def ref(value):
            if type(value) is not dict or set(value) != {"root", "sha256"}:
                raise CheckpointError("training.personal.request.invalid")
            return CorpusRef(Path(value["root"]), value["sha256"])
        inputs = TrainingInputs(Path(fields["dataset_root"]), fields["dataset_sha256"],
            ref(fields["preflight"]), ref(fields["corpus"]), ref(fields["candidate"]), ref(fields["admission"]),
            Path(fields["source_root"]), Path(fields["base_asset_root"]))
        previous = None if request["previous"] is None else ref(request["previous"])
        result = run_personal_window(inputs, request["run_id"], args.output_root, previous=previous, max_updates=maximum, emit=emit)
        if result.start_global_step != start_step:
            raise CheckpointError("training.personal.request.invalid")
        emit(make_update_control(result.point, start_global_step=result.start_global_step,
            completed_updates=result.completed_updates))  # Private supervisor control.
except CheckpointError as error:
    emit({"phase": "failed", "code": error.code})
    sys.exit(1)
except Exception:
    emit({"phase": "failed", "code": "training.personal.failed"})
    sys.exit(1)
