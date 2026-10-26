"""Bounded developer extraction; explicit inputs, CPU only, no auto training."""
import argparse
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
from las_rvc.features import load_prepared_clips
from las_rvc.feature_models import create_feature_models, publish_feature_preview


def status(phase, **fields):
    print(json.dumps({"phase": phase, **fields}), flush=True)


parser = argparse.ArgumentParser()
parser.add_argument("--dataset-root", type=Path, required=True)
parser.add_argument("--manifest-sha256", required=True)
parser.add_argument("--source-root", type=Path, required=True)
parser.add_argument("--asset-root", type=Path, required=True)
parser.add_argument("--output-root", type=Path, required=True)
parser.add_argument("--clip-id", action="append", required=True)
parser.add_argument("--verify-native-batches", action="store_true")
args = parser.parse_args()
try:
    with load_prepared_clips(args.dataset_root, args.manifest_sha256, args.clip_id) as selection:
        status("input_verified", selected_clips=len(selection.clips),
            dataset_clips=selection.dataset_clip_count, training_eligible=selection.training_eligible)
        with create_feature_models(args.source_root, args.asset_root) as models:
            status("models_ready", device="cpu", threads=2)
            native_batches = []
            def extract(clip):
                phone, f0 = models.extract(clip)
                if args.verify_native_batches:
                    from las_rvc.feature_batch import build_native_feature_batch
                    batch = build_native_feature_batch(args.source_root, clip.audio40, phone, f0)
                    native_batches.append({"phone": list(batch.phone.shape), "spec": list(batch.spec.shape),
                        "wave": list(batch.wave.shape), "frames": int(batch.phone_lengths[0])})
                return phone, f0
            result = publish_feature_preview(selection, args.output_root, extract)
        status("features_saved", selected_clips=len(result["clips"]),
            full_dataset_extracted=result["full_dataset_extracted"], training_admitted=False,
            native_batches_verified=len(native_batches), native_batch_shapes=native_batches,
            frames=[{key: clip[key] for key in ("hubert_frames", "f0_frames", "aligned_frames",
                "voiced_f0_frames")} for clip in result["clips"]])
except CheckpointError as error:
    status("failed", code=error.code)
    sys.exit(1)
except Exception:
    status("failed", code="features.preview.failed")
    sys.exit(1)
