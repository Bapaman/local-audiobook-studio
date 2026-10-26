"""Known selected personal checkpoint -> held-out short reconstruction, not TTS."""
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
from las_rvc.owned_files import plain_path, pinned_file
from las_rvc.personal_update import TrainingInputs
from las_rvc.personal_inference import run_personal_inference

parser = argparse.ArgumentParser()
parser.add_argument("--request", type=Path, required=True)
parser.add_argument("--request-sha256", required=True)
parser.add_argument("--output-root", type=Path, required=True)
args = parser.parse_args()

def emit(value): print(json.dumps(value, allow_nan=False), flush=True)

try:
    with pinned_file(plain_path(args.request)) as stream:
        raw = stream.read(65537)
        if len(raw) > 65536 or hashlib.sha256(raw).hexdigest() != args.request_sha256:
            raise CheckpointError("inference.request.invalid")
        request = json.loads(raw)
        comparison = type(request.get("format")) is int and request["format"] == 2
        expected_keys = {"format", "purpose", "inputs", "ledger", "clip_id", "output_root"}
        if comparison: expected_keys |= {"feature_source_root", "feature_asset_root"}
        if (set(request) != expected_keys
                or type(request["format"]) is not int or request["format"] != (2 if comparison else 1)
                or request["purpose"] != ("short-personal-base-context-comparison" if comparison else "short-personal-saved-generator-reconstruction")
                or str(plain_path(Path(request["output_root"]))) != str(plain_path(args.output_root))):
            raise CheckpointError("inference.request.invalid")
        fields = request["inputs"]
        if set(fields) != {"dataset_root", "dataset_sha256", "preflight", "corpus", "candidate", "admission", "source_root", "base_asset_root"}:
            raise CheckpointError("inference.request.invalid")
        def ref(value):
            if type(value) is not dict or set(value) != {"root", "sha256"}: raise CheckpointError("inference.request.invalid")
            return CorpusRef(Path(value["root"]), value["sha256"])
        inputs = TrainingInputs(Path(fields["dataset_root"]), fields["dataset_sha256"], ref(fields["preflight"]), ref(fields["corpus"]),
            ref(fields["candidate"]), ref(fields["admission"]), Path(fields["source_root"]), Path(fields["base_asset_root"]))
        if comparison:
            from las_rvc.voice_comparison import run_voice_comparison
            candidate = run_voice_comparison(inputs, ref(request["ledger"]), request["clip_id"],
                Path(request["feature_source_root"]), Path(request["feature_asset_root"]), args.output_root, emit=emit)
        else:
            candidate = run_personal_inference(inputs, ref(request["ledger"]), request["clip_id"], args.output_root, emit=emit)
        emit({"phase": "voice_comparison_control" if comparison else "personal_audio_control",
            "candidate_root": str(candidate.root), "manifest_sha256": candidate.sha256})
except CheckpointError as error:
    emit({"phase": "failed", "code": error.code})
    sys.exit(1)
except Exception:
    emit({"phase": "failed", "code": "inference.personal.failed"})
    sys.exit(1)
