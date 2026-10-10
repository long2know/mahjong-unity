"""Source/tool contract tests. All Unity bytes below are explicitly fabricated fixtures."""

import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import unittest
import uuid


ROOT = Path(__file__).resolve().parents[5]
FIXTURES = Path(__file__).resolve().parent / ".test-fixtures"
sys.dont_write_bytecode = True


def load_script(name):
    specification = importlib.util.spec_from_file_location(name, ROOT / "scripts" / f"{name}.py")
    module = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(module)
    return module


validator = load_script("verify-unity-build")
importer = load_script("import-baseline")


class OwnedFixture(unittest.TestCase):
    def setUp(self):
        self.fixture = FIXTURES / uuid.uuid4().hex
        self.fixture.mkdir(parents=True)

    def tearDown(self):
        shutil.rmtree(self.fixture)


class ValidatorTests(OwnedFixture):
    def setUp(self):
        super().setUp()
        self.manifest = {
            "artifactKind": "unity-webgl",
            "contractVersion": "1.0.0",
            "buildId": "FABRICATED-TEST-ONLY-NOT-A-UNITY-BUILD",
            "unityVersion": "6000.3.24f1",
            "unityRevision": "4e7b9b5b6244",
            "compression": "none",
            "threading": "single-threaded",
            "developmentBuild": False,
            "baselineRevision": importer.BASELINE_REVISION,
            "rendererSourceRevision": "1" * 40,
            "rendererSourceTreeSha256": "2" * 64,
            "sourceDirty": False,
            "builtAtUtc": "2026-10-10T00:00:00Z",
            "streamingAssetsPath": None,
        }
        for purpose, suffix in validator.PURPOSE_SUFFIXES.items():
            relative = "Build/FABRICATED_TEST" + suffix
            content = b"\0asm\x01\0\0\0" if purpose == "wasm" else b"FABRICATED TEST ONLY -- NOT UNITY OUTPUT\n"
            path = self.fixture / relative
            path.parent.mkdir(exist_ok=True)
            path.write_bytes(content)
            self.manifest[purpose] = {"path": relative, "bytes": len(content), "sha256": hashlib.sha256(content).hexdigest()}
        self.path = self.fixture / "renderer-build.json"
        self.save()

    def save(self):
        self.path.write_text(json.dumps(self.manifest), encoding="utf-8")

    def run_cli(self, *arguments, flag="0"):
        environment = dict(os.environ, ALLOW_SOURCE_DIRTY=flag, PYTHONDONTWRITEBYTECODE="1")
        return subprocess.run([sys.executable, str(ROOT / "scripts/verify-unity-build.py"), *arguments, str(self.path)],
                              text=True, capture_output=True, env=environment)

    def test_fabricated_fixture_only_proves_structural_integrity_not_unity_authenticity(self):
        report = validator.verify(self.path)
        self.assertEqual(set(report["files"]), {"loader", "framework", "wasm", "data"})
        self.assertEqual(report["artifactTreeFileCount"], 5)
        self.assertIn("separate evidence", report["qualification"])
        self.assertTrue(str(self.path).startswith(str(FIXTURES)))

    def test_each_pin_and_production_flag_is_checked(self):
        mutations = {
            "contractVersion": "2.0.0", "unityVersion": "2019.1.8f1", "unityRevision": "0" * 12,
            "baselineRevision": "0" * 40, "artifactKind": "mock-webgl",
            "compression": "brotli", "threading": "multi-threaded", "developmentBuild": True,
        }
        for field, value in mutations.items():
            with self.subTest(field=field):
                original = self.manifest[field]
                self.manifest[field] = value
                self.save()
                with self.assertRaisesRegex(validator.ValidationFailure, field):
                    validator.verify(self.path, allow_source_dirty=True)
                self.manifest[field] = original
        self.save()

    def test_source_dirty_is_explicit_local_only_and_does_not_bypass_integrity(self):
        self.manifest["sourceDirty"] = True
        self.save()
        rejected = self.run_cli()
        self.assertNotEqual(rejected.returncode, 0)
        self.assertIn("sourceDirty", rejected.stderr)
        accepted = self.run_cli("--allow-source-dirty", "--json")
        self.assertEqual(accepted.returncode, 0, accepted.stderr)
        self.assertTrue(json.loads(accepted.stdout)["sourceDirty"])
        self.assertTrue(json.loads(accepted.stdout)["allowSourceDirty"])
        self.assertEqual(self.run_cli(flag="1").returncode, 0)
        (self.fixture / self.manifest["data"]["path"]).write_bytes(b"CORRUPTED ACTUAL FIXTURE FILE")
        for arguments, flag in ((("--allow-source-dirty",), "0"), ((), "1")):
            result = self.run_cli(*arguments, flag=flag)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("data.bytes", result.stderr)

    def test_allow_dirty_never_allows_development_build_or_wrong_editor(self):
        self.manifest["sourceDirty"] = True
        for field, value in (("developmentBuild", True), ("unityVersion", "6000.3.0f1")):
            original = self.manifest[field]
            self.manifest[field] = value
            self.save()
            result = self.run_cli("--allow-source-dirty", flag="1")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(field, result.stderr)
            self.manifest[field] = original

    def test_invalid_environment_flag_is_not_truthy(self):
        result = self.run_cli(flag="true")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exactly 0 or 1", result.stderr)

    def test_traversal_url_noncanonical_and_wrong_extensions_are_rejected(self):
        original = self.manifest["loader"]["path"]
        for value in ("../outside.loader.js", "/absolute.loader.js", "https://other/Build.loader.js",
                      "Build//double.loader.js", "./Build/FABRICATED_TEST.loader.js",
                      "Build/../FABRICATED_TEST.loader.js", r"Build\bad.loader.js", "Build/loader.js",
                      "Build/TEST.loader.js.br"):
            with self.subTest(path=value):
                self.manifest["loader"]["path"] = value
                self.save()
                with self.assertRaisesRegex(validator.ValidationFailure, "loader"):
                    validator.verify(self.path)
        self.manifest["loader"]["path"] = original

    def test_duplicate_purposes_rejected_before_suffix_check(self):
        self.manifest["framework"] = dict(self.manifest["loader"])
        self.save()
        with self.assertRaisesRegex(validator.ValidationFailure, "duplicate artifact purpose"):
            validator.verify(self.path)

    def test_zero_missing_size_hash_and_wasm_magic_mutate_actual_artifacts(self):
        for purpose in validator.PURPOSE_SUFFIXES:
            with self.subTest(purpose=purpose):
                file = self.fixture / self.manifest[purpose]["path"]
                original = file.read_bytes()
                file.write_bytes(bytes((byte ^ 1 for byte in original)))
                with self.assertRaisesRegex(validator.ValidationFailure, purpose + ".sha256"):
                    validator.verify(self.path)
                file.write_bytes(b"")
                with self.assertRaisesRegex(validator.ValidationFailure, purpose + ".bytes"):
                    validator.verify(self.path)
                file.unlink()
                with self.assertRaisesRegex(validator.ValidationFailure, purpose + ".path"):
                    validator.verify(self.path)
                file.write_bytes(original)
        file = self.fixture / self.manifest["wasm"]["path"]
        file.write_bytes(b"NOT WASM")
        self.manifest["wasm"]["sha256"] = hashlib.sha256(file.read_bytes()).hexdigest()
        self.save()
        with self.assertRaisesRegex(validator.ValidationFailure, "WebAssembly magic"):
            validator.verify(self.path)

    def test_additional_properties_types_null_dates_and_duplicate_json_are_rejected(self):
        for field, value in (("unexpected", True), ("sourceDirty", "false"), ("developmentBuild", 0),
                             ("buildId", ""), ("rendererSourceTreeSha256", "a" * 63),
                             ("builtAtUtc", "2026-10-10"), ("builtAtUtc", "not-a-date")):
            with self.subTest(field=field, value=value):
                original = self.manifest.get(field)
                self.manifest[field] = value
                self.save()
                with self.assertRaises(validator.ValidationFailure):
                    validator.verify(self.path)
                if original is None:
                    del self.manifest[field]
                else:
                    self.manifest[field] = original
        self.path.write_text('{"artifactKind":"unity-webgl","artifactKind":"mock"}', encoding="utf-8")
        with self.assertRaisesRegex(validator.ValidationFailure, "Duplicate JSON property"):
            validator.verify(self.path)

    def test_schema_mutation_is_detected_not_silently_consumed(self):
        isolated = self.fixture / "consumer"
        (isolated / "contracts").mkdir(parents=True)
        for name in ("renderer-v1.schema.json", "migration-contract.json"):
            shutil.copyfile(ROOT / "contracts" / name, isolated / "contracts" / name)
        schema_path = isolated / "contracts/renderer-v1.schema.json"
        schema = json.loads(schema_path.read_text())
        schema["definitions"]["BuildManifest"]["properties"]["unityVersion"]["const"] = "2019.1.8f1"
        schema_path.write_text(json.dumps(schema))
        original = validator.ROOT
        try:
            validator.ROOT = isolated
            with self.assertRaisesRegex(validator.ValidationFailure, "schema drift"):
                validator.frozen_schema()
        finally:
            validator.ROOT = original

    def test_symlink_artifact_parent_manifest_and_unlisted_private_material_are_rejected(self):
        file = self.fixture / self.manifest["data"]["path"]
        original = file.read_bytes()
        outside = self.fixture / "not-artifact"
        outside.write_bytes(original)
        file.unlink()
        file.symlink_to(outside)
        with self.assertRaisesRegex(validator.ValidationFailure, "Symlink"):
            validator.verify(self.path)
        file.unlink()
        file.write_bytes(original)
        (self.fixture / "PRIVATE_TEST.ulf").write_text("FABRICATED TEST ONLY -- NOT A LICENSE")
        with self.assertRaisesRegex(validator.ValidationFailure, "Private/hidden"):
            validator.verify(self.path)

    def test_streaming_directory_must_be_relative_present_and_safe(self):
        self.manifest["streamingAssetsPath"] = "StreamingAssets"
        self.save()
        with self.assertRaisesRegex(validator.ValidationFailure, "streamingAssetsPath"):
            validator.verify(self.path)
        (self.fixture / "StreamingAssets").mkdir()
        self.assertEqual(validator.verify(self.path)["artifactTreeFileCount"], 5)
        self.manifest["streamingAssetsPath"] = "../StreamingAssets"
        self.save()
        with self.assertRaisesRegex(validator.ValidationFailure, "streamingAssetsPath"):
            validator.verify(self.path)

    def test_missing_manifest_reports_both_actual_licensed_build_paths(self):
        self.path.unlink()
        result = self.run_cli()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("missing manifest", result.stderr)
        self.assertIn(validator.REAL_BUILD_COMMAND, result.stderr)
        self.assertIn(validator.CONTAINER_BUILD_COMMAND, result.stderr)


class ImporterTests(OwnedFixture):
    def test_explicit_inventory_keeps_rule_source_tests_and_only_protocol_helper(self):
        retained = [
            "src/backend/src/Mahjong.Autotable.Api/Changsha/Runtime/ChangshaRuntime.cs",
            "src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W10/Bishop/ExampleTests.cs",
            "src/frontend/autotable-src/src/world.ts",
            "src/frontend/autotable-src/tests/node/room-startup-readiness.test.mjs",
            "src/frontend/autotable-src/server/protocol.ts",
            "src/frontend/autotable-src/COPYING",
            "src/frontend/autotable-src/UPSTREAM_SHA",
        ]
        for path in retained:
            self.assertIsNone(importer.exclusion_reason(path, "100644"), path)
        excluded = [
            ".env", ".env.local", ".env.example", ".squad/decisions.md",
            ".github/agents/squad.agent.md", "Phase_K_W10/evidence.json",
            "src/backend/src/Mahjong.Autotable.Api/appsettings.Development.json",
            "src/backend/src/Mahjong.Autotable.Api/bin/Release/code.cs",
            "src/frontend/autotable-src/img/tiles.svg",
            "src/frontend/autotable-src/sound/discard.wav",
            "src/frontend/autotable-src/static/screenshots/table.png",
            "src/frontend/autotable-src/tests/e2e/visual.spec.ts-snapshots/table.png",
            "src/frontend/autotable-src/server/index.ts",
            "src/frontend/autotable/index.html",
            "src/frontend/autotable-src/node_modules/package/index.ts",
        ]
        for path in excluded:
            self.assertIsNotNone(importer.exclusion_reason(path, "100644"), path)
        self.assertIsNotNone(importer.exclusion_reason(retained[0], "120000"))

    def test_path_traversal_and_symlink_collisions_are_rejected(self):
        for path in ("../outside", "/absolute", r"a\b", "C:/outside", "a/../b", "a//b"):
            with self.assertRaises(importer.ImportFailure):
                importer.safe_path(path)
        target = self.fixture / "file"
        target.write_text("pre-existing owner work")
        with self.assertRaisesRegex(importer.ImportFailure, "collision"):
            importer.check_destination(self.fixture, "file")
        (self.fixture / "parent").symlink_to(self.fixture / "actual")
        with self.assertRaisesRegex(importer.ImportFailure, "symlink"):
            importer.check_destination(self.fixture, "parent/file")
        self.assertEqual(target.read_text(), "pre-existing owner work")

    def test_safe_configuration_blanks_secret_fields_but_preserves_sqlite_and_behavior_settings(self):
        fixture = {
            "ConnectionStrings": {"Sqlite": "Data Source=data/mahjong-autotable.db",
                                  "PostgreSql": "FABRICATED-TEST-CONNECTION", "SqlServer": "FABRICATED-TEST-CONNECTION"},
            "Auth": {"JwtSigningKeys": ["FABRICATED-TEST-KEY"]},
            "Authentication": {"Providers": {"Example": {"Enabled": False, "ClientSecret": "FABRICATED-TEST-SECRET"}}},
            "Commentary": {"ApiKey": "env:COMMENTARY_API_KEY"},
            "Persistence": {"Provider": "Sqlite"},
        }
        content, changed = importer.sanitize_config(json.dumps(fixture).encode())
        safe = json.loads(content)
        self.assertEqual(safe["ConnectionStrings"]["Sqlite"], fixture["ConnectionStrings"]["Sqlite"])
        self.assertEqual(safe["Persistence"], fixture["Persistence"])
        self.assertEqual(safe["Auth"]["JwtSigningKeys"], [])
        self.assertEqual(safe["ConnectionStrings"]["PostgreSql"], "")
        self.assertEqual(safe["Authentication"]["Providers"]["Example"]["ClientSecret"], "")
        self.assertEqual(safe["Commentary"]["ApiKey"], "env:COMMENTARY_API_KEY")
        self.assertIn("Auth.JwtSigningKeys", changed)

    def test_git_blob_reader_ignores_working_tree_dirt_without_a_commit_or_checkout(self):
        store = self.fixture / "objects"
        subprocess.run(["git", "init", "--bare", "--quiet", str(store)], check=True)
        original = b"EXACT IMMUTABLE TEST SOURCE\n"
        oid = subprocess.run(["git", "--git-dir", str(store), "hash-object", "-w", "--stdin"],
                             input=original, capture_output=True, check=True).stdout.decode().strip()
        (store / "source.cs").write_bytes(b"DIRTY MIRROR CONTENT MUST NOT BE IMPORTED\n")
        entry = {"path": "src/backend/source.cs", "gitBlob": oid}
        self.assertEqual(importer.read_blobs(store, [entry])[entry["path"]], original)

    def test_private_key_header_documentation_is_not_a_private_key_but_payload_is(self):
        importer.check_private_material("AuthOptions.cs", b"/// PKCS#8 uses <c>-----BEGIN PRIVATE KEY-----</c>")
        payload = b"-----BEGIN PRIVATE KEY-----\n" + b"A" * 96 + b"\n-----END PRIVATE KEY-----"
        with self.assertRaisesRegex(importer.ImportFailure, "Credential-like private material"):
            importer.check_private_material("private-fixture.cs", payload)


if __name__ == "__main__":
    unittest.main()
