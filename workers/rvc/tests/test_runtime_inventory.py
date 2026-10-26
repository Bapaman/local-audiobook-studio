"""Readiness checks are metadata/file checks, never model imports."""
import importlib.metadata
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
try:
    from las_rvc.runtime_inventory import runtime_inventory, REQUIRED_VERSIONS
except ImportError:
    runtime_inventory = None


class RuntimeInventoryTests(unittest.TestCase):
    def test_missing_stage_is_not_a_ready_runtime(self):
        self.assertIsNotNone(runtime_inventory)
        with tempfile.TemporaryDirectory(prefix="las-inventory-") as directory:
            root = Path(directory)
            config = {k: str(root) for k in ("scripts_root", "source_root", "decoder_root", "base_asset_root", "feature_asset_root")}
            config.update(python_path=sys.executable, powershell_path=sys.executable)
            with self.assertRaises((OSError, ValueError)): runtime_inventory(config)

    def test_required_package_version_is_checked_without_importing_models(self):
        self.assertIsNotNone(runtime_inventory)
        from las_rvc.runtime_inventory import check_environment
        previously_imported = "torch" in sys.modules
        with patch.object(importlib.metadata, "version", return_value="incompatible"):
            with self.assertRaises(ValueError): check_environment()
        self.assertEqual(previously_imported, "torch" in sys.modules)

    def test_manifest_constants_are_parsed_not_executed_and_assets_are_required(self):
        self.assertIsNotNone(runtime_inventory)
        from las_rvc.runtime_inventory import literal_constants, add_file
        with tempfile.TemporaryDirectory(prefix="las-inventory-") as directory:
            root = Path(directory)
            module = root / "source.py"
            module.write_text("raise RuntimeError('must not execute')\nASSETS = {'model.bin': (3, '" + "a" * 64 + "')}\n", encoding="utf-8")
            self.assertEqual(3, literal_constants(module, ["ASSETS"])["ASSETS"]["model.bin"][0])
            with self.assertRaises((OSError, ValueError)): add_file({}, root / "model.bin", (3, "a" * 64))


if __name__ == "__main__": unittest.main()
