"""Source-only guard for the real Vite schema dependency in Docker stages."""

import hashlib
import json
from pathlib import Path, PurePosixPath
import posixpath
import re
import shlex
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCHEMA = ROOT / "contracts/renderer-v1.schema.json"


def normalized(path):
    return PurePosixPath(posixpath.normpath(str(path)))


def schema_copies(dockerfile):
    stages = {}
    stage = None
    workdir = PurePosixPath("/")
    for raw in dockerfile.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.split(maxsplit=1)[0].upper() not in ("FROM", "WORKDIR", "COPY"):
            continue
        tokens = shlex.split(line.removesuffix("\\").strip())
        if tokens[0].upper() == "FROM":
            stage = tokens[-1] if len(tokens) >= 4 and tokens[-2].upper() == "AS" else str(len(stages))
            stages[stage] = {}
            workdir = PurePosixPath("/")
        elif tokens[0].upper() == "WORKDIR":
            path = PurePosixPath(tokens[1])
            workdir = normalized(path if path.is_absolute() else workdir / path)
        elif tokens[0].upper() == "COPY":
            options = [token for token in tokens[1:] if token.startswith("--")]
            paths = [token for token in tokens[1:] if not token.startswith("--")]
            destination = PurePosixPath(paths[-1])
            destination = normalized(destination if destination.is_absolute() else workdir / destination)
            directory_destination = paths[-1].endswith("/") or len(paths) > 2
            source_stage = next((option.split("=", 1)[1] for option in options if option.startswith("--from=")), None)
            for source in paths[:-1]:
                if source_stage:
                    for path, original in stages.get(source_stage, {}).items():
                        source_path = normalized(PurePosixPath("/") / source)
                        if path == source_path:
                            target = destination / path.name if directory_destination else destination
                            stages[stage][normalized(target)] = original
                        elif path.is_relative_to(source_path):
                            stages[stage][normalized(destination / path.relative_to(source_path))] = original
                else:
                    source_path = ROOT / source
                    if source_path == SCHEMA:
                        target = destination / SCHEMA.name if directory_destination else destination
                        stages[stage][normalized(target)] = SCHEMA
                    elif SCHEMA.is_relative_to(source_path):
                        stages[stage][normalized(destination / SCHEMA.relative_to(source_path))] = SCHEMA
    return stages


class PlayerPackagingTests(unittest.TestCase):
    def test_real_vite_dependency_is_copied_into_the_frontend_build_stage(self):
        module = ROOT / "src/frontend/autotable-src/src/renderer/renderer-contract.ts"
        relative = re.search(r"import schema from ['\"]([^'\"]+)['\"]", module.read_text()).group(1)
        docker_module = PurePosixPath("/src/frontend/autotable-src/src/renderer/renderer-contract.ts")
        required = normalized(docker_module.parent / relative)
        copies = schema_copies((ROOT / "Dockerfile").read_text())
        self.assertIn(required, copies["frontend-build"],
                      f"Actual Vite import {relative} resolves to {required}; another Docker stage's schema is not available")
        actual = copies["frontend-build"][required]
        self.assertEqual(hashlib.sha256(SCHEMA.read_bytes()).hexdigest(),
                         hashlib.sha256(actual.read_bytes()).hexdigest())
        self.assertEqual(
            json.loads((ROOT / "contracts/migration-contract.json").read_text())["renderer"]["schema"],
            json.loads(actual.read_text()),
        )


if __name__ == "__main__":
    unittest.main()
