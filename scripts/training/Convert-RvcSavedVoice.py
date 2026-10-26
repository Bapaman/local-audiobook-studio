"""Explicit owned saved-voice conversion entrypoint; no discovery or publication."""
import argparse
import json
import os
from pathlib import Path
import sys

# Set native library/offline limits before importing NumPy or any model module.
for name in ("OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS", "NUMBA_NUM_THREADS",
        "NUMEXPR_NUM_THREADS", "VECLIB_MAXIMUM_THREADS"):
    os.environ[name] = "2"
for name in ("HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE", "HF_DATASETS_OFFLINE", "TORCH_FORCE_WEIGHTS_ONLY_LOAD"):
    os.environ[name] = "1"
os.environ["CUDA_VISIBLE_DEVICES"] = ""
os.environ.pop("LIBROSA_CACHE_DIR", None)
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "workers/rvc"))
from las_rvc.saved_voice_conversion import ConversionError, run_conversion


def emit(event):
    print(json.dumps(event, allow_nan=False), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", type=Path, required=True)
    parser.add_argument("--request-sha256", required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    try:
        emit(run_conversion(args.request, args.request_sha256, args.output_root, emit=emit))
        return 0
    except ConversionError as error:
        emit({"phase": "failed", "code": error.code})
        return 1


if __name__ == "__main__":
    sys.exit(main())
