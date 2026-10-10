"""Unit-only mocked process evidence; no browser or Unity/player proof."""

import contextlib
import importlib.util
import io
import json
from pathlib import Path
import shutil
import subprocess
import sys
import types
import unittest
from unittest import mock
import uuid


ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location(
    "browser_runner_under_test", ROOT / "tests/migration/run_browser_integration.py",
)
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


def unit_evidence(probe):
    if probe == "browser-unavailable.mjs":
        return {
            "status": "passed",
            "qualification": "Actual Chrome browser shell and unavailable renderer only; no Unity/player/device evidence",
            "browserVersion": "unit-test-only",
            "cases": [
                {
                    "viewport": {"width": width, "height": height},
                    "lobbyOnlyWithoutCanvas": True, "manifestStatus": 404,
                    "visibleState": "unavailable", "primaryClientConnected": True,
                    "rendererReady": False, "sceneReady": False,
                    "legacyFallback": False, "pageErrors": 0,
                }
                for width, height in ((1280, 800), (390, 844))
            ],
        }
    return {
        "status": "passed",
        "qualification": "Actual Chromium lobby, invitations, chat and unavailable-table viewport routing; not Unity/player/device gameplay",
        "browserVersion": "unit-test-only",
        "pageErrors": [],
        "moduleGraph": {
            "moduleCount": 373, "legacySourceImports": False, "actualServedGraph": True,
        },
        "cases": [
            {
                "name": "lobby-only", "canvasAbsent": True,
                "bareRoomChatDisabled": True, "bothSocialShellsMounted": True,
            },
            {
                "name": "real-invitation-and-join", "sameOriginAllowlistedJoinOnlyUrl": True,
                "ownerSeat": 0, "guestSeat": 1, "manifest404Observed": True,
                "rendererReady": False, "legacyFallback": False,
            },
            {
                "name": "real-chat-reload", "sentThroughDom": True,
                "otherBrowserReceived": True, "signedRoomReattached": True,
                "historyRestored": True,
            },
            {
                "name": "actual-unavailable-viewport-routing",
                "seatAuthorityUnchanged": True, "unityFrameRendered": False,
                "measurements": [
                    {
                        "widthCssPx": width, "heightCssPx": height,
                        "widthPx": width, "heightPx": height,
                        "renderPixelRatio": 1, "resizeRevision": revision,
                        "handBandCssPx": 96 if width < 768 else 0,
                        "actorSeat": 0, "state": "unavailable",
                    }
                    for revision, (width, height) in enumerate(
                        ((390, 844), (844, 390), (1280, 800)), 1,
                    )
                ],
            },
        ],
    }


class BrowserEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.fixture = ROOT / "artifacts/test-results" / ("browser-evidence-unit-" + uuid.uuid4().hex)
        self.fixture.mkdir(parents=True)

    def tearDown(self):
        shutil.rmtree(self.fixture)

    def run_probe(self, stdout, probe="browser-unavailable.mjs", code=0, error=None,
                  metadata_error=False, stop_error=False):
        output = self.fixture / ("output-" + uuid.uuid4().hex)
        work = self.fixture / ("work-" + uuid.uuid4().hex)
        commands = []
        stopped = []

        class FakeApi:
            base = "http://127.0.0.1:12345"
            receipt = {"checks": {}}

            @classmethod
            def setUpClass(cls):
                cls.fixture = work / ("published-" + uuid.uuid4().hex)
                cls.fixture.mkdir(parents=True)

            @classmethod
            def stop(cls):
                stopped.append(True)
                cls.receipt.update(processStopped=True, gracefulShutdown=True)
                shutil.rmtree(cls.fixture)
                if stop_error:
                    raise RuntimeError("Unit-only owned API stop failure")

        api_module = types.SimpleNamespace(PublishedIntegrationTests=FakeApi)
        api_spec = types.SimpleNamespace(loader=types.SimpleNamespace(exec_module=lambda module: None))

        def run(command, **kwargs):
            commands.append(command)
            if command[:2] == ["docker", "run"]:
                if error is not None:
                    raise error
                return subprocess.CompletedProcess(command, code, stdout, "Unit-only probe stderr\n")
            if command[:3] == ["docker", "container", "inspect"]:
                return subprocess.CompletedProcess(command, 1, "", "Error: No such container: unit-only\n")
            return subprocess.CompletedProcess(command, 0, "", "")

        read_bytes = Path.read_bytes

        def source_bytes(path):
            if metadata_error and path.name == "renderer-module-graph.json":
                raise OSError("Unit-only source metadata read failure")
            return read_bytes(path)

        with (
            mock.patch.object(runner, "OUTPUT", output),
            mock.patch.object(runner, "WORK", work),
            mock.patch.object(sys, "argv", ["run_browser_integration.py", "--probe", probe]),
            mock.patch.object(runner.importlib.util, "spec_from_file_location", return_value=api_spec),
            mock.patch.object(runner.importlib.util, "module_from_spec", return_value=api_module),
            mock.patch.object(runner.subprocess, "run", side_effect=run),
            mock.patch.object(runner.subprocess, "check_output", return_value="0" * 40 + "\n"),
            mock.patch.object(Path, "read_bytes", source_bytes),
            contextlib.redirect_stdout(io.StringIO()),
            contextlib.redirect_stderr(io.StringIO()),
        ):
            returned = runner.main()
        receipt = json.loads((output / (probe.removesuffix(".mjs") + "-receipt.json")).read_text())
        self.assertTrue(stopped, "The production caller must stop its owned API on evidence failure")
        self.assertTrue(receipt["ownedContainerRemoved"])
        self.assertTrue(receipt["ownedBrowserFixtureRemoved"])
        self.assertFalse(list(work.glob("container-*")))
        self.assertFalse(list(work.glob("published-*")))
        self.assertTrue(any(command[:2] == ["docker", "run"] for command in commands))
        return returned, receipt

    def assert_rejected(self, stdout, **kwargs):
        returned, receipt = self.run_probe(stdout, **kwargs)
        self.assertNotEqual(0, returned)
        self.assertEqual("failed", receipt["status"])
        self.assertIn("failure", receipt)
        return receipt

    def test_zero_exit_malformed_json_fails_with_persistent_receipt_and_cleanup(self):
        receipt = self.assert_rejected("not JSON\n")
        self.assertEqual(0, receipt["probeExitCode"])
        self.assertIn("JSON", receipt["failure"]["message"])
        self.assertIn("tests/migration/run_browser_integration.py", receipt["sourceSha256"])

    def test_zero_exit_nonobject_evidence_cannot_pass(self):
        for value in (None, [], "passed", 1, True):
            with self.subTest(value=value):
                self.assert_rejected(json.dumps(value))

    def test_ambiguous_or_nonfinite_json_cannot_pass(self):
        for stdout in (
            '{"status":"failed","status":"passed"}',
            '{"status":"passed","cases":NaN}',
            json.dumps(unit_evidence("browser-unavailable.mjs")) + "\n{}",
        ):
            with self.subTest(stdout=stdout):
                receipt = self.assert_rejected(stdout)
                self.assertEqual("browser-evidence", receipt["failure"]["stage"])

    def test_zero_exit_failed_or_blocked_evidence_preserves_original_failure(self):
        for status in ("failed", "blocked"):
            evidence = {
                "status": status, "cases": [],
                "failure": {"name": "OriginalProbeError", "message": "Original failure retained"},
            }
            with self.subTest(status=status):
                receipt = self.assert_rejected(json.dumps(evidence))
                self.assertEqual(evidence, receipt["browserEvidence"])

    def test_both_probes_require_the_exact_complete_case_inventory(self):
        for probe in ("browser-unavailable.mjs", "browser-social.mjs"):
            evidence = unit_evidence(probe)
            mutations = (
                {}, {"cases": []}, {"cases": {}}, {"cases": [None]},
                {"cases": evidence["cases"][:-1]},
                {"cases": evidence["cases"] + [evidence["cases"][0]]},
                {"cases": [evidence["cases"][0]] * len(evidence["cases"])},
                {"cases": [{"name": "unexecuted"}] * len(evidence["cases"])},
            )
            for mutation in mutations:
                value = {**evidence, **mutation}
                if not mutation:
                    del value["cases"]
                with self.subTest(probe=probe, mutation=mutation):
                    self.assert_rejected(json.dumps(value), probe=probe)

    def test_success_case_labels_cannot_hide_failure_fields(self):
        for probe in ("browser-unavailable.mjs", "browser-social.mjs"):
            evidence = unit_evidence(probe)
            evidence["cases"][0].pop(
                "lobbyOnlyWithoutCanvas" if probe == "browser-unavailable.mjs" else "canvasAbsent",
            )
            with self.subTest(probe=probe):
                self.assert_rejected(json.dumps(evidence), probe=probe)

    def test_social_case_names_still_require_coherent_actual_measurements(self):
        for mutation in ("empty", "duplicate-revision", "wrong-band", "wrong-buffer", "legacy-graph"):
            evidence = unit_evidence("browser-social.mjs")
            measured = evidence["cases"][3]["measurements"]
            if mutation == "empty":
                measured.clear()
            elif mutation == "duplicate-revision":
                measured[1]["resizeRevision"] = measured[0]["resizeRevision"]
            elif mutation == "wrong-band":
                measured[0]["handBandCssPx"] = 0
            elif mutation == "wrong-buffer":
                measured[0]["widthPx"] += 1
            else:
                evidence["moduleGraph"]["legacySourceImports"] = True
            with self.subTest(mutation=mutation):
                self.assert_rejected(json.dumps(evidence), probe="browser-social.mjs")

    def test_complete_unit_evidence_requires_zero_process_exit(self):
        for probe in ("browser-unavailable.mjs", "browser-social.mjs"):
            with self.subTest(probe=probe):
                returned, receipt = self.run_probe(json.dumps(unit_evidence(probe)), probe=probe)
                self.assertEqual(0, returned)
                self.assertEqual("passed", receipt["status"])
                self.assertEqual(len(unit_evidence(probe)["cases"]), len(receipt["browserEvidence"]["cases"]))
                self.assert_rejected(json.dumps(unit_evidence(probe)), probe=probe, code=3)

    def test_process_exception_still_records_failure_and_cleans_owned_resources(self):
        receipt = self.assert_rejected("", error=OSError("Unit-only original process failure"))
        self.assertEqual("Unit-only original process failure", receipt["failure"]["message"])

    def test_timeout_keeps_its_error_and_cleans_owned_resources(self):
        for partial in (b"Partial unit output", "Partial unit output"):
            with self.subTest(partial=partial):
                receipt = self.assert_rejected("", error=subprocess.TimeoutExpired(
                    ["docker", "run", "unit-only"], 600, output=partial,
                    stderr=partial,
                ))
                self.assertEqual(124, receipt["probeExitCode"])
                self.assertEqual("TimeoutExpired", receipt["failure"]["name"])
                self.assertEqual("browser-process", receipt["failure"]["stage"])

    def test_cleanup_failure_does_not_hide_original_evidence_error(self):
        receipt = self.assert_rejected("not JSON", stop_error=True)
        self.assertIn("JSON", receipt["failure"]["message"])
        self.assertTrue(any(
            "owned API stop failure" in entry["message"]
            for entry in receipt["secondaryFailures"]
        ))

    def test_source_metadata_failure_cannot_leave_a_success_or_omit_the_receipt(self):
        receipt = self.assert_rejected(json.dumps(unit_evidence("browser-unavailable.mjs")), metadata_error=True)
        self.assertIn("source metadata read failure", receipt["failure"]["message"])


if __name__ == "__main__":
    unittest.main()
