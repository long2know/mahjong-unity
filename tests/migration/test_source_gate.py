"""Source-only migration checks; no fixture is a Unity build or a license."""

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import unittest
import uuid


ROOT = Path(__file__).resolve().parents[2]
WORK = ROOT / "artifacts/test-results/migration-work"
PIN = "d10d351e543aa3e341960de9800828b6c0662a82"
IMPORT_CONTRACT_SHA256 = "4e373e7b195e8c8c71fdedae9b87d051b2a8d073abf612a0744b8ce91ea8a38e"
IMPORT_SCHEMA_SHA256 = "87150bc61c7c4555d2f143baf9cc02fb78380b83c39052fbb9a0478c648dd6a7"
CONTRACT_SHA256 = "ff514d0b21214308c6688fd9db483c3125ae7c7314e952b1d2c4f50243314719"
SCHEMA_SHA256 = "1e7a71fa2b34ce050e96c567449ae7e8e8eff9f0a75fdd5ba5afeb3569534d0f"
sys.dont_write_bytecode = True


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


importer = load_module("migration_importer", ROOT / "scripts/import-baseline.py")
source_metadata = load_module(
    "migration_renderer_metadata",
    ROOT / "unity/MahjongRenderer/Tools/source_metadata.py",
)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def ignore_results(paths):
    result = subprocess.run(
        ["git", "check-ignore", "--no-index", "--stdin", "-z", "-v", "--non-matching"],
        input=b"".join(path.encode() + b"\0" for path in paths),
        cwd=ROOT,
        capture_output=True,
        check=False,
    )
    if result.returncode not in (0, 1):
        raise AssertionError("Actual git check-ignore failed: " + result.stderr.decode())
    fields = result.stdout.decode().split("\0")[:-1]
    if len(fields) != len(paths) * 4:
        raise AssertionError("Actual git check-ignore omitted a requested path")
    return {
        fields[index + 3]: fields[index + 2]
        for index in range(0, len(fields), 4)
    }


class ImportSourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.contract = json.loads((ROOT / "contracts/migration-contract.json").read_text())
        cls.provenance = json.loads((ROOT / "contracts/source-provenance.json").read_text())
        reference = next(
            item["localPath"] for item in cls.contract["sourcePins"]
            if item["id"] == "behavior-baseline"
        )
        cls.reference = Path(os.environ.get("MIGRATION_BASELINE_GIT_STORE", reference))
        cls.tree = importer.inspect_tree(cls.reference) if cls.reference.is_dir() else None

    def test_frozen_contract_and_all_published_schema_copies_are_identical(self):
        schema = json.loads((ROOT / "contracts/renderer-v1.schema.json").read_text())
        embedded = json.loads(
            (ROOT / "unity/MahjongRenderer/Assets/Resources/renderer-v1.schema.json").read_text()
        )
        self.assertEqual(CONTRACT_SHA256, sha256(ROOT / "contracts/migration-contract.json"))
        self.assertEqual(SCHEMA_SHA256, sha256(ROOT / "contracts/renderer-v1.schema.json"))
        self.assertEqual(schema, embedded)
        self.assertEqual(schema, self.contract["renderer"]["schema"])
        self.assertEqual(IMPORT_CONTRACT_SHA256, self.provenance["contractSha256"])
        self.assertEqual(IMPORT_SCHEMA_SHA256, self.provenance["rendererSchemaSha256"])
        extension = self.provenance["rendererContractExtensions"][-1]
        self.assertEqual(IMPORT_CONTRACT_SHA256, extension["parentContractSha256"])
        self.assertEqual(IMPORT_SCHEMA_SHA256, extension["parentRendererSchemaSha256"])
        self.assertEqual(CONTRACT_SHA256, extension["contractSha256"])
        self.assertEqual(SCHEMA_SHA256, extension["rendererSchemaSha256"])

    def test_inventory_matches_actual_immutable_tree_and_allowlist_not_just_counts(self):
        if self.tree is None:
            self.skipTest("Actual pinned baseline store unavailable; set MIGRATION_BASELINE_GIT_STORE")
        selected = [
            entry for entry in self.tree
            if importer.exclusion_reason(entry["path"], entry["mode"]) is None
        ]
        excluded = [
            entry for entry in self.tree
            if importer.exclusion_reason(entry["path"], entry["mode"]) is not None
        ]
        inventory = self.provenance["inventory"]
        self.assertEqual(PIN, self.provenance["baselineRevision"])
        self.assertEqual(1443, len(selected))
        self.assertEqual(1824, len(excluded))
        self.assertEqual(1443, inventory["selectedCount"])
        self.assertEqual(1824, inventory["excludedCount"])
        for entries, receipt in (
            (selected, inventory["selected"]),
            (excluded, inventory["excluded"]),
        ):
            self.assertEqual(
                {(entry["path"], entry["gitBlob"]) for entry in entries},
                {(entry["path"], entry["gitBlob"]) for entry in receipt},
            )
        paths = [entry["path"] for entry in selected]
        self.assertEqual(len(paths), len({path.casefold() for path in paths}))
        self.assertTrue(all(entry["mode"] in ("100644", "100755") for entry in selected))
        for path in paths:
            importer.safe_path(path)
            self.assertFalse(any(
                part == ".env" or part.startswith(".env.")
                or part in importer.FORBIDDEN_PARTS
                for part in Path(path).parts
            ), path)
        self.assertIn("src/frontend/autotable-src/COPYING", paths)
        self.assertIn("src/frontend/autotable-src/UPSTREAM_SHA", paths)
        self.assertFalse(self.provenance["frontendOwnershipTransfer"]["backendWritesAfterReceipt"])

    def test_actual_baseline_backend_cs_and_retained_notice_blobs_are_preserved(self):
        if self.tree is None:
            self.skipTest("Actual pinned baseline store unavailable; set MIGRATION_BASELINE_GIT_STORE")
        entries = [
            entry for entry in self.provenance["inventory"]["selected"]
            if entry["path"].startswith("src/backend/") and entry["path"].endswith(".cs")
            or entry["path"] in (
                "src/frontend/autotable-src/COPYING",
                "src/frontend/autotable-src/UPSTREAM_SHA",
            )
        ]
        self.assertEqual(1038, sum(entry["path"].endswith(".cs") for entry in entries))
        blobs = importer.read_blobs(self.reference, entries)
        changed = set()
        for entry in entries:
            path = entry["path"]
            self.assertEqual(
                entry["sourceSha256"], hashlib.sha256(blobs[path]).hexdigest(), path,
            )
            if (ROOT / path).read_bytes() != blobs[path]:
                changed.add(path)
        self.assertEqual({
            "src/backend/src/Mahjong.Autotable.Api/Program.cs",
            "src/backend/src/Mahjong.Autotable.Api/Observability/SecurityHeadersMiddleware.cs",
        }, changed)

    def test_paths_and_secret_file_names_fail_before_blob_reads(self):
        for path in (
            "../outside.cs", "/absolute.cs", "src/../outside.cs",
            r"src\outside.cs", "src//file.cs", "./file.cs", "src/file:stream.cs",
            "src/file\0.cs",
        ):
            with self.subTest(path=path), self.assertRaises(importer.ImportFailure):
                importer.safe_path(path)
        for name in (
            ".env", ".env.production", "appsettings.Development.json",
            "private.ulf", "private.pem", "private.pfx", "private.p12", "private.key",
        ):
            path = "src/backend/src/Mahjong.Autotable.Api/" + name
            self.assertIsNotNone(importer.exclusion_reason(path, "100644"), path)
        self.assertIsNotNone(importer.exclusion_reason("src/backend/source.cs", "120000"))


class IgnoreSourceTests(unittest.TestCase):
    def test_actual_gitignore_preserves_every_real_renderer_source_and_nested_project(self):
        source = source_metadata.source_files(ROOT) + [
            "contracts/migration-contract.json",
            "contracts/renderer-v1.schema.json",
            "contracts/source-provenance.json",
            "src/frontend/autotable-src/package-lock.json",
            "src/backend/src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj",
            "tests/migration/test_source_gate.py",
            ".env.example",
        ]
        patterns = ignore_results(source)
        self.assertIn("unity/MahjongRenderer/Tools/CSharpSyntax/CSharpSyntax.csproj", source)
        self.assertTrue(any(path.endswith(".meta") for path in source))
        self.assertTrue(any(path.endswith(".unity") for path in source))
        for path, pattern in patterns.items():
            self.assertTrue(not pattern or pattern.startswith("!"), (path, pattern))

    def test_actual_gitignore_excludes_generated_private_and_dependency_paths(self):
        generated = [
            "unity/MahjongRenderer/Library/generated",
            "unity/MahjongRenderer/Temp/generated",
            "unity/MahjongRenderer/BuildCache/tool-work/generated",
            "unity/MahjongRenderer/Logs/editor.log",
            "unity/MahjongRenderer/UserSettings/EditorUserSettings.asset",
            "unity/MahjongRenderer/Assembly-CSharp.csproj",
            "unity/MahjongRenderer/MahjongRenderer.slnx",
            "unity/MahjongRenderer/Assets/Resources/renderer-build-identity.json",
            "src/backend/src/Mahjong.Autotable.Api/bin/Debug/output.dll",
            "src/backend/src/Mahjong.Autotable.Api/obj/project.assets.json",
            "src/frontend/autotable-src/node_modules/package/index.js",
            "artifacts/unity-webgl/renderer-build.json",
            "private/Unity_lic.ulf", "private/key.pfx", "private/key.pem",
            ".env", ".env.production", "src/backend/.env.local",
            "src/backend/src/Mahjong.Autotable.Api/appsettings.Development.json",
        ]
        for path, pattern in ignore_results(generated).items():
            self.assertTrue(pattern and not pattern.startswith("!"), (path, pattern))


class OwnedFixture(unittest.TestCase):
    def setUp(self):
        self.fixture = WORK / uuid.uuid4().hex
        self.fixture.mkdir(parents=True)

    def tearDown(self):
        shutil.rmtree(self.fixture)


class ArtifactAndContextTests(OwnedFixture):
    def test_destination_parent_symlinks_and_existing_owner_files_are_not_overwritten(self):
        destination = self.fixture / "destination"
        outside = self.fixture / "outside"
        destination.mkdir()
        outside.mkdir()
        (destination / "linked").symlink_to(outside, target_is_directory=True)
        with self.assertRaisesRegex(importer.ImportFailure, "symlink"):
            importer.check_destination(destination, "linked/source.cs")
        file = destination / "owner.cs"
        file.write_text("SOURCE OWNER COLLISION PROBE\n")
        with self.assertRaisesRegex(importer.ImportFailure, "refusing to overwrite"):
            importer.check_destination(destination, "owner.cs")
        self.assertEqual("SOURCE OWNER COLLISION PROBE\n", file.read_text())
        self.assertFalse((outside / "source.cs").exists())

    def test_real_docker_context_filter_keeps_outputs_notices_and_excludes_private_names(self):
        if not shutil.which("docker"):
            self.skipTest("Docker is unavailable; actual context selection is blocked")
        context = self.fixture / "context"
        context.mkdir()
        shutil.copyfile(ROOT / ".dockerignore", context / ".dockerignore")
        (context / "Dockerfile").write_text("FROM scratch\nCOPY . /filtered-context/\n")
        included = {
            "THIRD_PARTY_NOTICES.txt",
            "contracts/renderer-v1.schema.json",
            "contracts/migration-contract.json",
            "contracts/source-provenance.json",
            "src/frontend/autotable-src/COPYING",
            "src/frontend/autotable-src/UPSTREAM_SHA",
            "src/frontend/autotable-src/package-lock.json",
            "src/backend/src/Mahjong.Autotable.Api/Program.cs",
            "artifacts/unity-webgl/renderer-build.json",
            "artifacts/unity-webgl/Build/CONTEXT_NAME_ONLY.loader.js",
            "artifacts/unity-webgl/Build/CONTEXT_NAME_ONLY.framework.js",
            "artifacts/unity-webgl/Build/CONTEXT_NAME_ONLY.wasm",
            "artifacts/unity-webgl/Build/CONTEXT_NAME_ONLY.data",
        }
        excluded = {
            ".env", ".env.production", "private/Unity_lic.ulf", "private/key.pfx",
            "artifacts/unity-webgl/.env", "artifacts/unity-webgl/key.pem",
            "artifacts/unity-webgl/Unity_lic.ulf",
            "artifacts/unity-webgl/Build/editor.log",
            "artifacts/unity-webgl/Build/editor.tmp",
            "artifacts/test-results/probe.json",
            "artifacts/api/probe.dll",
            "artifacts/tool-work/probe",
            "artifacts/cache/probe",
            "artifacts/unity-builder-home/probe",
            "artifacts/logs/probe.log",
            "artifacts/other/probe",
            "src/backend/src/Mahjong.Autotable.Api/appsettings.Development.json",
            "src/backend/src/Mahjong.Autotable.Api/bin/probe.dll",
            "src/frontend/autotable-src/node_modules/probe.js",
            "unity/MahjongRenderer/Library/probe.asset",
            ".squad/decisions.md",
        }
        for relative in included | excluded:
            path = context / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            # Empty filename probes are not manifests, wasm/data, credentials or licenses.
            path.touch()
        exported = self.fixture / "exported"
        environment = dict(os.environ, TMPDIR=str(WORK), TMP=str(WORK), TEMP=str(WORK))
        result = subprocess.run(
            ["docker", "build", "--network=none", "--output", f"type=local,dest={exported}", str(context)],
            capture_output=True, text=True, env=environment, timeout=120,
        )
        self.assertEqual(0, result.returncode, result.stderr[-3000:])
        files = {
            path.relative_to(exported / "filtered-context").as_posix()
            for path in (exported / "filtered-context").rglob("*") if path.is_file()
        }
        self.assertTrue(included <= files, "Required context paths omitted: " + str(included - files))
        self.assertFalse(excluded & files, "Private/generated paths included: " + str(excluded & files))

    def test_real_builder_blocks_absent_private_inputs_without_docker_or_activation(self):
        environment = dict(os.environ, TMPDIR=str(WORK), TMP=str(WORK), TEMP=str(WORK))
        for key in (
            "UNITY_LICENSE_FILE", "UNITY_LICENSE_DIR", "UNITY_LICENSE_MOUNT_SOURCE",
            "UNITY_LICENSE_MOUNT_TARGET", "ALLOW_SOURCE_DIRTY",
        ):
            environment.pop(key, None)
        result = subprocess.run(
            ["bash", "scripts/build-unity-docker.sh", "--test-and-build"],
            cwd=ROOT, env=environment, capture_output=True, text=True, timeout=10,
        )
        self.assertEqual(1, result.returncode)
        self.assertIn("Docker supplies build tools, not a Unity license", result.stderr)
        self.assertIn("no unsupported manual activation is attempted", result.stderr)
        self.assertEqual("", result.stdout)


if __name__ == "__main__":
    unittest.main()
