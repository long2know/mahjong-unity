"""Focus tool provenance/guard contracts; no game/browser outcomes are simulated."""

import ast
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import unittest
from unittest.mock import patch
import uuid


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("focus_tools", ROOT / "scripts/focus_probe_tooling.py")
tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tools)
BROWSER = ("mcr.microsoft.com/playwright:v1.60.0-noble@"
           "sha256:83192064c7510f7ee73dd63dc5f22a5e01a92c81a2e6a9c715d9e3fe55471fd9")


@unittest.skipUnless(os.environ.get("FOCUS_APPLICATION_ROOT"), "Requires the actual clean frozen application checkout")
class FocusToolingTests(unittest.TestCase):
    def setUp(self):
        self.source = Path(os.environ["FOCUS_APPLICATION_ROOT"]).resolve(strict=True)
        self.work = ROOT / ".work" / ("focus-tools-test-" + uuid.uuid4().hex)
        self.work.mkdir(parents=True)
        self.addCleanup(shutil.rmtree, self.work)

    def staged(self):
        return tools.stage(self.source, self.work / "tools")

    def test_derivation_preserves_application_and_records_actual_tool_bytes(self):
        before = {name: tools.sha(self.source / "tests/migration" / name) for name in tools.INPUTS}
        record, environment = self.staged()
        self.assertEqual(record["applicationSourceSha"], tools.APPLICATION_SOURCE)
        self.assertEqual(record["probeSourceSha"], subprocess.check_output(
            ["git", "-C", str(ROOT), "rev-parse", "HEAD"], text=True).strip())
        root = Path(record["probeTooling"]["root"])
        self.assertEqual(json.loads(Path(environment["MAHJONG_TEST_TOOLING_RECEIPT"]).read_text()), record)
        for name in tools.INPUTS:
            self.assertEqual(tools.sha(self.source / "tests/migration" / name), before[name])
            self.assertEqual(tools.sha(root / name), record["probeTooling"]["executedFileSha256"][name])
            if name.endswith(".py"):
                ast.parse((root / name).read_text())
        original = (self.source / "tests/migration/browser-gameplay.mjs").read_text()
        self.assertEqual((root / "browser-gameplay.mjs").read_text(),
                         original.replace(tools.OLD_DISPLAY_GUARD, tools.DISPLAY_GUARD))
        self.assertEqual(tools.sha(root / "player-headful.mjs"), tools.INPUTS["player-headful.mjs"])
        tools.validate(record)
        (root / "browser-gameplay.mjs").write_text("changed actual probe")
        with self.assertRaisesRegex(ValueError, "Executed focus-tool bytes changed"):
            tools.validate(record)

    def test_frozen_input_mutation_is_rejected_before_derivative_creation(self):
        changed = dict(tools.INPUTS, **{"browser-gameplay.mjs": "0" * 64})
        with patch.object(tools, "INPUTS", changed), self.assertRaisesRegex(ValueError, "Frozen focus-tool input"):
            self.staged()
        self.assertFalse((self.work / "tools").exists())

    def test_tool_checkout_changes_outside_owned_work_still_fail_closed(self):
        for status in (b" M scripts/focus_probe_tooling.py\n", b"?? foreign-probe.mjs\n"):
            with self.subTest(status=status), patch.object(tools.subprocess, "check_output",
                    side_effect=[tools.APPLICATION_SOURCE + "\n", b"", "probe-revision\n", status]), \
                    self.assertRaisesRegex(ValueError, "tooling checkout must be clean"):
                self.staged()
        self.assertFalse((self.work / "tools").exists())

    def test_actual_consumer_guard_rejects_remote_display_formats(self):
        record, _ = self.staged()
        result = subprocess.run([
            "node", str(ROOT / "tests/migration/private-display-contract.mjs"),
            str(self.source / "tests/migration/browser-gameplay.mjs"),
            str(Path(record["probeTooling"]["root"]) / "browser-gameplay.mjs"), "--syntax-only",
        ], check=True, capture_output=True, text=True)
        self.assertEqual(json.loads(result.stdout), {"localSyntaxAccepted": True, "remoteSyntaxRejected": True})

    @unittest.skipUnless(os.environ.get("POLISH_POSIX_UID_REGRESSION") == "1",
                         "Requires hosted non-root Docker/private-Xvfb tooling")
    def test_actual_private_cookie_launcher_native_x11_focus_and_restore(self):
        self.assertEqual(os.getuid(), 1001)
        record, _ = self.staged()
        root = Path(record["probeTooling"]["root"])
        fixture = self.work / "fixture"
        fixture.mkdir()
        for name in ("home", "temp"):
            (fixture / name).mkdir()
        subprocess.run(["docker", "pull", "--platform", "linux/amd64", BROWSER],
                       check=True, stdout=subprocess.DEVNULL, timeout=180)
        result = subprocess.run([
            "docker", "run", "--rm", "--pull=never", "--init", "--platform", "linux/amd64",
            "--user", f"{os.getuid()}:{os.getgid()}", "--read-only", "--cap-drop=ALL",
            "--security-opt=no-new-privileges:true", "--network=host", "--shm-size=256m",
            "--workdir=/workspace", "--memory=1536m", "--pids-limit=512",
            "--tmpfs", f"/probe/temp:rw,nosuid,nodev,size=256m,uid={os.getuid()},gid={os.getgid()},mode=0700",
            "--tmpfs", "/var/lib/xkb:rw,nosuid,nodev,size=16m,mode=1777",
            "--env", "TMPDIR=/probe/temp", "--env", "HOME=/probe/home",
            "--env", "MAHJONG_PLAYER_HEADFUL=1",
            "--mount", f"type=bind,source={fixture},target=/probe",
            "--mount", f"type=bind,source={root},target=/tools,readonly",
            "--mount", f"type=bind,source={self.source / 'tests/migration/browser-gameplay.mjs'},target=/original.mjs,readonly",
            "--mount", f"type=bind,source={ROOT / 'tests/migration/private-display-contract.mjs'},target=/contract.mjs,readonly",
            BROWSER, "node", "/tools/player-headful.mjs", "/contract.mjs",
            "/original.mjs", "/tools/browser-gameplay.mjs",
        ], capture_output=True, text=True, timeout=45)
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        observed = json.loads(result.stdout)
        self.assertTrue(observed["actualAuthenticatedLoopbackDisplay"])
        self.assertTrue(observed["originalGuardRejected"])
        self.assertTrue(observed["nativeX11FocusAndRestorePassed"])
        print("Actual unchanged private-cookie Xvfb/native-X11 regression:", json.dumps(observed), flush=True)
        tools.validate(record)


if __name__ == "__main__":
    unittest.main()
