"""Explicit persisted corpus preparation; CPU-only, no training or discovery."""
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
from las_rvc.feature_corpus import CorpusRef, PreviewRef, load_persisted_preview, publish_corpus_snapshot
from las_rvc.features import load_prepared_clips


def status(phase, **fields):
    print(json.dumps({"phase": phase, **fields}), flush=True)


parser = argparse.ArgumentParser()
parser.add_argument("--dataset-root", type=Path, required=True)
parser.add_argument("--manifest-sha256", required=True)
commands = parser.add_subparsers(dest="command", required=True)
append = commands.add_parser("append")
append.add_argument("--shard", nargs=2, action="append", default=[], metavar=("ROOT", "KNOWN_SHA256"))
append.add_argument("--previous", nargs=2, metavar=("ROOT", "KNOWN_SHA256"))
append.add_argument("--output-root", type=Path, required=True)
check = commands.add_parser("validate-stored")
check.add_argument("--shard", nargs=2, required=True, metavar=("ROOT", "KNOWN_SHA256"))
check.add_argument("--clip-id", action="append", required=True)
check.add_argument("--source-root", type=Path, required=True)
args = parser.parse_args()
try:
    if args.command == "append":
        refs = [PreviewRef(Path(root), sha) for root, sha in args.shard]
        previous = None if args.previous is None else CorpusRef(Path(args.previous[0]), args.previous[1])
        result = publish_corpus_snapshot(args.dataset_root, args.manifest_sha256, refs, args.output_root,
            previous=previous)
        status("corpus_saved", accepted_clips=len(result["accepted_clip_ids"]),
            pending_clips=len(result["pending_clip_ids"]), dataset_clips=result["input"]["dataset_clip_count"],
            full_dataset_extracted=result["full_dataset_extracted"], training_admitted=False,
            original_preparation_eligible=result["input"]["training_eligible"])
    else:
        ref = PreviewRef(Path(args.shard[0]), args.shard[1])
        with load_prepared_clips(args.dataset_root, args.manifest_sha256, args.clip_id) as selection:
            with load_persisted_preview(ref, selection) as stored:
                # Import Torch/native kernel only AFTER persisted inputs verify.
                from las_rvc.feature_batch import build_native_feature_batch
                shapes = []
                for prepared, clip in zip(selection.clips, stored.clips):
                    batch = build_native_feature_batch(args.source_root, prepared.audio40, clip.phone, clip.f0)
                    shapes.append({"phone": list(batch.phone.shape), "spec": list(batch.spec.shape),
                        "wave": list(batch.wave.shape), "frames": int(batch.phone_lengths[0])})
                status("stored_native_batches_verified", clips=len(shapes), native_batch_shapes=shapes,
                    models_reextracted=False, training_admitted=False)
except CheckpointError as error:
    status("failed", code=error.code)
    sys.exit(1)
except Exception:
    status("failed", code="features.corpus.invalid")
    sys.exit(1)
