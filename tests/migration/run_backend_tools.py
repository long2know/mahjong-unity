"""Run the existing backend tool suites with project-local, owned fixtures."""

import importlib.util
import os
from pathlib import Path
import shutil
import sys
import unittest
import uuid


ROOT = Path(__file__).resolve().parents[2]
WORK = ROOT / "artifacts/test-results/migration-work"
sys.dont_write_bytecode = True


def main():
    WORK.mkdir(parents=True, exist_ok=True)
    for key in ("TMPDIR", "TMP", "TEMP"):
        os.environ[key] = str(WORK)
    os.environ["PYTHONDONTWRITEBYTECODE"] = "1"
    fixture = WORK / ("backend-tools-" + uuid.uuid4().hex)
    fixture.mkdir()
    suite = unittest.TestSuite()
    try:
        directory = ROOT / "src/backend/tests/Mahjong.Autotable.Api.Tests/Hosting"
        for file in sorted(directory.glob("test_*.py")):
            spec = importlib.util.spec_from_file_location(file.stem, file)
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            module.FIXTURES = fixture
            suite.addTests(unittest.defaultTestLoader.loadTestsFromModule(module))
        result = unittest.TextTestRunner(verbosity=2).run(suite)
        return 0 if result.wasSuccessful() else 1
    finally:
        shutil.rmtree(fixture)


if __name__ == "__main__":
    sys.exit(main())
