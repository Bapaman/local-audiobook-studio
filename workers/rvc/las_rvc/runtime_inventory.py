"""Bounded offline readiness inventory; never import Torch or load model weights."""
import ast
import hashlib
import importlib.metadata
import importlib.util
from pathlib import Path
import struct
import sys

from .local_workflow import RUNTIME_FIELDS, STAGES
from .owned_files import pinned_file, plain_path

REQUIRED_VERSIONS = {"torch": "2.10.0+cpu", "numpy": "1.26.4", "scipy": "1.13.1",
    "librosa": "0.10.2.post1", "transformers": "4.49.0", "huggingface-hub": "0.36.2",
    "numba": "0.68.0", "soundfile": "0.14.0"}
MODULES = "__init__ checkpoints continuation_ledger experimental_intake feature_batch feature_corpus feature_models features local_workflow owned_files persistent_training personal_inference personal_session personal_update recording_dataset recording_quality rvc_kernel runtime_inventory saved_voice_conversion training_admission training_loop training_state update_checkpoints voice_comparison".split()
DECODERS = {"bin/ffmpeg.exe": "0e482b853f2fd4821055d20cdce510f0eea6027037c04b994f454a2136e8868e",
    "bin/ffprobe.exe": "b9627185ffbad1d4fc6ef953868ca9ad8df75fc8dd035fcab58aca78caa2d23e"}


def add_file(inventory, path, expected=None):
    path = plain_path(Path(path))
    with pinned_file(path) as stream:
        info = path.stat()
        if not 0 <= info.st_size <= 512 * 1024 ** 2: raise ValueError("inventory file bound")
        digest = hashlib.sha256()
        while chunk := stream.read(1024 * 1024): digest.update(chunk)
    sha = digest.hexdigest()
    if expected is not None and (info.st_size != expected[0] or sha != expected[1]): raise ValueError("pinned runtime file")
    inventory[str(path)] = {"path": str(path), "bytes": info.st_size, "sha256": sha}


def literal_constants(path, names):
    with pinned_file(path) as stream:
        raw = stream.read(1024 * 1024 + 1)
    if len(raw) > 1024 * 1024: raise ValueError("bounded module")
    result = {}
    for node in ast.parse(raw).body:
        if isinstance(node, ast.Assign):
            for target in node.targets:
                if isinstance(target, ast.Name) and target.id in names:
                    result[target.id] = ast.literal_eval(node.value)
    if set(result) != set(names): raise ValueError("required pinned constants")
    return result


def check_environment():
    if sys.version_info[:2] != (3, 11) or struct.calcsize("P") != 8: raise ValueError("Python 3.11 x64 required")
    for name, required in REQUIRED_VERSIONS.items():
        if importlib.metadata.version(name) != required: raise ValueError("incompatible dependency")


def runtime_inventory(config):
    if type(config) is not dict or set(config) != RUNTIME_FIELDS: raise ValueError("explicit runtime configuration")
    if Path(config["python_path"]).resolve() != Path(sys.executable).resolve(): raise ValueError("configured interpreter")
    check_environment()
    result = {}
    for key in ("python_path", "powershell_path"): add_file(result, config[key])
    scripts = plain_path(Path(config["scripts_root"]))
    for name in sorted(STAGES | {"Run-RvcWorkflow.py", "Convert-RvcSavedVoice.py"}): add_file(result, scripts / name)
    modules = scripts.parents[1] / "workers/rvc/las_rvc"
    for name in MODULES: add_file(result, modules / (name + ".py"))
    kernel = literal_constants(modules / "rvc_kernel.py", ["SOURCE_MANIFEST", "BASE_GENERATOR", "BASE_DISCRIMINATOR"])
    features = literal_constants(modules / "feature_models.py", ["SOURCE_FILES", "ASSET_FILES"])
    for name, expected in {**kernel["SOURCE_MANIFEST"], **features["SOURCE_FILES"]}.items():
        add_file(result, Path(config["source_root"]) / name, expected)
    for name, expected in features["ASSET_FILES"].items(): add_file(result, Path(config["feature_asset_root"]) / name, expected)
    for name, constant in (("f0G40k.pth", "BASE_GENERATOR"), ("f0D40k.pth", "BASE_DISCRIMINATOR")):
        add_file(result, Path(config["base_asset_root"]) / name, kernel[constant])
    for name, sha in DECODERS.items():
        file = Path(config["decoder_root"]) / name
        add_file(result, file, (file.stat().st_size, sha))
    # Detect a removed/replaced installed distribution without importing its code.
    for name in REQUIRED_VERSIONS:
        distribution = importlib.metadata.distribution(name)
        files = distribution.files
        if files is None: raise ValueError("installed metadata")
        metadata = [f for f in files if f.name in ("METADATA", "RECORD") and ".dist-info" in str(f)]
        if len(metadata) != 2: raise ValueError("installed metadata inventory")
        for file in metadata: add_file(result, distribution.locate_file(file))
    for name in ("torch", "numpy", "scipy", "librosa", "transformers", "huggingface_hub", "numba", "soundfile"):
        spec = importlib.util.find_spec(name)
        if spec is None or spec.origin is None: raise ValueError("missing installed module")
        add_file(result, spec.origin)
    if not 50 <= len(result) <= 128: raise ValueError("bounded explicit inventory")
    return {"ready": True, "files": sorted(result.values(), key=lambda f: f["path"])}
