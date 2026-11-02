"""Registry handoff contracts. Synthetic test bytes are NOT qualified Unity output."""

import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import shutil
import subprocess
import tarfile
import unittest
from unittest.mock import patch
import uuid


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("ghcr_publish", ROOT / "scripts/ghcr_publish.py")
publish = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publish)
SHA = "a" * 40
DIGEST = "sha256:" + "b" * 64


class PublishingTests(unittest.TestCase):
    def setUp(self):
        self.work = ROOT / ".work" / ("ghcr-test-" + uuid.uuid4().hex)
        self.work.mkdir(parents=True)
        self.addCleanup(shutil.rmtree, self.work)

    def archive(self, entries):
        path = self.work / (uuid.uuid4().hex + ".tar.gz")
        with tarfile.open(path, "w:gz") as archive:
            for name, kind in entries:
                member = tarfile.TarInfo(name)
                member.type = kind
                member.linkname = "../../escape"
                member.size = 4 if kind == tarfile.REGTYPE else 0
                archive.addfile(member, io.BytesIO(b"data") if member.isfile() else None)
        return path

    def test_hash_pinned_regular_bundle_extracts_only_below_artifact_root(self):
        archive = self.archive([("artifacts/unity-webgl/Build/player.wasm", tarfile.REGTYPE)])
        destination = self.work / "output"
        self.assertEqual(publish.extract(archive, destination, publish.sha256(archive)), {"Build/player.wasm"})
        self.assertEqual((destination / "Build/player.wasm").read_bytes(), b"data")
        with self.assertRaisesRegex(ValueError, "SHA256 mismatch"):
            publish.extract(archive, self.work / "changed", "0" * 64)
        self.assertFalse((self.work / "changed").exists())

    def test_traversal_links_duplicates_special_files_and_unexpected_roots_are_rejected(self):
        prefix = "artifacts/unity-webgl/"
        cases = [
            [("../escape", tarfile.REGTYPE)],
            [("/absolute", tarfile.REGTYPE)],
            [(prefix + "../escape", tarfile.REGTYPE)],
            [(prefix + "./file", tarfile.REGTYPE)],
            [(prefix + "file", tarfile.SYMTYPE)],
            [(prefix + "file", tarfile.LNKTYPE)],
            [(prefix + "file", tarfile.FIFOTYPE)],
            [(prefix + "file", tarfile.DIRTYPE)],
            [(prefix + "file", tarfile.REGTYPE), (prefix + "FILE", tarfile.REGTYPE)],
            [("scripts/verify-unity-build.py", tarfile.REGTYPE)],
        ]
        for entries in cases:
            with self.subTest(entries=entries):
                archive = self.archive(entries)
                destination = self.work / ("output-" + uuid.uuid4().hex)
                with self.assertRaises(ValueError):
                    publish.extract(archive, destination, publish.sha256(archive))
                self.assertFalse(destination.exists())

    def test_oversize_member_is_rejected_before_destination_is_created(self):
        archive = self.archive([("artifacts/unity-webgl/data", tarfile.REGTYPE)])
        with patch.object(publish, "MAX_BYTES", 3), self.assertRaisesRegex(ValueError, "size limit"):
            publish.extract(archive, self.work / "oversize", publish.sha256(archive))
        self.assertFalse((self.work / "oversize").exists())

    def fixture(self):
        source = self.work / "source"
        artifacts = self.work / "artifacts"
        artifacts.mkdir()
        metadata = publish.module("source_metadata_fixture", ROOT / "unity/MahjongRenderer/Tools/source_metadata.py")
        for relative in metadata.INPUT_DIRS:
            (source / relative).mkdir(parents=True)
            (source / relative / "input.txt").write_text("source")
        (source / "scripts").mkdir()
        (source / metadata.PRODUCER).write_text("producer")
        subprocess.run(["git", "init", "--quiet", str(source)], check=True)
        subprocess.run(["git", "-C", str(source), "add", "."], check=True)
        subprocess.run(["git", "-C", str(source), "-c", "user.name=Fixture",
                        "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "fixture"], check=True)
        actual = metadata.metadata(source)
        schema = json.loads((ROOT / "contracts/renderer-v1.schema.json").read_text())
        manifest = {key: rule["const"] for key, rule in
                    schema["definitions"]["BuildManifest"]["properties"].items() if "const" in rule}
        manifest.update(actual, buildId="renderer-" + actual["rendererSourceTreeSha256"][:32],
                        developmentBuild=False, builtAtUtc="2026-10-26T06:46:16Z", streamingAssetsPath=None)
        for purpose, suffix in (("loader", ".loader.js"), ("framework", ".framework.js"),
                                ("data", ".data"), ("wasm", ".wasm")):
            content = b"\0asm\x01\0\0\0" if purpose == "wasm" else b"fixture"
            path = "player" + suffix
            (artifacts / path).write_bytes(content)
            manifest[purpose] = {"path": path, "bytes": len(content), "sha256": hashlib.sha256(content).hexdigest()}
        manifest_path = artifacts / "renderer-build.json"
        manifest_path.write_text(json.dumps(manifest))
        return source, artifacts, actual["rendererSourceRevision"], manifest_path, manifest

    def test_real_verifier_and_fingerprint_roundtrip_and_source_mutation(self):
        source, artifacts, sha, _, _ = self.fixture()
        archive = self.work / "bundle.tar.gz"
        with contextlib.redirect_stdout(io.StringIO()):
            publish.bundle(source, artifacts, sha, archive)
        destination = self.work / "roundtrip"
        inventory = publish.extract(archive, destination, publish.sha256(archive))
        self.assertEqual(inventory, publish.provenance(source, destination, sha))
        (source / "unity/MahjongRenderer/Assets/input.txt").write_text("mutated")
        with self.assertRaisesRegex(ValueError, "rendererSourceTreeSha256"):
            publish.provenance(source, destination, sha)

    def test_manifest_provenance_dirty_development_and_artifact_substitution_are_rejected(self):
        source, artifacts, sha, path, original = self.fixture()
        for key, value in (("rendererSourceRevision", "f" * 40),
                           ("rendererSourceTreeSha256", "f" * 64),
                           ("sourceDirty", True), ("developmentBuild", True)):
            with self.subTest(field=key):
                path.write_text(json.dumps(dict(original, **{key: value})))
                with self.assertRaisesRegex(ValueError, key):
                    publish.provenance(source, artifacts, sha)
        path.write_text(json.dumps(original))
        (artifacts / original["data"]["path"]).write_bytes(b"altered")
        with self.assertRaisesRegex(ValueError, "sha256"):
            publish.provenance(source, artifacts, sha)

    def test_prepare_rejects_non_draft_or_wrong_release_asset_before_download(self):
        pr = {"head": {"repo": {"full_name": publish.REPO}, "sha": SHA},
              "base": {"repo": {"full_name": publish.REPO}, "ref": "dev"}, "state": "open"}
        for release in ({"draft": False}, {"draft": True, "assets": [{"id": 99}]}):
            with self.subTest(release=release), patch.object(publish, "command", return_value=SHA), \
                    patch.object(publish.subprocess, "run") as run, \
                    patch.object(publish, "api", side_effect=[pr, release]):
                with self.assertRaises(ValueError):
                    publish.prepare(self.work, SHA, "21", "1", "2", "c" * 64)
                run.assert_not_called()

    def test_prepare_downloads_authenticated_asset_and_checks_real_source_inventory(self):
        source, artifacts, sha, _, _ = self.fixture()
        archive = self.work / "input.tar.gz"
        with contextlib.redirect_stdout(io.StringIO()):
            publish.bundle(source, artifacts, sha, archive)
        downloaded = ROOT / ".work/ghcr-input.tar.gz"
        self.assertFalse(downloaded.exists())
        self.addCleanup(lambda: downloaded.unlink(missing_ok=True))
        pr = {"head": {"repo": {"full_name": publish.REPO}, "sha": sha},
              "base": {"repo": {"full_name": publish.REPO}, "ref": "dev"}, "state": "open"}
        release = {"draft": True, "assets": [{"id": 2, "state": "uploaded", "size": archive.stat().st_size}]}

        real_run = subprocess.run

        def download(args, *positional, **kwargs):
            if args[0] != "gh":
                return real_run(args, *positional, **kwargs)
            self.assertEqual(args, ["gh", "api", "-H", "Accept: application/octet-stream",
                                    "repos/long2know/mahjong-unity/releases/assets/2"])
            self.assertTrue(kwargs["check"])
            kwargs["stdout"].write(archive.read_bytes())

        with patch.object(publish, "api", side_effect=[pr, release]), \
                patch.object(publish.subprocess, "run", side_effect=download), \
                contextlib.redirect_stdout(io.StringIO()):
            publish.prepare(source, sha, "21", "1", "2", publish.sha256(archive))
        emitted = source / "artifacts/unity-webgl"
        self.assertEqual((emitted / "renderer-build.json").read_bytes(),
                         (artifacts / "renderer-build.json").read_bytes())

    def test_candidate_rejects_fork_mismatched_sha_or_wrong_base_before_download(self):
        for field in ("fork", "sha", "base"):
            pr = {"head": {"repo": {"full_name": publish.REPO}, "sha": SHA},
                  "base": {"repo": {"full_name": publish.REPO}, "ref": "dev"}, "state": "open"}
            if field == "fork":
                pr["head"]["repo"]["full_name"] = "elsewhere/fork"
            elif field == "sha":
                pr["head"]["sha"] = "f" * 40
            else:
                pr["base"]["ref"] = "main"
            with self.subTest(field=field), patch.object(publish, "command", return_value=SHA), \
                    patch.object(publish, "api", return_value=pr), \
                    patch.object(publish.subprocess, "run") as run, self.assertRaisesRegex(ValueError, "exact same-repository"):
                publish.prepare(self.work, SHA, "21", "1", "2", "c" * 64)
            run.assert_not_called()

    def promotion_apis(self, endpoint):
        return {
            "environments/ghcr-production": {"protection_rules": [{"type": "required_reviewers", "reviewers": ["owner"]}]},
            "actions/runs/123/attempts/1": {"conclusion": "success", "event": "workflow_dispatch",
                                         "head_branch": "main", "path": ".github/workflows/ghcr-publish.yml"},
            "pulls/22": {"base": {"repo": {"full_name": publish.REPO}}},
        }[endpoint]

    def test_accepted_digest_is_copied_without_rebuilding_and_read_back(self):
        with patch.object(publish, "api", side_effect=self.promotion_apis), \
                patch.object(publish, "command", return_value=json.dumps({"digest": DIGEST})) as inspect, \
                patch.object(publish.subprocess, "run") as run, contextlib.redirect_stdout(io.StringIO()):
            publish.promote(DIGEST, SHA, "123", "1", "22", "runtime-qualified:" + DIGEST)
            run.assert_called_once_with(["docker", "buildx", "imagetools", "create", "--prefer-index=false",
                                         "--tag", publish.IMAGE + ":latest", publish.IMAGE + "@" + DIGEST], check=True)
            self.assertEqual(inspect.call_count, 4)

    def test_missing_approval_failed_wrong_candidate_or_changed_digest_never_moves_latest(self):
        for mutation in ("confirmation", "environment", "failed", "workflow", "branch", "digest"):
            def response(endpoint):
                result = self.promotion_apis(endpoint)
                if mutation == "environment" and endpoint.startswith("environments/"):
                    result["protection_rules"] = []
                if endpoint.startswith("actions/"):
                    if mutation == "failed":
                        result["conclusion"] = "failure"
                    if mutation == "workflow":
                        result["path"] = ".github/workflows/migration-build.yml"
                    if mutation == "branch":
                        result["head_branch"] = "unreviewed"
                return result
            with self.subTest(mutation=mutation), patch.object(publish, "api", side_effect=response), \
                    patch.object(publish, "command", return_value=json.dumps({"digest": "sha256:" + "c" * 64})), \
                    patch.object(publish.subprocess, "run") as run:
                confirmation = "no" if mutation == "confirmation" else "runtime-qualified:" + DIGEST
                with self.assertRaises(ValueError):
                    publish.promote(DIGEST, SHA, "123", "1", "22", confirmation)
                run.assert_not_called()

    def test_unmerged_source_and_mismatched_source_tag_never_move_latest(self):
        for gate in ("unmerged", "source-tag"):
            calls = 0

            def inspect(*args):
                nonlocal calls
                calls += 1
                if args[0] == "git":
                    if gate == "unmerged":
                        raise subprocess.CalledProcessError(1, args)
                    return ""
                return json.dumps({"digest": DIGEST if calls == 2 else "sha256:" + "c" * 64})

            with self.subTest(gate=gate), patch.object(publish, "api", side_effect=self.promotion_apis), \
                    patch.object(publish, "command", side_effect=inspect), \
                    patch.object(publish.subprocess, "run") as run:
                with self.assertRaises((ValueError, subprocess.CalledProcessError)):
                    publish.promote(DIGEST, SHA, "123", "1", "22", "runtime-qualified:" + DIGEST)
                run.assert_not_called()

    def test_shell_metacharacters_and_noncanonical_ids_never_reach_commands(self):
        for sha, release, asset, checksum in (
            ("main;touch x", "1", "2", "c" * 64),
            (SHA, "../1", "2", "c" * 64), (SHA, "1", "$(id)", "c" * 64),
            (SHA, "1", "2", "not-a-hash"),
        ):
            with patch.object(publish, "command") as command, self.assertRaises(ValueError):
                publish.prepare(self.work, sha, "21", release, asset, checksum)
            command.assert_not_called()

    def test_workflow_publication_contracts(self):
        build = (ROOT / ".github/workflows/ghcr-publish.yml").read_text()
        promote = (ROOT / ".github/workflows/ghcr-promote.yml").read_text()
        for workflow in (build, promote):
            self.assertNotIn("pull_request:", workflow)
            self.assertNotIn("\n  push:", workflow)
            self.assertIn("github.ref == 'refs/heads/main'", workflow)
            self.assertIn("github.repository == 'long2know/mahjong-unity'", workflow)
            self.assertIn("cancel-in-progress: false", workflow)
            for line in workflow.splitlines():
                if "uses:" in line:
                    self.assertRegex(line, r"@[0-9a-f]{40} #")
                if "run:" in line:
                    self.assertNotIn("${{ inputs.", line)
        self.assertIn("platforms: linux/amd64", build)
        self.assertNotIn(":latest", build)
        self.assertIn("ALLOW_SOURCE_DIRTY=0", build)
        self.assertIn("BUILD_SHA=${{ inputs.source_sha }}", build)
        self.assertIn("name: ghcr-production", promote)
        self.assertIn("ACCEPTANCE_PR: ${{ inputs.acceptance_pr }}", promote)
        self.assertIn("set -euo pipefail", promote)


if __name__ == "__main__":
    unittest.main()
