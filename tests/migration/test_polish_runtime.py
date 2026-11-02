"""Hosted handoff contracts; synthetic fixtures are not genuine Unity evidence."""

import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import shutil
import tarfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("polish_runtime", ROOT / "scripts/qualify-polish-candidate.py")
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)


class RuntimeHandoffTests(unittest.TestCase):
    def setUp(self):
        self.work = ROOT / ".work" / ("polish-runtime-test-" + uuid.uuid4().hex)
        self.work.mkdir(parents=True)
        self.addCleanup(shutil.rmtree, self.work)
        self.payloads = {runtime.EDITOR: b'{"synthetic":true}', runtime.XML: b"<synthetic/>",
                         runtime.PLAN: b'{"synthetic":true}'}
        self.hashes = {name: hashlib.sha256(data).hexdigest() for name, data in self.payloads.items()}

    def archive(self, entries=None):
        path = self.work / (uuid.uuid4().hex + ".tar.gz")
        with tarfile.open(path, "w:gz") as archive:
            for name, kind, payload in entries or [(n, tarfile.REGTYPE, d) for n, d in self.payloads.items()]:
                member = tarfile.TarInfo(name)
                member.type = kind
                member.linkname = "../../escape"
                member.size = len(payload) if kind == tarfile.REGTYPE else 0
                archive.addfile(member, io.BytesIO(payload) if member.isfile() else None)
        return path

    def test_pinned_three_file_bundle_preserves_original_bytes(self):
        archive = self.archive()
        destination = self.work / "source"
        with patch.object(runtime, "HASHES", self.hashes):
            runtime.extract_evidence(archive, destination, runtime.digest(archive))
        for name, payload in self.payloads.items():
            self.assertEqual((destination / name).read_bytes(), payload)
        self.assertEqual(len([p for p in destination.rglob("*") if p.is_file()]), 3)

    def test_outer_digest_mutation_cannot_create_evidence(self):
        archive = self.archive()
        destination = self.work / "source"
        with self.assertRaisesRegex(ValueError, "bundle SHA256"):
            runtime.extract_evidence(archive, destination, "0" * 64)
        self.assertFalse(destination.exists())

    def test_inner_member_mutation_is_rejected_even_with_matching_outer_digest(self):
        entries = [(n, tarfile.REGTYPE, b"mutated" if n == runtime.XML else d)
                   for n, d in self.payloads.items()]
        archive = self.archive(entries)
        destination = self.work / "source"
        with patch.object(runtime, "HASHES", self.hashes), self.assertRaisesRegex(ValueError, "Original Editor/XML"):
            runtime.extract_evidence(archive, destination, runtime.digest(archive))
        self.assertFalse(destination.exists())

    def test_missing_extra_duplicate_traversal_and_linked_members_are_rejected_before_writes(self):
        valid = [(n, tarfile.REGTYPE, d) for n, d in self.payloads.items()]
        cases = [valid[:-1], valid + [valid[0]], valid + [("../escape", tarfile.REGTYPE, b"x")],
                 valid + [("/absolute", tarfile.REGTYPE, b"x")],
                 [(runtime.XML, tarfile.SYMTYPE, b"")], [(runtime.XML, tarfile.LNKTYPE, b"")],
                 [(runtime.XML, tarfile.FIFOTYPE, b"")]]
        for entries in cases:
            with self.subTest(entries=entries):
                archive = self.archive(entries)
                destination = self.work / ("source-" + uuid.uuid4().hex)
                with patch.object(runtime, "HASHES", self.hashes), self.assertRaises(ValueError):
                    runtime.extract_evidence(archive, destination, runtime.digest(archive))
                self.assertFalse(destination.exists())

    def test_linked_destination_parent_cannot_redirect_genuine_evidence(self):
        outside = self.work / "outside"
        outside.mkdir()
        linked = self.work / "linked"
        linked.symlink_to(outside, target_is_directory=True)
        archive = self.archive()
        with patch.object(runtime, "HASHES", self.hashes), self.assertRaisesRegex(ValueError, "Linked"):
            runtime.extract_evidence(archive, linked, runtime.digest(archive))
        self.assertEqual(list(outside.iterdir()), [])

    def test_existing_original_evidence_cannot_be_overwritten(self):
        source = self.work / "source"
        old = source / runtime.EDITOR
        old.parent.mkdir(parents=True)
        old.write_bytes(b"retained original")
        archive = self.archive()
        with patch.object(runtime, "HASHES", self.hashes), self.assertRaisesRegex(ValueError, "overwritten"):
            runtime.extract_evidence(archive, source, runtime.digest(archive))
        self.assertEqual(old.read_bytes(), b"retained original")

    def test_changed_source_or_dirty_checkout_fails_before_network(self):
        for results in ([b"f" * 40, b""], [runtime.SOURCE.encode(), b" M tracked-file"]):
            with self.subTest(results=results), patch.object(runtime.subprocess, "check_output",
                                                            side_effect=results) as command:
                with self.assertRaises(ValueError):
                    runtime.prepare(self.work)
                self.assertTrue(all(call.args[0][0] == "git" for call in command.call_args_list))

    def test_wrong_release_or_cross_release_asset_fails_before_download(self):
        for release in ({"draft": False}, {"draft": True, "assets": []},
                        {"draft": True, "assets": [{"id": runtime.ASSET, "state": "new", "size": 1}]}):
            with self.subTest(release=release), patch.object(runtime, "checked_source"), \
                    patch.object(runtime.subprocess, "check_output", return_value=json.dumps(release)), \
                    patch.object(runtime.subprocess, "run") as download, self.assertRaises(ValueError):
                runtime.prepare(self.work)
            download.assert_not_called()

    def test_image_identity_source_platform_nonroot_and_dirty_override_are_fail_closed(self):
        image = {"Id": runtime.CONFIG, "Os": "linux", "Architecture": "amd64",
                 "RepoDigests": [runtime.IMAGE],
                 "Config": {"User": "1000:1000", "Env": ["BUILD_SHA=" + runtime.SOURCE],
                            "Labels": {"org.opencontainers.image.revision": runtime.SOURCE,
                                       "org.mahjong-unity.allow-source-dirty": "0"}}}
        runtime.validate_image(image)
        mutations = [
            ("Id", "sha256:" + "f" * 64), ("Architecture", "arm64"), ("RepoDigests", []),
            ("Config.User", "0:0"), ("Config.Env", ["BUILD_SHA=other"]),
            ("Config.Labels.org.opencontainers.image.revision", "f" * 40),
            ("Config.Labels.org.mahjong-unity.allow-source-dirty", "1"),
        ]
        for field, value in mutations:
            with self.subTest(field=field):
                changed = copy.deepcopy(image)
                if field.startswith("Config.Labels."):
                    changed["Config"]["Labels"][field.removeprefix("Config.Labels.")] = value
                elif field.startswith("Config."):
                    changed["Config"][field.removeprefix("Config.")] = value
                else:
                    changed[field] = value
                with self.assertRaises(ValueError):
                    runtime.validate_image(changed)

    def test_static_partitions_call_original_wrapper_and_mobile_keeps_native_lifecycle(self):
        plan = {"ports": dict.fromkeys(runtime.GROUPS, 5105),
                "groups": {g: ["actual-" + g] for g in runtime.GROUPS if g != "mobile"}}
        for group in runtime.GROUPS:
            with self.subTest(group=group):
                args, name = runtime.player_command(group, plan, "123", "1")
                self.assertEqual(args[:4], ["python3", "tests/migration/run_runtime_player_integration.py",
                                           "--image", runtime.CONFIG])
                self.assertIn(runtime.EDITOR, args)
                self.assertIn(group, name)
                if group == "mobile":
                    self.assertIn("--mobile-playability", args)
                    self.assertNotIn("--cases", args)
                else:
                    self.assertEqual(args[-2:], ["--cases", "actual-" + group])
        with self.assertRaises(ValueError):
            runtime.player_command("arbitrary", plan, "123", "1")

    def run_partition_fixture(self, group, runtime_status="passed"):
        source = self.work / "source"
        plan = {"ports": dict.fromkeys(runtime.GROUPS, 5105),
                "groups": {g: ["actual-" + g] for g in runtime.GROUPS if g != "mobile"}}
        payloads = dict(self.payloads, **{runtime.PLAN: json.dumps(plan).encode()})
        hashes = {p: hashlib.sha256(data).hexdigest() for p, data in payloads.items()}
        for relative, data in payloads.items():
            path = source / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        image = {"Id": runtime.CONFIG, "Os": "linux", "Architecture": "amd64", "RepoDigests": [runtime.IMAGE],
                 "Config": {"User": "1000:1000", "Env": ["BUILD_SHA=" + runtime.SOURCE],
                            "Labels": {"org.opencontainers.image.revision": runtime.SOURCE,
                                       "org.mahjong-unity.allow-source-dirty": "0"}}}

        def execute(args, **kwargs):
            if args[0] == "docker":
                self.assertEqual(args[:4], ["docker", "pull", "--platform", "linux/amd64"])
            else:
                self.assertEqual(args[:2], ["python3", "tests/migration/run_runtime_player_integration.py"])
                self.assertEqual(kwargs["cwd"], source)
                name = args[args.index("--output-name") + 1]
                for suffix, data in [
                    ("", {"status": runtime_status, "imageId": runtime.CONFIG,
                          "actualHealth": {"buildSha": runtime.SOURCE}}),
                    ("-browser", {"status": "passed", "browserEvidence": {
                        "cases": [{"name": "synthetic-not-qualified-rendering"}], "caseFailures": []}}),
                ]:
                    path = source / "artifacts/test-results" / (name + suffix)
                    path.mkdir(parents=True)
                    (path / ("player-receipt.json" if suffix else "runtime-receipt.json")).write_text(json.dumps(data))
            return SimpleNamespace(returncode=0)

        with patch.object(runtime, "HASHES", hashes), patch.object(runtime, "checked_source"), \
                patch.object(runtime.os, "getuid", return_value=1000), \
                patch.dict(runtime.os.environ, {"GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "1"}), \
                patch.object(runtime.subprocess, "check_output", return_value=json.dumps([image])), \
                patch.object(runtime.subprocess, "run", side_effect=execute) as run:
            if runtime_status == "passed":
                runtime.qualify(source, group)
            else:
                with self.assertRaisesRegex(ValueError, "canonical receipt failed"):
                    runtime.qualify(source, group)
        receipt = json.loads((source / f"artifacts/test-results/polish/runtime-hosted/{group}-group.json").read_text())
        self.assertEqual([call.args[0][-1] for call in run.call_args_list[:2]], [runtime.IMAGE, runtime.BROWSER])
        self.assertEqual(receipt["publishedImage"], runtime.IMAGE)
        self.assertEqual(receipt["imageId"], runtime.CONFIG)
        self.assertEqual(receipt["sourceCommit"], runtime.SOURCE)
        self.assertFalse(receipt["combinedQualification"])
        self.assertFalse(receipt["independentTesterApproved"])
        return source, receipt

    def test_partition_caller_pins_actual_raw_receipts_and_preserves_full_mobile_command(self):
        source, receipt = self.run_partition_fixture("mobile")
        self.assertEqual(receipt["status"], "passed")
        record = receipt["groups"][0]
        self.assertIn("--mobile-playability", record["command"])
        self.assertNotIn("--cases", record["command"])
        for kind in ("runtime", "player"):
            self.assertEqual(record[kind + "ReceiptSha256"], runtime.digest(source / record[kind + "Receipt"]))
        self.assertEqual(record["caseInventory"], ["synthetic-not-qualified-rendering"])

    def test_zero_exit_with_failed_actual_receipt_never_claims_passing_partition(self):
        _, receipt = self.run_partition_fixture("general", runtime_status="failed")
        self.assertEqual(receipt["status"], "failed")
        self.assertEqual(receipt["groups"][0]["runtimeStatus"], "failed")
        self.assertIn("canonical receipt failed", receipt["failure"]["message"])

    def test_root_runner_is_rejected_before_source_or_network_access(self):
        with patch.object(runtime.os, "getuid", return_value=0), \
                patch.object(runtime, "checked_source") as check, self.assertRaisesRegex(ValueError, "non-root"):
            runtime.qualify(self.work, "general")
        check.assert_not_called()

    def test_workflow_remains_manual_main_only_isolated_readonly_registry_and_always_retained(self):
        workflow = (ROOT / ".github/workflows/polish-runtime.yml").read_text()
        self.assertIn("workflow_dispatch:", workflow)
        self.assertNotIn("pull_request:", workflow)
        self.assertIn("github.ref == 'refs/heads/main'", workflow)
        self.assertIn("group: [general, focus, camera, relay, mobile]", workflow)
        self.assertIn("fail-fast: false", workflow)
        self.assertIn("packages: read", workflow)
        self.assertNotIn("packages: write", workflow)
        self.assertIn("if: always()", workflow)
        self.assertEqual(workflow.count("GH_TOKEN:"), 1)
        self.assertNotIn("docker/login-action", workflow)
        self.assertIn("test_polish_runtime.py", (ROOT / ".github/workflows/migration-build.yml").read_text())


if __name__ == "__main__":
    unittest.main()
