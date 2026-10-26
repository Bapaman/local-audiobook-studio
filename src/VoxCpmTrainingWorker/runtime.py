"""Explicit, pinned offline namespace bootstrap, invoked only after START."""
from contextlib import ExitStack
import os
from pathlib import Path
import sys
import types
from owned_files import pin_directories, open_file
from protocol import require, safe_path

_pins = None


def install_dependencies(request):
    global _pins
    require(_pins is None and not any(name == "voxcpm" or name.startswith("voxcpm.") for name in sys.modules))
    pins = ExitStack()
    try:
        for site in request["dependencySites"]:
            pins.enter_context(pin_directories(site))
        pins.enter_context(pin_directories(request["sourceRoot"]))
        pins.enter_context(pin_directories(request["modelRoot"]))
        # Parent already verified full hashes and retains the explicit runtime
        # inventory. Child pins exact source/model files through process exit.
        for key, root in (("sourceFiles", request["sourceRoot"]), ("modelFiles", request["modelRoot"])):
            for name, entry in request[key].items():
                stream = pins.enter_context(open_file(safe_path(root, name)))
                require(os.fstat(stream.fileno()).st_size == entry["bytes"])
        sys.dont_write_bytecode = True
        os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_DATASETS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1",
                          CUDA_VISIBLE_DEVICES="", OMP_NUM_THREADS="2", MKL_NUM_THREADS="2", OPENBLAS_NUM_THREADS="2",
                          NUMEXPR_NUM_THREADS="2", NUMBA_NUM_THREADS="2", TOKENIZERS_PARALLELISM="false")
        # append, never addsitedir: .pth files are not executable configuration.
        sys.path.extend(request["dependencySites"])
        root = Path(request["sourceRoot"]) / "src" / "voxcpm"
        for name, path in (("voxcpm", root), ("voxcpm.model", root / "model"), ("voxcpm.training", root / "training")):
            module = types.ModuleType(name)
            module.__path__ = [str(path)]
            module.__package__ = name
            sys.modules[name] = module
        _pins = pins
    except BaseException:
        pins.close()
        raise
