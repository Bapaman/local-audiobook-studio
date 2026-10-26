"""Explicit developer entry point; never discovers audio, binaries or old runtimes."""
import argparse
import json
import os
from pathlib import Path
import sys

os.environ["OMP_NUM_THREADS"] = "2"
os.environ["OPENBLAS_NUM_THREADS"] = "2"
os.environ["MKL_NUM_THREADS"] = "2"
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "workers/rvc"))
from las_rvc.checkpoints import CheckpointError
from las_rvc.recording_dataset import decode_recording, prepare_pcm_dataset

parser = argparse.ArgumentParser()
parser.add_argument("--source", type=Path, required=True)
parser.add_argument("--source-sha256", required=True)
parser.add_argument("--decoder-root", type=Path, required=True)
parser.add_argument("--output-root", type=Path, required=True)
parser.add_argument("--consent", action="store_true")
args = parser.parse_args()
try:
    recording = decode_recording(source_path=args.source, source_sha256=args.source_sha256,
        ffmpeg_path=args.decoder_root / "bin/ffmpeg.exe",
        ffmpeg_sha256="0e482b853f2fd4821055d20cdce510f0eea6027037c04b994f454a2136e8868e",
        ffprobe_path=args.decoder_root / "bin/ffprobe.exe",
        ffprobe_sha256="b9627185ffbad1d4fc6ef953868ca9ad8df75fc8dd035fcab58aca78caa2d23e",
        consent=args.consent)
    result = prepare_pcm_dataset(recording, args.source, args.output_root)
    print(json.dumps({"phase": "prepared", "duration_seconds": result["duration_seconds"],
        "usable_seconds": result["usable_seconds"], "clips": len(result["clips"]),
        "eligible": result["eligible"], "reasons": result["reasons"]}))
except CheckpointError as error:
    print(json.dumps({"phase": "failed", "code": error.code}))
    sys.exit(1)
