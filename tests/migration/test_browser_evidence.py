"""Unit-only mocked process evidence; no browser or Unity/player proof."""

import contextlib
import copy
import hashlib
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
spec = importlib.util.spec_from_file_location(
    "player_runner_under_test", ROOT / "tests/migration/run_player_integration.py",
)
player_runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(player_runner)


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
        self.source_graph = self.fixture / "renderer-module-graph.json"
        self.source_graph.write_text('{"artifactKind":"unit-only-source-graph","qualification":"mocked metadata, not browser evidence"}\n')

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
            if path.name == "renderer-module-graph.json":
                if metadata_error:
                    raise OSError("Unit-only source metadata read failure")
                return read_bytes(self.source_graph)
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
                self.assertEqual(0, returned, receipt.get("failure"))
                self.assertEqual(
                    hashlib.sha256(self.source_graph.read_bytes()).hexdigest(),
                    receipt["sourceSha256"]["src/frontend/autotable/renderer-module-graph.json"],
                )
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


class PlayerEvidenceTests(unittest.TestCase):
    def test_headful_temporary_files_use_private_tmpfs_not_the_evidence_bind_mount(self):
        for headful, temporary in ((True, "/tmp"), (False, "/probe/temp")):
            with self.subTest(headful=headful):
                environment = player_runner.browser_environment("gameplay", player_runner.IDLE_FOCUS_CASE, headful)
                self.assertEqual({
                    "HOME": "/probe/home", "XDG_CONFIG_HOME": "/probe/home/config",
                    "XDG_DATA_HOME": "/probe/home/data", "XDG_CACHE_HOME": "/probe/home/cache",
                    "TMPDIR": temporary, "TMP": temporary, "TEMP": temporary,
                    "MAHJONG_PLAYER_SCOPE": "gameplay", "MAHJONG_PLAYER_CASE": player_runner.IDLE_FOCUS_CASE,
                    "MAHJONG_PLAYER_HEADFUL": "1" if headful else "0",
                }, environment)

    def setUp(self):
        self.manifest = {"buildId": "unit-player", "unityVersion": "unit-version", "contractVersion": "1.0.0"}
        names = [
            *[f"actual-mobile-{width}-dpr{dpr}-single-touch" for width in (360, 390, 500) for dpr in (1, 2, 3)],
            "actual-mobile-canceled-gestures", "actual-mobile-chat-open-single-touch",
            "actual-mobile-landscape-single-touch", "actual-desktop-native-double-click",
            "actual-mobile-manual-pickup",
            "progressed-360x800", "progressed-390x844", "progressed-500x900",
            "progressed-portrait-chat-open", "progressed-portrait-explicit-perspective",
            "progressed-desktop-with-move-log",
        ]
        cases = []
        for name in names:
            case = {
                "name": name, "state": "ready", "instanceId": "unit-instance", "sessionId": "unit-session",
                "realReady": {**self.manifest, "type": "ready", "instanceId": "unit-instance"},
                "markers": ["true", "true"], "frameId": 1, "realFrameRenderedCount": 1, "entities": 100,
                "viewport": {"widthCssPx": 500, "heightCssPx": 900, "devicePixelRatio": 1,
                             "renderPixelRatio": 1, "widthPx": 500, "heightPx": 900},
                "pixels": {"screenshot": "unit-only.png", "quantizedColors": 20, "largestColorFraction": 0.5,
                           "boardGreenFraction": 0.3, "tileLightFraction": 0.2},
                "session": {"actorSeat": 0}, "publicTiles": 36,
                "handCentersReached": 14,
                "handTargets": [{"entityId": f"unit-tile-{i}", "slotName": f"hand.{i}@0",
                                 "x": 30 + i % 7 * 50, "y": 700 + i // 7 * 70} for i in range(14)],
            }
            if name.endswith("-single-touch") or name in ("actual-desktop-native-double-click", "actual-mobile-manual-pickup"):
                pickup = name == "actual-mobile-manual-pickup"
                case.update(
                    input="desktop-double-click" if "desktop" in name else "ordinary-single-touch",
                    nativeActions=[{"type": "action", "actionId": "unit-action", "instanceId": "unit-instance",
                                    "sessionId": "unit-session",
                                    "intent": {"kind": "takePickup" if pickup else "discard", "entityId": "unit-tile-0"}}],
                    target={"entityId": "unit-tile-0"},
                    command=["pickup", "take", {"seatIndex": 0, "count": 4}] if pickup else ["discard", 0, {"tileId": 0}],
                    serverOutcomeVerified=True, nativeWireVerified=True,
                )
            if name == "actual-mobile-canceled-gestures":
                case.update(noUnintendedCommands=True, gestures=[
                    {"kind": kind, "nativeActions": [], "noDiscardVerified": True}
                    for kind in ("moved", "canceled", "long-pressed", "multi-touch", "resized")
                ])
            cases.append(case)
        self.evidence = {
            "status": "passed", "scope": "mobile-playability", "browserVersion": "unit-test-only",
            "caseFailures": [], "pageErrors": [], "cspViolations": [], "cases": cases,
        }

    def validate(self, evidence):
        return player_runner.validate_player_evidence(evidence, self.manifest, "mobile-playability")

    def idle_evidence(self):
        case = copy.deepcopy(self.evidence["cases"][0])
        initial = {
            "timeMs": 1000, "focused": True, "visible": True, "suspended": False,
            "sessionId": "unit-session", "frameId": 3, "queuedFrameId": None, "framesSent": 1,
            "usedWasmBytes": 100000000, "linearMemoryBytes": 134217728,
        }
        paused = [{**initial, "timeMs": 2000 + n * 2000, "focused": False, "suspended": True,
                   "queuedFrameId": 301 + n * 2, "framesSent": 2} for n in range(25)]
        resumed = {**initial, "timeMs": 51000, "frameId": 350, "framesSent": 3}
        case.update(name=player_runner.IDLE_FOCUS_CASE, frameId=400, idleMemory={
            "headful": True, "focusEmulationDisabled": True, "nativeMetrics": True,
            "oldContextRetired": True, "sourceStatePreserved": True, "nativeInputRestored": True,
            "maxInFlight": 1, "pauseDurationMs": 48000, "initial": initial, "paused": paused, "resumed": resumed,
            "resumedReceipt": {"type": "frameRendered", "instanceId": "unit-instance",
                               "sessionId": "unit-session", "frameId": 350, "layoutRevision": 1},
            "lifecycle": [
                {"type": "blur", "trusted": True, "focused": False, "visible": True, "suspended": True},
                {"type": "focus", "trusted": True, "focused": True, "visible": True, "suspended": False},
            ],
        })
        case["nativeActions"][0]["contextFrameId"] = 351
        return {**self.evidence, "scope": "gameplay", "browserHeadless": False, "cases": [case]}

    def test_idle_memory_case_requires_native_heap_focus_pause_and_input_proof(self):
        evidence = self.idle_evidence()
        name = player_runner.IDLE_FOCUS_CASE
        self.assertIs(evidence, player_runner.validate_player_evidence(evidence, self.manifest, "gameplay", [name]))
        for field, value in (
            ("headful", False), ("focusEmulationDisabled", False), ("nativeMetrics", False),
            ("oldContextRetired", False), ("sourceStatePreserved", False), ("nativeInputRestored", False),
            ("maxInFlight", 2), ("pauseDurationMs", 1000), ("paused", []), ("lifecycle", []),
            ("resumedReceipt", {}),
        ):
            bad = copy.deepcopy(evidence)
            bad["cases"][0]["idleMemory"][field] = value
            with self.subTest(field=field), self.assertRaises(AssertionError):
                player_runner.validate_player_evidence(bad, self.manifest, "gameplay", [name])
        evidence["browserHeadless"] = True
        with self.assertRaises(AssertionError):
            player_runner.validate_player_evidence(evidence, self.manifest, "gameplay", [name])

    def test_idle_memory_samples_reject_runaway_native_publication_memory_and_stale_resume(self):
        for mutation in ("snapshot-traffic", "native-growth", "linear-growth", "not-visible", "not-paused",
                         "duplicate-time", "fabricated-duration", "old-context", "stale-resume", "stale-discard"):
            evidence = self.idle_evidence()
            case = evidence["cases"][0]
            idle = case["idleMemory"]
            if mutation == "snapshot-traffic":
                idle["paused"][-1]["framesSent"] += 1
            elif mutation == "native-growth":
                idle["paused"][-1].update(usedWasmBytes=150000000, linearMemoryBytes=268435456)
            elif mutation == "linear-growth":
                idle["paused"][-1]["linearMemoryBytes"] += 33 * 1048576
            elif mutation == "not-visible":
                idle["paused"][5]["visible"] = False
            elif mutation == "not-paused":
                idle["paused"][5]["frameId"] += 1
            elif mutation == "duplicate-time":
                idle["paused"][5]["timeMs"] = idle["paused"][4]["timeMs"]
            elif mutation == "fabricated-duration":
                idle["paused"][-1]["timeMs"] -= 1000
            elif mutation == "old-context":
                idle["paused"][-1]["queuedFrameId"] = idle["initial"]["frameId"] + 239
            elif mutation == "stale-resume":
                idle["resumed"]["frameId"] = idle["paused"][-1]["queuedFrameId"]
            else:
                case["nativeActions"][0]["contextFrameId"] = idle["paused"][-1]["queuedFrameId"]
            with self.subTest(mutation=mutation), self.assertRaises(AssertionError):
                player_runner.validate_player_evidence(evidence, self.manifest, "gameplay", [player_runner.IDLE_FOCUS_CASE])

    def test_idle_memory_case_rejects_untrusted_or_out_of_order_focus_events(self):
        for mutation in ("untrusted-blur", "untrusted-focus", "wrong-order"):
            evidence = self.idle_evidence()
            events = evidence["cases"][0]["idleMemory"]["lifecycle"]
            if mutation == "wrong-order":
                events.reverse()
            else:
                events[0 if mutation == "untrusted-blur" else 1]["trusted"] = False
            with self.subTest(mutation=mutation), self.assertRaises(AssertionError):
                player_runner.validate_player_evidence(evidence, self.manifest, "gameplay", [player_runner.IDLE_FOCUS_CASE])

    def test_complete_mobile_unit_inventory_is_accepted(self):
        self.assertIs(self.evidence, self.validate(self.evidence))

    def test_missing_duplicate_or_unknown_actual_case_is_rejected(self):
        for mutation in ("missing", "duplicate", "unknown"):
            evidence = copy.deepcopy(self.evidence)
            if mutation == "missing":
                evidence["cases"].pop()
            elif mutation == "duplicate":
                evidence["cases"].append(copy.deepcopy(evidence["cases"][0]))
            else:
                evidence["cases"][0]["name"] = "unexecuted"
            with self.subTest(mutation=mutation), self.assertRaises(AssertionError):
                self.validate(evidence)

    def test_single_touch_requires_one_native_target_and_exact_server_command(self):
        for mutation in ("duplicate", "wrong-kind", "wrong-input", "wrong-instance", "wrong-target",
                         "missing-command", "raw-pickup-tile", "unproven-server"):
            evidence = copy.deepcopy(self.evidence)
            case = evidence["cases"][0]
            if mutation == "duplicate":
                case["nativeActions"].append(copy.deepcopy(case["nativeActions"][0]))
            elif mutation == "wrong-kind":
                case["nativeActions"][0]["intent"]["kind"] = "select"
            elif mutation == "wrong-input":
                case["input"] = "double-touch"
            elif mutation == "wrong-instance":
                case["nativeActions"][0]["instanceId"] = "another-instance"
            elif mutation == "wrong-target":
                case["target"]["entityId"] = "another-tile"
            elif mutation == "missing-command":
                del case["command"]
            elif mutation == "raw-pickup-tile":
                case["command"][2] = {"tileIds": [0]}
            else:
                case["serverOutcomeVerified"] = False
            with self.subTest(mutation=mutation), self.assertRaises(AssertionError):
                self.validate(evidence)

    def test_reachable_hand_inventory_and_native_cancellation_cannot_be_labels_only(self):
        for mutation in ("missing-target", "duplicate-target", "offscreen", "missing-gesture",
                         "duplicate-gesture", "unproven-cancel", "native-discard"):
            evidence = copy.deepcopy(self.evidence)
            case = evidence["cases"][0]
            canceled = next(case for case in evidence["cases"] if case["name"] == "actual-mobile-canceled-gestures")
            if mutation == "missing-target":
                case["handTargets"].pop()
            elif mutation == "duplicate-target":
                case["handTargets"][1] = copy.deepcopy(case["handTargets"][0])
            elif mutation == "offscreen":
                case["handTargets"][0]["x"] = 1000
            elif mutation == "missing-gesture":
                canceled["gestures"].pop()
            elif mutation == "duplicate-gesture":
                canceled["gestures"].append(copy.deepcopy(canceled["gestures"][0]))
            elif mutation == "unproven-cancel":
                canceled["gestures"][0]["noDiscardVerified"] = False
            else:
                canceled["gestures"][0]["nativeActions"] = case["nativeActions"]
            with self.subTest(mutation=mutation), self.assertRaises(AssertionError):
                self.validate(evidence)

    def test_progressed_public_tiles_and_actual_render_proof_are_mandatory(self):
        for mutation in ("unprogressed", "wrong-build", "wrong-buffer", "flat-pixels"):
            evidence = copy.deepcopy(self.evidence)
            case = evidence["cases"][0]
            if mutation == "unprogressed":
                next(case for case in evidence["cases"] if case["name"].startswith("progressed-"))["publicTiles"] = 0
            elif mutation == "wrong-build":
                case["realReady"]["buildId"] = "unqualified"
            elif mutation == "wrong-buffer":
                case["viewport"]["widthPx"] = 1
            else:
                case["pixels"]["quantizedColors"] = 1
            with self.subTest(mutation=mutation), self.assertRaises(AssertionError):
                self.validate(evidence)

    def test_editor_receipt_supports_observed_modern_and_legacy_inventory_not_success_labels(self):
        editor = {
            "producerExitCode": 0, "manifest": {"sha256": "unit-sha"},
            "editMode": {"result": "Passed", "passed": 134, "failed": 0, "skipped": 0},
            "qualificationChecks": {"actualAllEditModePassed": True, "actualWebGlBuildReportSucceeded": True,
                                    "authorizedCommittedSourceRemainedClean": True},
        }
        player_runner.validate_editor_evidence(editor, "unit-sha")
        legacy = copy.deepcopy(editor)
        legacy["editMode"].update(passed=82, count=82, failed=[])
        del legacy["editMode"]["skipped"]
        legacy["qualificationChecks"]["actualAll82EditModePassed"] = legacy["qualificationChecks"].pop("actualAllEditModePassed")
        player_runner.validate_editor_evidence(legacy, "unit-sha")
        for section, field, value in (
            ("editMode", "passed", 0), ("editMode", "failed", 1), ("editMode", "skipped", 1),
            ("qualificationChecks", "actualAllEditModePassed", False),
            ("qualificationChecks", "authorizedCommittedSourceRemainedClean", False),
            ("manifest", "sha256", "unqualified"),
        ):
            bad = copy.deepcopy(editor)
            bad[section][field] = value
            with self.subTest(field=field), self.assertRaises(AssertionError):
                player_runner.validate_editor_evidence(bad, "unit-sha")

    def test_traditional_meld_case_requires_real_claim_and_ordinary_post_claim_discard(self):
        name = "actual-traditional-csharp-local-meld"
        case = copy.deepcopy(self.evidence["cases"][0])
        case.update(name=name, completeWallBeforeDrawing=108, localMeldTiles=3,
                    actualClaimCommand=["claim", "0", {"action": "claim", "type": "Pung"}],
                    ordinaryPostClaimTouch=True, postClaimNativeActions=case["nativeActions"],
                    handTargetsBeforeDiscard=[{"canvasReceivesInput": True, "chatCovered": False} for _ in range(11)])
        evidence = {**self.evidence, "scope": "gameplay", "cases": [case]}
        player_runner.validate_player_evidence(evidence, self.manifest, "gameplay", [name])
        for field, value in (
            ("completeWallBeforeDrawing", 0), ("localMeldTiles", 0), ("actualClaimCommand", []),
            ("ordinaryPostClaimTouch", False), ("postClaimNativeActions", []),
            ("handTargetsBeforeDiscard", [{"canvasReceivesInput": False, "chatCovered": True}]),
        ):
            bad = copy.deepcopy(evidence)
            bad["cases"][0][field] = value
            with self.subTest(field=field), self.assertRaises(AssertionError):
                player_runner.validate_player_evidence(bad, self.manifest, "gameplay", [name])

    def test_camera_cases_require_actual_three_axis_pixels_fixed_hand_reset_and_restored_native_input(self):
        name = "actual-camera-touch-orbit-zoom"
        case = copy.deepcopy(self.evidence["cases"][0])
        case.update(name=name, frameId=10, presentation={"camera": None}, cameraControls={
            "inputs": sorted(player_runner.CAMERA_CASES[name]), "renderedFrameId": 3, "resetFrameId": 6,
            "override": {"scale": 1.4, "yaw": 30, "pitch": 65, "roll": 20},
            "sourceStatePreserved": True, "noNativeCameraCommands": True, "resetRestored": True,
            "nativeInputRestored": True, "partialReleaseSafe": True,
            "beforePixels": {"screenshot": "unit-before.png", "sha256": "a" * 64, "width": 500, "height": 900},
            "movedPixels": {"screenshot": "unit-moved.png", "sha256": "b" * 64, "width": 500, "height": 900},
            "differences": [
                {"name": "board", "x": 0, "y": 0, "width": 500, "height": 400, "pixels": 200000,
                 "changedPixels": 20000, "changedFraction": 0.1},
                {"name": "hand", "x": 0, "y": 400, "width": 500, "height": 150, "pixels": 75000,
                 "changedPixels": 0, "changedFraction": 0},
            ],
        })
        evidence = {**self.evidence, "scope": "gameplay", "cases": [case]}
        player_runner.validate_player_evidence(evidence, self.manifest, "gameplay", [name])
        for field, value in (
            ("inputs", []), ("renderedFrameId", 0), ("resetFrameId", 2),
            ("override", {"scale": 1.4, "yaw": 0, "pitch": 65, "roll": 20}),
            ("noNativeCameraCommands", False), ("partialReleaseSafe", False),
            ("nativeInputRestored", False), ("sourceStatePreserved", False),
            ("differences", case["cameraControls"]["differences"][:1]),
            ("movedPixels", case["cameraControls"]["beforePixels"]),
        ):
            bad = copy.deepcopy(evidence)
            bad["cases"][0]["cameraControls"][field] = value
            with self.subTest(field=field), self.assertRaises(AssertionError):
                player_runner.validate_player_evidence(bad, self.manifest, "gameplay", [name])
        for index, count, fraction in ((0, 1000, 0.005), (1, 1000, 1000 / 75000)):
            bad = copy.deepcopy(evidence)
            bad["cases"][0]["cameraControls"]["differences"][index].update(changedPixels=count, changedFraction=fraction)
            with self.subTest(region=index), self.assertRaises(AssertionError):
                player_runner.validate_player_evidence(bad, self.manifest, "gameplay", [name])


if __name__ == "__main__":
    unittest.main()
