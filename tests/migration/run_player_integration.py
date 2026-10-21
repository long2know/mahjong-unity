#!/usr/bin/env python3
"""Qualify the genuine player against isolated Production ASP.NET and Chromium."""

import argparse
import datetime
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import uuid


ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "artifacts/test-results/migration-player-actual"
WORK = ROOT / "artifacts/test-results/migration-player-work"
IMAGE = (
    "mcr.microsoft.com/playwright:v1.60.0-noble@"
    "sha256:83192064c7510f7ee73dd63dc5f22a5e01a92c81a2e6a9c715d9e3fe55471fd9"
)
CAMERA_CASES = {
    "actual-camera-touch-orbit-zoom": {"pinch", "two-finger-orbit", "twist", "partial-release", "touch-discard", "reset-button"},
    "actual-camera-mouse-keyboard-settings": {"wheel", "alt-drag", "middle-shift-drag", "arrows", "keyboard-roll", "keyboard-zoom", "home-reset", "sliders", "legacy-reset"},
    "actual-camera-relay-input-recovery": {"wheel", "home-reset", "right-click", "native-f"},
}
IDLE_FOCUS_CASE = "actual-idle-visible-unfocused-memory"
sys.dont_write_bytecode = True


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def browser_environment(scope, cases, headful):
    temporary = "/tmp" if headful else "/probe/temp"
    return {
        "HOME": "/probe/home", "XDG_CONFIG_HOME": "/probe/home/config",
        "XDG_DATA_HOME": "/probe/home/data", "XDG_CACHE_HOME": "/probe/home/cache",
        "TMPDIR": temporary, "TMP": temporary, "TEMP": temporary,
        "MAHJONG_PLAYER_SCOPE": scope, "MAHJONG_PLAYER_CASE": cases,
        "MAHJONG_PLAYER_HEADFUL": "1" if headful else "0",
    }


def validate_editor_evidence(editor, manifest_sha):
    checks = editor["qualificationChecks"]
    tests = editor["editMode"]
    failed = tests.get("failed")
    if (type(editor["producerExitCode"]) is not int or editor["producerExitCode"] != 0
            or checks.get("actualAllEditModePassed", checks.get("actualAll82EditModePassed")) is not True
            or checks.get("actualWebGlBuildReportSucceeded") is not True
            or checks.get("authorizedCommittedSourceRemainedClean") is not True
            or editor["manifest"]["sha256"] != manifest_sha
            or tests.get("result") != "Passed"
            or type(tests.get("passed")) is not int or tests["passed"] <= 0
            or not (type(failed) is int and failed == 0 or failed == [])
            or tests.get("skipped", 0) != 0):
        raise AssertionError("Actual clean pinned licensed Editor/test/build receipt does not qualify this player")


def validate_player_evidence(evidence, manifest, scope, selected_cases=()):
    if not isinstance(evidence, dict) or evidence.get("status") != "passed":
        raise AssertionError("Actual player qualification failed: " + json.dumps(
            evidence.get("failure") if isinstance(evidence, dict) else evidence,
        ))
    if (evidence.get("scope") != scope or evidence.get("failure") is not None
            or evidence.get("caseFailures") != [] or evidence.get("pageErrors") != []
            or evidence.get("cspViolations") != [] or not isinstance(evidence.get("browserVersion"), str)
            or not evidence["browserVersion"].strip()):
        raise AssertionError("Player evidence omitted its actual scope/browser/errors inventory")
    cases = evidence.get("cases")
    if not isinstance(cases, list):
        raise AssertionError("Player evidence requires the actual rendered case inventory")
    required = {"actual-changsha-manual-csharp-rendered-player", "actual-manual-dice-server-roundtrip"}
    if scope == "all-variant-startup":
        required.add("actual-changsha-csharp-rendered-player")
        for variant in ("FOUR_PLAYER", "THREE_PLAYER", "BAMBOO", "MINEFIELD"):
            required.update({f"actual-{variant}-csharp-rendered-player",
                             f"actual-{variant}-390x844", f"actual-{variant}-844x390"})
    elif scope == "manual-layout":
        required.update({
            "manual-dpr1-390x844", "manual-dpr1-844x390", "manual-dpr1-1280x800",
            "manual-dpr1-actual-camera-control", "manual-dpr1-actual-rectangular-separate-controls",
            "actual-mobile-emulated-dpr2", "actual-mobile-emulated-dpr3",
            "actual-spectator-follow-initial-wall-privacy-only",
        })
    elif scope == "mobile-playability":
        required = {
            *{f"actual-mobile-{width}-dpr{dpr}-single-touch" for width in (360, 390, 500) for dpr in (1, 2, 3)},
            "actual-mobile-canceled-gestures", "actual-mobile-chat-open-single-touch",
            "actual-mobile-landscape-single-touch", "actual-desktop-native-double-click",
            "actual-mobile-manual-pickup",
            "progressed-360x800", "progressed-390x844", "progressed-500x900",
            "progressed-portrait-chat-open", "progressed-portrait-explicit-perspective",
            "progressed-desktop-with-move-log",
        }
    else:
        required = {
            "actual-auto-csharp-select-discard", "actual-manual-csharp-count-only-pickup-discard",
            "actual-dealt-hand-viewport-camera-controls", "actual-mobile-dpr2-touch",
            "actual-mobile-dpr3-touch", "actual-two-human-chat-spectator-privacy",
            "actual-seat-revocation-reconnect", "actual-traditional-csharp-local-meld",
            IDLE_FOCUS_CASE,
            *CAMERA_CASES,
            *{f"actual-{variant}-relay-native-input" for variant in
              ("FOUR_PLAYER", "THREE_PLAYER", "BAMBOO", "MINEFIELD")},
        }
        if selected_cases:
            required = set(selected_cases)
    names = [case.get("name") for case in cases if isinstance(case, dict)]
    if (len(names) != len(cases) or len(set(names)) != len(names)
            or (not required <= set(names) if scope == "gameplay" else set(names) != required)):
        raise AssertionError("Player evidence does not contain its complete unique actual case inventory")
    for case in cases:
        if case.get("state") != "ready":
            raise AssertionError("Player case is not actually ready: " + case["name"])
        if case["name"] == "actual-manual-dice-server-roundtrip":
            if case.get("buttonClicked") is not True or case.get("pickup", {}).get("canTake") is not True:
                raise AssertionError("The manual dice case omitted its real server pickup outcome")
            continue
        if scope in ("gameplay", "mobile-playability") and ("csharp" in case["name"] or "touch" in case["name"]
                                   or case["name"] == "actual-mobile-combined-reachability"
                                   or "relay-native-input" in case["name"]
                                   or case["name"] in CAMERA_CASES
                                   or case["name"] == IDLE_FOCUS_CASE
                                   or case["name"] in ("actual-desktop-native-double-click", "actual-mobile-manual-pickup")):
            native_actions = case.get("nativeActions", [])
            if (not isinstance(native_actions, list) or not native_actions
                    or case.get("serverOutcomeVerified") is not True or case.get("nativeWireVerified") is not True
                    or any(action.get("type") != "action" or not action.get("actionId")
                           or not action.get("intent", {}).get("kind") for action in native_actions)):
                raise AssertionError("Gameplay case omitted actual C# actions and server/wire outcome")
        if case["name"] == IDLE_FOCUS_CASE:
            idle = case.get("idleMemory")
            if (evidence.get("browserHeadless") is not False or not isinstance(idle, dict)
                    or any(idle.get(field) is not True for field in
                           ("headful", "focusEmulationDisabled", "nativeMetrics", "oldContextRetired",
                            "sourceStatePreserved", "nativeInputRestored"))
                    or type(idle.get("maxInFlight")) is not int or idle["maxInFlight"] != 1):
                raise AssertionError("Idle memory case omitted its real focus/native flow-control proof")
            initial, resumed, paused = idle.get("initial"), idle.get("resumed"), idle.get("paused")
            if not isinstance(initial, dict) or not isinstance(resumed, dict) or not isinstance(paused, list) or len(paused) < 20:
                raise AssertionError("Idle memory case requires actual before/paused/resumed samples")
            for sample in [initial, *paused, resumed]:
                if (not isinstance(sample, dict)
                        or type(sample.get("timeMs")) not in (int, float) or not math.isfinite(sample["timeMs"])
                        or not isinstance(sample.get("sessionId"), str) or not sample["sessionId"]
                        or any(type(sample.get(field)) is not int or sample[field] <= 0
                               for field in ("frameId", "usedWasmBytes", "linearMemoryBytes"))
                        or type(sample.get("framesSent")) is not int or sample["framesSent"] < 0
                        or sample["usedWasmBytes"] > sample["linearMemoryBytes"]):
                    raise AssertionError("Idle memory case omitted finite real native heap and frame counters")
            if (initial.get("focused") is not True or initial.get("visible") is not True or initial.get("suspended") is not False
                    or resumed.get("focused") is not True or resumed.get("visible") is not True or resumed.get("suspended") is not False
                    or not initial["timeMs"] < paused[0]["timeMs"] < paused[-1]["timeMs"] < resumed["timeMs"]
                    or type(idle.get("pauseDurationMs")) not in (int, float) or not math.isfinite(idle["pauseDurationMs"])
                    or idle["pauseDurationMs"] < 45000
                    or abs(idle["pauseDurationMs"] - (paused[-1]["timeMs"] - paused[0]["timeMs"])) > 0.001):
                raise AssertionError("Idle memory case did not exercise a real sustained focus pause and resume")
            for index, sample in enumerate(paused):
                if (sample.get("focused") is not False or sample.get("visible") is not True or sample.get("suspended") is not True
                        or sample["sessionId"] != initial["sessionId"]
                        or sample["framesSent"] != paused[0]["framesSent"] or sample["frameId"] != paused[0]["frameId"]
                        or index and sample["timeMs"] <= paused[index - 1]["timeMs"]
                        or sample["usedWasmBytes"] - paused[0]["usedWasmBytes"] > 32 * 1048576
                        or sample["linearMemoryBytes"] - paused[0]["linearMemoryBytes"] > 32 * 1048576):
                    raise AssertionError("Paused native frames or WASM heap grew instead of remaining bounded")
            queued = paused[-1].get("queuedFrameId")
            native_actions = case.get("nativeActions", [])
            receipt = idle.get("resumedReceipt", {})
            if (type(queued) is not int or queued - initial["frameId"] < 240
                    or resumed["sessionId"] != initial["sessionId"] or resumed["frameId"] <= queued
                    or not isinstance(receipt, dict) or receipt.get("type") != "frameRendered"
                    or receipt.get("frameId") != resumed["frameId"] or receipt.get("sessionId") != resumed["sessionId"]
                    or receipt.get("instanceId") != case.get("instanceId")
                    or [action.get("intent", {}).get("kind") for action in native_actions] != ["discard"]
                    or any(type(action.get("contextFrameId")) is not int or action["contextFrameId"] <= queued
                           or action.get("sessionId") != resumed["sessionId"] for action in native_actions)):
                raise AssertionError("Idle resume omitted its newest actual C# frame and fresh native input context")
            lifecycle = idle.get("lifecycle", [])
            if not isinstance(lifecycle, list):
                raise AssertionError("Idle memory case omitted actual trusted focus events")
            blurred = [index for index, event in enumerate(lifecycle) if isinstance(event, dict)
                       and event.get("type") == "blur" and event.get("trusted") is True
                       and event.get("focused") is False and event.get("visible") is True and event.get("suspended") is True]
            focused = [index for index, event in enumerate(lifecycle) if isinstance(event, dict)
                       and event.get("type") == "focus" and event.get("trusted") is True
                       and event.get("focused") is True and event.get("visible") is True and event.get("suspended") is False]
            if not blurred or not focused or min(blurred) >= max(focused):
                raise AssertionError("Synthetic or missing focus transitions cannot qualify the idle OOM correction")
        if case["name"] == "actual-traditional-csharp-local-meld":
            claim = case.get("actualClaimCommand", [])
            targets = case.get("handTargetsBeforeDiscard", [])
            if (case.get("completeWallBeforeDrawing") != 108 or case.get("localMeldTiles", 0) < 3
                    or case.get("ordinaryPostClaimTouch") is not True
                    or [action.get("intent", {}).get("kind") for action in case.get("postClaimNativeActions", [])] != ["discard"]
                    or not isinstance(claim, list) or len(claim) != 3 or claim[:2] != ["claim", "0"]
                    or claim[2].get("action") != "claim" or claim[2].get("type") not in ("Pung", "Chow")
                    or not targets or len(targets) > 11
                    or not all(target.get("canvasReceivesInput") is True and target.get("chatCovered") is False for target in targets)):
                raise AssertionError("Traditional layout omitted its real wall/deal/meld and ordinary post-claim touch proof")
        if case["name"] in CAMERA_CASES:
            camera = case.get("cameraControls")
            if (not isinstance(camera, dict) or not isinstance(camera.get("inputs"), list)
                    or not CAMERA_CASES[case["name"]] <= set(camera["inputs"])
                    or any(camera.get(field) is not True for field in
                           ("sourceStatePreserved", "noNativeCameraCommands", "resetRestored", "nativeInputRestored"))
                    or type(camera.get("renderedFrameId")) is not int
                    or type(camera.get("resetFrameId")) is not int
                    or not 0 < camera["renderedFrameId"] < camera["resetFrameId"] <= case.get("frameId", 0)
                    or not isinstance(case.get("presentation"), dict) or case["presentation"].get("camera") is not None):
                raise AssertionError("Camera case omitted its rendered/reset/input-isolation proof")
            view = camera.get("override")
            if (not isinstance(view, dict) or set(view) != {"scale", "yaw", "pitch", "roll"}
                    or any(type(view[field]) not in (int, float) or not math.isfinite(view[field])
                           for field in ("scale", "yaw", "roll"))
                    or not 1.05 < view["scale"] <= 2.5
                    or not -180 <= view["yaw"] <= 180 or not -180 <= view["roll"] <= 180
                    or view["pitch"] is not None and
                    (type(view["pitch"]) not in (int, float) or not math.isfinite(view["pitch"]) or not 20 <= view["pitch"] <= 90)):
                raise AssertionError("Camera case omitted its bounded actual camera override")
            if case["name"] != "actual-camera-relay-input-recovery" and (
                    view["pitch"] is None or abs(view["yaw"]) < 2 or abs(view["roll"]) < 2):
                raise AssertionError("Camera case did not exercise actual pitch, yaw and roll")
            if (case["name"] == "actual-camera-touch-orbit-zoom" and camera.get("partialReleaseSafe") is not True
                    or case["name"] == "actual-camera-mouse-keyboard-settings" and
                    (camera.get("settingsMirrored") is not True or camera.get("preferencesPersisted") is not True)):
                raise AssertionError("Camera input/settings surfaces were not actually exercised")
            before, moved = camera.get("beforePixels"), camera.get("movedPixels")
            if (not isinstance(before, dict) or not isinstance(moved, dict)
                    or not before.get("screenshot") or not moved.get("screenshot")
                    or not before.get("sha256") or before.get("sha256") == moved.get("sha256")
                    or any(type(before.get(field)) is not int or before[field] <= 0
                           or before[field] != moved.get(field) for field in ("width", "height"))):
                raise AssertionError("Camera case omitted actual before/after screenshot artifacts")
            regions = camera.get("differences")
            if (not isinstance(regions, list) or len(regions) != 2
                    or {region.get("name") for region in regions} != {"board", "hand"}):
                raise AssertionError("Camera evidence must measure the board and separate hand independently")
            for region in regions:
                if (any(type(region.get(field)) is not int for field in
                        ("x", "y", "width", "height", "pixels", "changedPixels"))
                        or region["x"] < 0 or region["y"] < 0 or region["width"] <= 0 or region["height"] <= 0
                        or region["x"] + region["width"] > before["width"] or region["y"] + region["height"] > before["height"]
                        or region["pixels"] != region["width"] * region["height"]
                        or not 0 <= region["changedPixels"] <= region["pixels"]
                        or type(region.get("changedFraction")) not in (int, float) or not math.isfinite(region["changedFraction"])
                        or abs(region["changedFraction"] - region["changedPixels"] / region["pixels"]) > 1e-9
                        or region["name"] == "board" and region["changedFraction"] <= 0.02
                        or region["name"] == "hand" and region["changedFraction"] > 0.001):
                    raise AssertionError("Camera pixel evidence did not prove a moving table and fixed hand")
        if scope == "mobile-playability":
            is_touch = case["name"].endswith("-single-touch")
            is_desktop = case["name"] == "actual-desktop-native-double-click"
            is_pickup = case["name"] == "actual-mobile-manual-pickup"
            if is_touch or is_desktop or is_pickup:
                actions = case.get("nativeActions", [])
                kind = "takePickup" if is_pickup else "discard"
                if (len(actions) != 1 or actions[0]["intent"]["kind"] != kind
                        or actions[0].get("instanceId") != case.get("instanceId")
                        or actions[0].get("sessionId") != case.get("sessionId")):
                    raise AssertionError("One native gesture must emit exactly one matching C# action")
                command = case.get("command")
                seat = case.get("session", {}).get("actorSeat")
                if not isinstance(command, list) or len(command) != 3 or not isinstance(command[2], dict):
                    raise AssertionError("The native mobile case omitted its actual server command")
                if is_pickup:
                    if (command[:2] != ["pickup", "take"] or set(command[2]) != {"seatIndex", "count"}
                            or command[2]["seatIndex"] != seat
                            or type(command[2]["count"]) is not int or command[2]["count"] <= 0):
                        raise AssertionError("Manual pickup must retain its native count-only server command")
                elif (case.get("input") != ("ordinary-single-touch" if is_touch else "desktop-double-click")
                      or case.get("target", {}).get("entityId") != actions[0]["intent"].get("entityId")
                      or command[:2] != ["discard", seat] or set(command[2]) != {"tileId"}
                      or type(command[2]["tileId"]) is not int or command[2]["tileId"] < 0):
                    raise AssertionError("The discard case omitted its ordinary input, native target or server command")
            if ("handTargets" in case
                    or case["name"].startswith(tuple(f"actual-mobile-{width}-" for width in (360, 390, 500)))
                    or case["name"] in ("progressed-360x800", "progressed-390x844", "progressed-500x900",
                                        "progressed-portrait-chat-open")):
                targets = case.get("handTargets", [])
                if (case.get("handCentersReached") != 14 or not isinstance(targets, list) or len(targets) != 14
                        or len({target.get("entityId") for target in targets}) != 14
                        or len({target.get("slotName") for target in targets}) != 14
                        or any(not target.get("entityId") or not target.get("slotName")
                               or any(type(target.get(axis)) not in (float, int)
                                      or not math.isfinite(target[axis]) or target[axis] < 0
                                      or target[axis] > case["viewport"][dimension]
                                      for axis, dimension in (("x", "widthCssPx"), ("y", "heightCssPx")))
                               for target in targets)):
                    raise AssertionError("The mobile case omitted all fourteen reachable CSS hand centers")
            if case["name"].startswith("progressed-") and case.get("publicTiles", 0) < 36:
                raise AssertionError("Public-tile readability must be captured from an actually progressed game")
            if case["name"] == "actual-mobile-canceled-gestures" and (
                    len(case.get("gestures", [])) != 5
                    or {gesture.get("kind") for gesture in case.get("gestures", [])}
                    != {"moved", "canceled", "long-pressed", "multi-touch", "resized"}
                    or not all(gesture.get("noDiscardVerified") is True
                               and isinstance(gesture.get("nativeActions"), list)
                               and not any(action.get("intent", {}).get("kind") == "discard"
                                           for action in gesture["nativeActions"])
                               for gesture in case["gestures"])
                    or case.get("noUnintendedCommands") is not True):
                raise AssertionError("The native mobile case omitted cancellation/resize guard outcomes")
        if case["name"] == "actual-mobile-combined-reachability":
            measurements = case.get("measurements", [])
            if (len(measurements) != 21 or case.get("measuredLayouts") != 21
                    or case.get("measuredHandCenters") != 294
                    or case.get("allHandCentersHitUnityCanvas") is not True
                    or case.get("chatPreservedVisible") is not True
                    or len(case.get("lowerRows", [])) != 3
                    or len(case.get("touchDiscards", [])) != 3
                    or case.get("touchscreenSingleTapDiscardQualified") is not True):
                raise AssertionError("The mobile case omitted exact all-fourteen-target/DPR/orientation/native proof")
            for measurement in measurements:
                controls = measurement.get("controls", [])
                if (len(controls) != 14 or not all(control.get("canvasReceivesInput") is True
                                                  and control.get("chatCovered") is False for control in controls)
                        or measurement.get("chrome", {}).get("display") == "none"
                        or measurement.get("chrome", {}).get("visibility") != "visible"):
                    raise AssertionError("The actual fourteen-target layout is obscured or chat was disabled")
            for lower in case["lowerRows"]:
                if (lower.get("serverOutcomeVerified") is not True
                        or lower.get("nativeWireVerified") is not True
                        or [action.get("intent", {}).get("kind") for action in lower.get("nativeActions", [])]
                        != ["discard"]):
                    raise AssertionError("A formerly blocked lower-row tile omitted its real C# server roundtrip")
        ready = case.get("realReady", {})
        if (ready.get("type") != "ready" or ready.get("buildId") != manifest["buildId"]
                or ready.get("unityVersion") != manifest["unityVersion"]
                or ready.get("contractVersion") != manifest["contractVersion"]
                or ready.get("instanceId") != case.get("instanceId")
                or case.get("markers") != ["true", "true"]
                or type(case.get("frameId")) is not int or case["frameId"] <= 0
                or type(case.get("realFrameRenderedCount")) is not int or case["realFrameRenderedCount"] <= 0
                or type(case.get("entities")) is not int or case["entities"] <= 0):
            raise AssertionError("The case omitted its matching genuine C# ready/rendered proof: " + case["name"])
        viewport = case.get("viewport", {})
        dpr = viewport.get("devicePixelRatio")
        ratio = viewport.get("renderPixelRatio")
        if (type(dpr) not in (float, int) or not math.isfinite(dpr)
                or ratio != min(dpr, 2) or dpr <= 0):
            raise AssertionError("The case omitted its actual capped viewport measurement")
        for field, css in (("widthPx", "widthCssPx"), ("heightPx", "heightCssPx")):
            value = viewport.get(css)
            if (type(value) not in (float, int) or not math.isfinite(value) or value <= 0
                    or viewport.get(field) != math.floor(value * ratio + 0.5)):
                raise AssertionError("The case has invalid measured backing dimensions")
        if case.get("duplicateReceipts", []) != []:
            raise AssertionError("The real player emitted duplicate rendered frame receipts")
        pixels = [case.get("pixels")] if "pixels" in case else [case.get("firstPixels"), case.get("followedPixels")]
        for image in pixels:
            if (not isinstance(image, dict) or type(image.get("quantizedColors")) is not int
                    or image["quantizedColors"] <= 12 or not image.get("screenshot")
                    or not 0 < image.get("largestColorFraction", 1) < 0.95
                    or not 0.01 < image.get("boardGreenFraction", 0) <= 1
                    or not 0.001 < image.get("tileLightFraction", 0) <= 1):
                raise AssertionError("The case omitted its actual nonuniform board/tile pixels")
    return evidence


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--origin", help="An already running isolated localhost Docker runtime")
    parser.add_argument("--reuse-created-container", help="Resume a specifically owned delayed VFS-created probe")
    parser.add_argument("--port", type=int, default=0, help="A specific unused localhost API port for owned-container resume")
    parser.add_argument("--editor-receipt", type=Path,
                        default=ROOT / "artifacts/test-results/unity-clean-source-build/receipt.json",
                        help="Actual clean pinned Editor/test/build receipt for this exact player")
    parser.add_argument("--output-name", default="migration-player-actual")
    parser.add_argument("--scope", choices=("all-variant-startup", "manual-layout", "gameplay", "mobile-playability"), default="all-variant-startup")
    parser.add_argument("--probe", choices=("browser-player.mjs", "browser-gameplay.mjs", "browser-mobile-playability.mjs"), default="browser-player.mjs")
    parser.add_argument("--cases", default="", help="Exact comma-separated real gameplay cases for a focused retake")
    args = parser.parse_args()
    headful = (args.probe == "browser-gameplay.mjs" and args.scope == "gameplay"
               and (not args.cases or IDLE_FOCUS_CASE in args.cases.split(",")))
    browser_entry = ["xvfb-run", "-a", "node"] if headful else ["node"]
    global OUTPUT
    if not args.output_name.startswith("migration-player-") or "/" in args.output_name:
        parser.error("Output must be a project-local migration-player-* artifact directory")
    OUTPUT = ROOT / "artifacts/test-results" / args.output_name
    OUTPUT.mkdir(parents=True, exist_ok=True)
    WORK.mkdir(parents=True, exist_ok=True)
    fixture = WORK / ("browser-" + uuid.uuid4().hex)
    container = "migration-player-" + uuid.uuid4().hex
    if args.reuse_created_container:
        container = args.reuse_created_container
        if not container.startswith("migration-player-"):
            parser.error("Only the specifically owned migration-player container may be resumed")
        info = json.loads(subprocess.check_output(
            ["docker", "container", "inspect", container], text=True, timeout=30,
        ))[0]
        config, host = info["Config"], info["HostConfig"]
        expected_origin = args.origin or f"http://127.0.0.1:{args.port}"
        if (info["State"]["Status"] != "created" or config["User"] != f"{os.getuid()}:{os.getgid()}"
                or not host["ReadonlyRootfs"] or host["Privileged"] or host["NetworkMode"] != "host"
                or "ALL" not in host["CapDrop"] or "no-new-privileges:true" not in host["SecurityOpt"]
                or config["Cmd"] != [*browser_entry, "tests/migration/" + args.probe, expected_origin]):
            raise AssertionError("The created container does not match this exact hardened owned probe")
        mounts = [mount for mount in info["Mounts"] if mount["Destination"] == "/probe"]
        if len(mounts) != 1 or not Path(mounts[0]["Source"]).is_relative_to(WORK):
            raise AssertionError("The created probe fixture is not owned project-local test state")
        fixture = Path(mounts[0]["Source"])
    fixture.mkdir(exist_ok=True)
    for name in ("home", "temp", "screenshots"):
        (fixture / name).mkdir(exist_ok=True)
    api = None
    started = attempted = False
    receipt = {
        "artifactKind": "migration-actual-player-review",
        "issue": "long2know/mahjong-unity#5",
        "recordedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "status": "failed", "image": IMAGE, "deviceEvidence": "container desktop Chromium/emulated mobile only",
        "licensingAccepted": False, "licenseEvidence": "artifacts/test-results/unity-hub-activated-build/final-qualification.json",
        "licenseOrAccountFilesRead": False, "fabricatedUnityArtifactsOrCallbacks": False,
        "processCleanup": {}, "sourceRevision": subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip(),
    }
    stage = "genuine-build-integrity"
    code = 1
    try:
        verifier = load("player_build_verifier", ROOT / "scripts/verify-unity-build.py")
        manifest_path = ROOT / "artifacts/unity-webgl/renderer-build.json"
        receipt["integrity"] = verifier.verify(manifest_path)
        receipt["manifestSha256"] = hashlib.sha256(manifest_path.read_bytes()).hexdigest()
        clean_editor = args.editor_receipt.resolve()
        if not clean_editor.is_relative_to(ROOT / "artifacts/test-results") or clean_editor.suffix != ".json":
            raise AssertionError("Editor receipt must be owned project-local test evidence")
        editor = json.loads(clean_editor.read_text())
        validate_editor_evidence(editor, receipt["manifestSha256"])
        receipt["licenseEvidence"] = str(clean_editor.relative_to(ROOT))
        receipt["licensingAccepted"] = True
        receipt["cleanEditorEvidence"] = {
            "passed": editor["editMode"]["passed"], "xmlSha256": editor["editMode"]["sha256"],
            "sourceRevision": editor["manifest"]["rendererSourceRevision"],
            "developmentBuild": False, "sourceDirty": False,
        }
        metadata = load("player_source_metadata", ROOT / "unity/MahjongRenderer/Tools/source_metadata.py")
        receipt["sourceMetadataBefore"] = metadata.metadata(ROOT)
        manifest = json.loads(manifest_path.read_text())
        for key, value in receipt["sourceMetadataBefore"].items():
            if manifest[key] != value:
                raise AssertionError("Actual source and genuine build differ at " + key)
        receipt["stagedIntegrity"] = verifier.verify(
            ROOT / "src/frontend/autotable/unity/renderer-build.json",
        )
        if receipt["stagedIntegrity"] != receipt["integrity"]:
            raise AssertionError("Staged Unity is not the exact genuine build")
        uid, gid = os.getuid(), os.getgid()
        if uid == 0:
            raise AssertionError("The real browser test must run as an unprivileged host/container user")
        lock = json.loads((ROOT / "src/frontend/autotable-src/package-lock.json").read_text())
        if lock["packages"]["node_modules/@playwright/test"]["version"] != "1.60.0":
            raise AssertionError("Playwright test package/image mismatch")
        subprocess.run(["docker", "image", "inspect", IMAGE], capture_output=True, check=True, timeout=30)
        if args.origin:
            base = args.origin
        else:
            stage = "actual-production-api-startup"
            module = load("player_published_api", ROOT / "tests/migration/test_published_integration.py")
            module.RESULTS = OUTPUT
            module.WORK = WORK
            api = module.PublishedIntegrationTests
            api.runtime_environment = "Production"
            api.build_sha = "squad5-local-" + receipt["sourceRevision"]
            api.listen_port = args.port
            api.setUpClass()
            started = True
            api.receipt["qualification"] = "Actual Production ASP.NET with the verified real Unity player, not unavailable-build-only evidence"
            base = api.base
            tester = api(methodName="runTest")
            static = []
            for path, mime, digest in [
                ("/autotable/unity/renderer-build.json", "application/json", receipt["manifestSha256"]),
                *[
                    ("/autotable/unity/" + manifest[purpose]["path"], mime, manifest[purpose]["sha256"])
                    for purpose, mime in (
                        ("loader", "application/javascript"), ("framework", "application/javascript"),
                        ("wasm", "application/wasm"), ("data", "application/octet-stream"),
                    )
                ],
            ]:
                status, headers, content = tester.request(path)
                tester.assertEqual(200, status)
                tester.assertEqual(mime, headers.get_content_type())
                tester.assertEqual(digest, hashlib.sha256(content).hexdigest())
                policy = headers["Content-Security-Policy"]
                tester.assertIn("'wasm-unsafe-eval'", policy)
                tester.assertNotIn("'unsafe-eval'", policy)
                tester.assertIn("no-cache", headers["Cache-Control"])
                tester.assertNotIn("Cross-Origin-Opener-Policy", headers)
                tester.assertNotIn("Cross-Origin-Embedder-Policy", headers)
                static.append({"path": path, "status": status, "mime": mime, "bytes": len(content), "sha256": digest})
            status, headers, content = tester.request("/autotable/unity/Build/not-a-build.wasm")
            tester.assertEqual(404, status)
            tester.assertEqual(b"", content)
            tester.assertNotEqual("text/html", headers.get_content_type())
            api.receipt["checks"]["genuineArtifactMimesHashesCacheCspAnd404"] = static
            status, _, content = tester.request("/health")
            health = json.loads(content)
            tester.assertEqual(200, status)
            tester.assertEqual("healthy", health["status"])
            tester.assertTrue(health["db"]["connected"])
            api.receipt["checks"]["actualHealthyPersistentDatabase"] = health
        receipt["origin"] = base
        receipt["browserDisplay"] = "private Xvfb (headful), not physical hardware" if headful else "headless"
        stage = "actual-browser-player"
        command = [
            "docker", "run", "--rm", "--pull=never", "--init", "--name", container,
            "--platform", "linux/amd64", "--user", f"{uid}:{gid}", "--read-only",
            "--cap-drop=ALL", "--security-opt=no-new-privileges:true", "--network=host",
            "--shm-size=256m", "--workdir=/workspace",
        ]
        if headful:
            command.extend(("--tmpfs", "/tmp:rw,nosuid,nodev,size=256m", "--memory=1536m", "--pids-limit=512"))
        for key, value in browser_environment(args.scope, args.cases, headful).items():
            command.extend(("--env", key + "=" + value))
        for source, target, readonly in (
            *[(ROOT / "tests/migration" / name, "/workspace/tests/migration/" + name, True)
              for name in (["browser-player.mjs"] if args.probe == "browser-player.mjs"
                           else ["browser-player.mjs", args.probe, "player-aim.mjs"])],
            (ROOT / "src/frontend/autotable-src/package.json", "/workspace/src/frontend/autotable-src/package.json", True),
            *[
                (ROOT / "src/frontend/autotable-src/node_modules" / package,
                 "/workspace/src/frontend/autotable-src/node_modules/" + package, True)
                for package in ("@playwright/test", "playwright", "playwright-core")
            ],
            (fixture, "/probe", False),
        ):
            command.extend(("--mount", f"type=bind,source={source},target={target}" + (",readonly" if readonly else "")))
        command.extend((IMAGE, *browser_entry, "tests/migration/" + args.probe, base))
        if args.reuse_created_container:
            command = ["docker", "start", "--attach", container]
            receipt["resumedOwnedCreatedContainer"] = True
        receipt["browserCommand"] = command
        attempted = True
        result = subprocess.run(command, cwd=ROOT, text=True, capture_output=True,
                                timeout=1600 if args.scope == "mobile-playability" else 1200 if args.scope == "gameplay" else 660)
        (OUTPUT / "browser-player.stdout.json").write_text(result.stdout)
        (OUTPUT / "browser-player.stderr.log").write_text(result.stderr)
        receipt["browserReturnCode"] = result.returncode
        browser_tools = load("player_browser_evidence", ROOT / "tests/migration/run_browser_integration.py")
        evidence = browser_tools.parse_browser_evidence(result.stdout)
        receipt["browserEvidence"] = evidence
        receipt["sourceMetadataAfter"] = metadata.metadata(ROOT)
        receipt["rendererSourceUnchanged"] = receipt["sourceMetadataAfter"] == receipt["sourceMetadataBefore"]
        if not receipt["rendererSourceUnchanged"]:
            raise AssertionError("Production renderer source changed during actual-player tests")
        validate_player_evidence(evidence, manifest, args.scope, args.cases.split(",") if args.cases else ())
        if result.returncode != 0:
            raise AssertionError("Actual browser process failed despite claiming passing player evidence")
        receipt["status"] = "passed"
        code = 0
    except Exception as error:
        receipt["failure"] = {"stage": stage, "name": type(error).__name__, "message": str(error)[:6000]}
    finally:
        if attempted:
            cleanup = load("player_browser_cleanup", ROOT / "tests/migration/run_browser_integration.py")
            try:
                cleanup.remove_owned_container(container)
                receipt["processCleanup"]["browserContainerAbsent"] = True
            except Exception as error:
                receipt["processCleanup"]["browserCleanupFailure"] = str(error)
                code = 1
        if api is not None and started:
            try:
                api.tearDownClass()
                receipt["apiEvidence"] = api.receipt
                receipt["processCleanup"]["apiProcessStopped"] = api.receipt["processStopped"]
                receipt["processCleanup"]["apiGracefulShutdown"] = api.receipt["gracefulShutdown"]
                receipt["processCleanup"]["apiFixtureRemoved"] = not api.fixture.exists()
            except Exception as error:
                receipt["processCleanup"]["apiCleanupFailure"] = str(error)
                code = 1
        for screenshot in (fixture / "screenshots").glob("*.png"):
            (OUTPUT / "screenshots").mkdir(exist_ok=True)
            shutil.copyfile(screenshot, OUTPUT / "screenshots" / screenshot.name)
        if (fixture / "wire").is_dir():
            shutil.copytree(fixture / "wire", OUTPUT / "wire", dirs_exist_ok=True)
        for evidence in fixture.glob("*-failure-state.json"):
            shutil.copyfile(evidence, OUTPUT / evidence.name)
        for evidence in fixture.glob("*-hand-input-reachability.json"):
            shutil.copyfile(evidence, OUTPUT / evidence.name)
        if (fixture / "mobile-geometry-progress.json").is_file():
            shutil.copyfile(fixture / "mobile-geometry-progress.json", OUTPUT / "mobile-geometry-progress.json")
        if (fixture / "browser-result.json").is_file():
            shutil.copyfile(fixture / "browser-result.json", OUTPUT / "browser-result.json")
        shutil.rmtree(fixture)
        receipt["processCleanup"]["browserFixtureRemoved"] = not fixture.exists()
        if code:
            receipt["status"] = "failed"
        (OUTPUT / "player-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
        print(json.dumps({
            "status": receipt["status"], "receipt": str((OUTPUT / "player-receipt.json").relative_to(ROOT)),
            "failure": receipt.get("failure"), "cleanup": receipt["processCleanup"],
        }, indent=2))
    return code


if __name__ == "__main__":
    sys.exit(main())
