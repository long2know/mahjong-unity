"""Hosted handoff contracts; synthetic fixtures are not genuine Unity evidence."""

import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import tarfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("polish_runtime", ROOT / "scripts/qualify-polish-candidate.py")
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)
player_spec = importlib.util.spec_from_file_location("headful_runtime", ROOT / "tests/migration/run_player_integration.py")
player = importlib.util.module_from_spec(player_spec)
player_spec.loader.exec_module(player)


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
                self.assertIn("-hosted-" + runtime.SOURCE[:7] + "-", name)
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
        tooling = {"applicationSourceSha": runtime.SOURCE, "probeSourceSha": "reviewed-probe-revision",
                   "probeTooling": {"root": str(self.work / "focus-tools")}}
        tool_environment = {"MAHJONG_APPLICATION_SOURCE_ROOT": str(source),
                            "MAHJONG_PROBE_TOOL_ROOT": tooling["probeTooling"]["root"],
                            "MAHJONG_TEST_TOOLING_RECEIPT": str(self.work / "tooling-provenance.json")}

        def execute(args, **kwargs):
            if args[0] == "docker":
                self.assertEqual(args[:4], ["docker", "pull", "--platform", "linux/amd64"])
            else:
                expected = (str(Path(tooling["probeTooling"]["root"]) / "run_runtime_player_integration.py")
                            if group == "focus" else "tests/migration/run_runtime_player_integration.py")
                self.assertEqual(args[:2], ["python3", expected])
                self.assertEqual(kwargs["cwd"], source)
                if group == "focus":
                    for key, value in tool_environment.items():
                        self.assertEqual(kwargs["env"][key], value)
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
                patch.object(runtime, "prepare_runtime_fixtures", return_value={"runtimeUid": 1000}) as fixtures, \
                patch.object(runtime.focus_tools, "stage", return_value=(tooling, tool_environment)) as stage, \
                patch.object(runtime.focus_tools, "validate") as validate, \
                patch.object(runtime.subprocess, "run", side_effect=execute) as run:
            if runtime_status == "passed":
                runtime.qualify(source, group)
            else:
                with self.assertRaisesRegex(ValueError, "canonical receipt failed"):
                    runtime.qualify(source, group)
        fixtures.assert_called_once_with(source)
        if group == "focus":
            stage.assert_called_once_with(source, ROOT / ".work/focus-tooling")
            validate.assert_called_once_with(tooling)
        else:
            stage.assert_not_called()
            validate.assert_not_called()
        receipt = json.loads((source / f"artifacts/test-results/polish/runtime-hosted/{group}-group.json").read_text())
        self.assertEqual(receipt["fixturePermissions"]["runtimeUid"], 1000)
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

    def test_focus_caller_records_distinct_application_and_executed_probe_revision(self):
        _, receipt = self.run_partition_fixture("focus")
        self.assertEqual(receipt["applicationSourceSha"], runtime.SOURCE)
        self.assertEqual(receipt["probeSourceSha"], "reviewed-probe-revision")
        self.assertEqual(receipt["sourceCommit"], runtime.SOURCE)
        self.assertEqual(receipt["publishedImage"], runtime.IMAGE)
        self.assertEqual(receipt["groups"][0]["command"][1],
                         str(Path(receipt["probeTooling"]["root"]) / "run_runtime_player_integration.py"))

    def test_root_runner_is_rejected_before_source_or_network_access(self):
        with patch.object(runtime.os, "getuid", return_value=0), \
                patch.object(runtime, "checked_source") as check, self.assertRaisesRegex(ValueError, "non-root"):
            runtime.qualify(self.work, "general")
        check.assert_not_called()

    def test_fixture_bridge_rejects_existing_directory_without_changing_it(self):
        work = self.work / "artifacts/test-results/migration-player-work"
        work.mkdir(parents=True)
        original = work.stat().st_mode
        with patch.object(runtime.subprocess, "run") as acl, self.assertRaises(FileExistsError):
            runtime.prepare_runtime_fixtures(self.work)
        acl.assert_not_called()
        self.assertEqual(work.stat().st_mode, original)

    def test_fixture_bridge_rejects_linked_ancestor_without_touching_target(self):
        outside = self.work / "outside"
        outside.mkdir()
        (self.work / "artifacts").symlink_to(outside, target_is_directory=True)
        with patch.object(runtime.subprocess, "run") as acl, self.assertRaisesRegex(ValueError, "Linked"):
            runtime.prepare_runtime_fixtures(self.work)
        acl.assert_not_called()
        self.assertEqual(list(outside.iterdir()), [])

    @unittest.skipUnless(os.environ.get("POLISH_POSIX_UID_REGRESSION") == "1",
                         "Requires hosted POSIX UID1001 runner and normal Docker tmpfs mounts")
    def test_actual_headful_tmpfs_stat_and_private_uid1001_authority_write(self):
        uid, gid = os.getuid(), os.getgid()
        self.assertEqual(uid, 1001)
        fixture = self.work / "headful-fixture"
        fixture.mkdir()
        for name in ("home", "temp", "screenshots"):
            (fixture / name).mkdir()
        subprocess.run(["docker", "pull", "--platform", "linux/amd64", runtime.BROWSER],
                       check=True, stdout=subprocess.DEVNULL, timeout=180)

        def probe(option):
            result = subprocess.run([
                "docker", "run", "--rm", "--pull=never", "--init", "--platform", "linux/amd64",
                "--user", f"{uid}:{gid}", "--read-only", "--cap-drop=ALL",
                "--security-opt=no-new-privileges:true", "--network=host", "--shm-size=256m",
                "--workdir=/workspace", "--tmpfs", option,
                "--tmpfs", "/var/lib/xkb:rw,nosuid,nodev,size=16m,mode=1777",
                "--memory=1536m", "--pids-limit=512", "--env", "TMPDIR=" + option.split(":")[0],
                "--mount", f"type=bind,source={fixture},target=/probe",
                "--entrypoint", "node", runtime.BROWSER, "-e",
                "const fs=require('node:fs');const dir=process.env.TMPDIR,s=fs.statSync(dir);"
                "const r={uid:s.uid,gid:s.gid,mode:s.mode&511,user:process.getuid()};"
                "try{const p=dir+'/xauthority-regression';fs.writeFileSync(p,'private',"
                "{mode:0o600,flag:'wx'});const f=fs.statSync(p);"
                "r.file={uid:f.uid,gid:f.gid,mode:f.mode&511};fs.unlinkSync(p)}"
                "catch(e){r.writeError=e.code}console.log(JSON.stringify(r));",
            ], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            return json.loads(result.stdout)

        before = probe("/probe/temp:rw,nosuid,nodev,size=256m")
        print("Actual original hardened tmpfs stat/write:", json.dumps(before), flush=True)
        self.assertEqual(before.get("writeError"), "EACCES")
        for directory in ("/probe/temp", "/tmp"):
            with self.subTest(directory=directory):
                after = probe(player.private_browser_tmpfs(directory, uid, gid))
                print("Actual private UID/GID tmpfs stat/write:", directory, json.dumps(after), flush=True)
                self.assertEqual(after, {"uid": uid, "gid": gid, "mode": 0o700, "user": uid,
                                         "file": {"uid": uid, "gid": gid, "mode": 0o600}})

    @unittest.skipUnless(os.environ.get("POLISH_POSIX_UID_REGRESSION") == "1",
                         "Requires hosted POSIX UID1001 runner and normal Docker fixture mounts")
    def test_real_uid1001_fixtures_allow_uid1000_sqlite_and_bootstrap_writes(self):
        self.assertEqual(os.getuid(), 1001, "This regression must exercise the actual hosted runner UID")
        subprocess.run(["docker", "pull", "--platform", "linux/amd64", runtime.BROWSER],
                       check=True, stdout=subprocess.DEVNULL, timeout=180)

        def write_as_runtime(fixture):
            return subprocess.run([
                "docker", "run", "--rm", "--pull=never", "--platform", "linux/amd64",
                "--network", "none", "--user", "1000:1000", "--read-only", "--cap-drop=ALL",
                "--security-opt=no-new-privileges:true",
                "--mount", f"type=bind,source={fixture},target=/data",
                "--mount", f"type=bind,source={fixture / 'bootstrap-cache'},target=/app/data",
                "--entrypoint", "python3", runtime.BROWSER, "-c",
                "import os,pathlib,sqlite3; assert os.getuid()==1000 and os.getgid()==1000; "
                "db=sqlite3.connect('/data/state.db'); db.execute('create table proof (value integer)'); "
                "db.execute('insert into proof values (42)'); db.commit(); db.close(); "
                "pathlib.Path('/app/data/proof').write_text('uid1000'); "
                "pathlib.Path('/app/data/created-by-runtime').mkdir()",
            ], capture_output=True, text=True)

        denied = self.work / "unbridged"
        denied.mkdir(mode=0o755)
        (denied / "bootstrap-cache").mkdir(mode=0o755)
        before = write_as_runtime(denied)
        self.assertNotEqual(before.returncode, 0)
        self.assertIn("unable to open database file", before.stderr)
        self.assertFalse((denied / "state.db").exists())

        bridge = runtime.prepare_runtime_fixtures(self.work)
        root = self.work / bridge["path"]
        fixture = root / ("runtime-" + uuid.uuid4().hex)
        fixture.mkdir()
        (fixture / "bootstrap-cache").mkdir()
        self.assertEqual(fixture.stat().st_uid, 1001)
        self.assertEqual((fixture / "bootstrap-cache").stat().st_uid, 1001)
        written = write_as_runtime(fixture)
        self.assertEqual(written.returncode, 0, written.stderr + "\n" +
                         subprocess.check_output(["getfacl", "-p", str(fixture)], text=True))
        self.assertEqual((fixture / "state.db").stat().st_uid, 1000)
        self.assertEqual((fixture / "bootstrap-cache/proof").read_text(), "uid1000")
        self.assertEqual((fixture / "bootstrap-cache/created-by-runtime").stat().st_uid, 1000)
        with sqlite3.connect(fixture / "state.db") as db:
            self.assertEqual(db.execute("select value from proof").fetchall(), [(42,)])
        shutil.rmtree(fixture)
        self.assertFalse(fixture.exists(), "The runner must retain canonical fixture cleanup access")
        print("Actual POSIX UID1001 -> UID1000 SQLite/bootstrap regression passed", flush=True)

    def test_workflow_remains_manual_main_only_isolated_readonly_registry_and_always_retained(self):
        workflow = (ROOT / ".github/workflows/polish-runtime.yml").read_text()
        self.assertIn("workflow_dispatch:", workflow)
        self.assertNotIn("pull_request:", workflow)
        self.assertIn("github.ref == 'refs/heads/main'", workflow)
        self.assertIn('\'["general","focus","camera","relay","mobile"]\'', workflow)
        self.assertIn('inputs.focus_only && \'["focus"]\'', workflow)
        self.assertIn("fail-fast: false", workflow)
        for field in ("ref", "SOURCE_SHA", "BUILD_SHA"):
            self.assertIn(field + ": " + runtime.SOURCE, workflow)
        self.assertIn('RELEASE_ID: "' + runtime.RELEASE + '"', workflow)
        self.assertIn("polish-runtime-" + runtime.SOURCE[:7], workflow)
        self.assertIn("packages: read", workflow)
        self.assertNotIn("packages: write", workflow)
        self.assertIn("if: always()", workflow)
        self.assertEqual(workflow.count("GH_TOKEN:"), 1)
        self.assertNotIn("docker/login-action", workflow)
        self.assertIn("test_polish_runtime.py", (ROOT / ".github/workflows/migration-build.yml").read_text())


if __name__ == "__main__":
    unittest.main()
