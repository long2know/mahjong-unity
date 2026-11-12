import hashlib
import json
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
FRONTEND = ROOT / "src/frontend/autotable-src"


class WebSourceTests(unittest.TestCase):
    def test_portable_art_and_original_notices_survive_engine_removal(self):
        self.assertFalse((ROOT / "unity").exists())
        self.assertEqual((FRONTEND / "UPSTREAM_SHA").read_text().strip(), "8b81d92aa37997dcfbcc6724d3bd3f694f9cc53a")
        self.assertIn("Paweł Marczewski", (FRONTEND / "COPYING").read_text())
        license_file = FRONTEND / "art/source/FluffyStuff/LICENSE.md"
        self.assertEqual(hashlib.sha256(license_file.read_bytes()).hexdigest(),
                         "17996c5e7de4cbbc71d0e2db73cb63b0590632cc75be9ab2d885edfae5b9f737")
        for name in ("Faces.png", "FacesLabelled.png"):
            self.assertTrue((FRONTEND / "art/tiles" / name).read_bytes().startswith(b"\x89PNG"))
        provenance = json.loads((ROOT / "contracts/source-provenance.json").read_text())
        self.assertEqual(provenance["baselineRevision"], "d10d351e543aa3e341960de9800828b6c0662a82")

    def test_schema_reference_closure_has_no_player_manifest_or_engine_handshake(self):
        schema = json.loads((ROOT / "contracts/renderer-v1.schema.json").read_text())
        definitions = schema["definitions"]
        def walk(value):
            if isinstance(value, dict):
                if "$ref" in value:
                    self.assertIn(value["$ref"].split("/")[-1], definitions)
                for child in value.values():
                    walk(child)
            elif isinstance(value, list):
                for child in value:
                    walk(child)
        walk(schema)
        self.assertNotIn("BuildManifest", definitions)
        self.assertNotIn("Ready", definitions)
        for retained in ("RendererAction", "RendererFrame", "RendererLayout", "FrameRendered", "ActionResult"):
            self.assertIn(retained, definitions)

    def test_canonical_docker_build_and_wrappers_accept_source_not_player_inputs(self):
        docker = (ROOT / "Dockerfile").read_text()
        self.assertEqual(sum(line.startswith("FROM ") for line in docker.splitlines()), 3)
        self.assertIn("npm run typecheck && npm run build", docker)
        self.assertIn("dotnet publish", docker)
        self.assertNotIn("unity", docker.lower())
        self.assertNotIn("artifacts/", docker)
        self.assertIn("USER 1000:1000", docker)
        self.assertIn("Security__CspStrict=true", docker)
        for path in ("scripts/build-unity-webgl.sh", "scripts/verify-unity-build.py", "docker-compose.unity-build.yml"):
            self.assertFalse((ROOT / path).exists())
        self.assertNotIn("SkipUnityBuild", (ROOT / "build.ps1").read_text())

    def test_build_context_keeps_operator_and_local_evidence_files_out(self):
        dockerignore = (ROOT / ".dockerignore").read_text().splitlines()
        for name in ("artifacts/", "**/node_modules/", "**/.env", "**/*.key", "**/*.db", ".squad/"):
            self.assertIn(name, dockerignore)
        self.assertFalse(any(line.startswith("!artifacts") for line in dockerignore))
        paths = ["artifacts/webgl-only/fixture.db", ".work/probe.txt", "data/game.db", "operator.env", "private.key"]
        for path in paths:
            result = subprocess.run(["git", "check-ignore", "--no-index", path], cwd=ROOT, capture_output=True)
            self.assertEqual(result.returncode, 0, path)
        package = json.loads((FRONTEND / "package.json").read_text())
        self.assertEqual(package["name"], "mahjong-web")
        self.assertFalse(any("unity" in name.lower() or "autotable" in name.lower() for name in package["dependencies"]))


if __name__ == "__main__":
    unittest.main()
