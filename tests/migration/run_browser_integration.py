#!/usr/bin/env python3
"""Real browser checks in the lock-matched official image; no Unity fixture."""

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
IMAGE = (
    "mcr.microsoft.com/playwright:v1.60.0-noble@"
    "sha256:83192064c7510f7ee73dd63dc5f22a5e01a92c81a2e6a9c715d9e3fe55471fd9"
)
OUTPUT = ROOT / "artifacts/test-results/migration-browser-actual"
WORK = ROOT / "artifacts/test-results/migration-browser-work"
sys.dont_write_bytecode = True


class EvidenceFailure(ValueError):
    pass


QUALIFICATIONS = {
    "browser-unavailable.mjs": "Actual Chrome browser shell and unavailable renderer only; no Unity/player/device evidence",
    "browser-social.mjs": "Actual Chromium lobby, invitations, chat and unavailable-table viewport routing; not Unity/player/device gameplay",
}
UNAVAILABLE_CASES = ((1280, 800), (390, 844))
SOCIAL_CASES = (
    {
        "name": "lobby-only", "canvasAbsent": True,
        "bareRoomChatDisabled": True, "bothSocialShellsMounted": True,
    },
    {
        "name": "real-invitation-and-join", "sameOriginAllowlistedJoinOnlyUrl": True,
        "ownerSeat": 0, "manifest404Observed": True,
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
    },
)


def parse_browser_evidence(stdout):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise EvidenceFailure("Duplicate browser JSON property: " + key)
            result[key] = value
        return result

    def invalid_constant(value):
        raise EvidenceFailure("Non-finite browser JSON value: " + value)

    try:
        return json.loads(stdout, object_pairs_hook=unique, parse_constant=invalid_constant)
    except json.JSONDecodeError as error:
        raise EvidenceFailure("Browser process did not return valid JSON: " + str(error)) from error


def require_fields(value, expected, location):
    if type(value) is not dict:
        raise EvidenceFailure(location + " must be a JSON object")
    for key, required in expected.items():
        actual = value.get(key)
        if key not in value or type(actual) is not type(required) or actual != required:
            raise EvidenceFailure(location + "." + key + " does not contain the required result")


def validate_browser_evidence(probe, evidence):
    require_fields(evidence, {"status": "passed", "qualification": QUALIFICATIONS[probe]}, "browserEvidence")
    if evidence.get("failure") is not None:
        raise EvidenceFailure("Browser evidence contains a probe failure")
    if type(evidence.get("browserVersion")) is not str or not evidence["browserVersion"].strip():
        raise EvidenceFailure("Browser evidence is missing its actual browser version")
    cases = evidence.get("cases")
    count = len(UNAVAILABLE_CASES) if probe == "browser-unavailable.mjs" else len(SOCIAL_CASES)
    if type(cases) is not list or len(cases) != count:
        raise EvidenceFailure(f"Browser evidence requires exactly {count} actual {probe} cases")
    if probe == "browser-unavailable.mjs":
        for index, (case, (width, height)) in enumerate(zip(cases, UNAVAILABLE_CASES)):
            location = f"browserEvidence.cases[{index}]"
            require_fields(case, {
                "lobbyOnlyWithoutCanvas": True, "manifestStatus": 404,
                "visibleState": "unavailable", "primaryClientConnected": True,
                "rendererReady": False, "sceneReady": False,
                "legacyFallback": False, "pageErrors": 0,
            }, location)
            require_fields(case.get("viewport"), {"width": width, "height": height}, location + ".viewport")
        return
    for index, (case, required) in enumerate(zip(cases, SOCIAL_CASES)):
        require_fields(case, required, f"browserEvidence.cases[{index}]")
    guest = cases[1].get("guestSeat")
    if type(guest) is not int or guest == 0:
        raise EvidenceFailure("The real invitation case is missing the distinct guest seat")
    require_fields(evidence, {"pageErrors": []}, "browserEvidence")
    require_fields(evidence.get("moduleGraph"), {
        "legacySourceImports": False, "actualServedGraph": True,
    }, "browserEvidence.moduleGraph")
    modules = evidence["moduleGraph"].get("moduleCount")
    if type(modules) is not int or modules <= 0:
        raise EvidenceFailure("The social browser case is missing its actual served module count")
    measurements = cases[3].get("measurements")
    if type(measurements) is not list or len(measurements) != 3:
        raise EvidenceFailure("The viewport case requires all three actual measurements")
    previous = 0
    for index, measurement in enumerate(measurements):
        location = f"browserEvidence.cases[3].measurements[{index}]"
        require_fields(measurement, {"actorSeat": 0, "state": "unavailable"}, location)
        revision = measurement.get("resizeRevision")
        if type(revision) is not int or revision <= previous:
            raise EvidenceFailure(location + " does not have a strictly newer resize revision")
        previous = revision
        for field in ("widthCssPx", "heightCssPx", "renderPixelRatio", "handBandCssPx"):
            value = measurement.get(field)
            if type(value) not in (int, float) or not math.isfinite(value):
                raise EvidenceFailure(location + "." + field + " is not a finite number")
        width, height = measurement["widthCssPx"], measurement["heightCssPx"]
        ratio, band = measurement["renderPixelRatio"], measurement["handBandCssPx"]
        if width <= 0 or height <= 0 or not 0 < ratio <= 2 or band < 0 or (band > 0) != (width < 768):
            raise EvidenceFailure(location + " has an invalid measured viewport or local-hand band")
        require_fields(measurement, {
            "widthPx": math.floor(width * ratio + 0.5),
            "heightPx": math.floor(height * ratio + 0.5),
        }, location)
    if not (measurements[0]["widthCssPx"] < 768 <= measurements[1]["widthCssPx"]
            and measurements[1]["widthCssPx"] > measurements[1]["heightCssPx"]
            and measurements[2]["widthCssPx"] > measurements[1]["widthCssPx"]):
        raise EvidenceFailure("The viewport case does not contain the actual portrait/landscape/desktop inventory")


def record_failure(receipt, stage, error):
    failure = {"stage": stage, "name": type(error).__name__, "message": str(error)}
    if "failure" not in receipt:
        receipt["failure"] = failure
    else:
        receipt.setdefault("secondaryFailures", []).append(failure)
    receipt["status"] = "failed"


def remove_owned_container(name):
    def inspect():
        result = subprocess.run(
            ["docker", "container", "inspect", name], capture_output=True, text=True, timeout=30,
        )
        if result.returncode == 0:
            return True
        if "No such container" not in result.stderr:
            raise RuntimeError("Unable to verify removal of the owned browser container: " + result.stderr)
        return False

    if inspect():
        subprocess.run(["docker", "stop", "--time", "5", name], capture_output=True, check=True, timeout=30)
        subprocess.run(["docker", "rm", name], capture_output=True, check=True, timeout=30)
    if inspect():
        raise RuntimeError("The owned browser container remains after cleanup")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--probe", default="browser-unavailable.mjs",
                        choices=("browser-unavailable.mjs", "browser-social.mjs"))
    args = parser.parse_args()
    OUTPUT.mkdir(parents=True, exist_ok=True)
    WORK.mkdir(parents=True, exist_ok=True)
    fixture = WORK / ("container-" + uuid.uuid4().hex)
    api = None
    name = "migration-browser-" + uuid.uuid4().hex
    started = container_attempted = False
    code = 1
    stage = "prerequisites"
    receipt = {
        "artifactKind": "migration-real-browser-review",
        "issue": "long2know/mahjong-unity#5",
        "status": "failed",
        "recordedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "scope": "Real browser lobby/table-unavailable only, not Unity rendering or gameplay",
        "image": IMAGE, "imagePlatform": "linux/amd64",
        "lockVersion": "1.60.0", "licenseReadOrActivated": False,
        "unityArtifactsFabricated": False,
    }
    try:
        lock = json.loads((ROOT / "src/frontend/autotable-src/package-lock.json").read_text())
        if lock["packages"]["node_modules/@playwright/test"]["version"] != "1.60.0":
            raise ValueError("The pinned test image must match the checked-in Playwright lock version")
        subprocess.run(
            ["docker", "image", "inspect", IMAGE, "--format", "{{.Id}}"],
            check=True, capture_output=True,
        )
        receipt["sourceRevision"] = subprocess.check_output(
            ["git", "rev-parse", "--verify", "HEAD"], cwd=ROOT, text=True,
        ).strip()
        for directory in ("home", "temp"):
            (fixture / directory).mkdir(parents=True)
        spec = importlib.util.spec_from_file_location(
            "browser_published_api", ROOT / "tests/migration/test_published_integration.py",
        )
        api_module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(api_module)
        api_module.RESULTS = OUTPUT
        api_module.WORK = WORK
        api = api_module.PublishedIntegrationTests
        stage = "api-start"
        api.setUpClass()
        started = True
        command = [
            "docker", "run", "--rm", "--pull=never", "--init", "--name", name,
            "--platform", "linux/amd64", "--user", f"{os.getuid()}:{os.getgid()}",
            "--read-only", "--cap-drop=ALL", "--security-opt=no-new-privileges:true",
            "--network=host", "--shm-size=256m", "--workdir=/workspace",
            "--env", "MAHJONG_BROWSER_MODE=playwright",
            "--env", "HOME=/probe/home", "--env", "XDG_CONFIG_HOME=/probe/home/config",
            "--env", "XDG_DATA_HOME=/probe/home/data", "--env", "XDG_CACHE_HOME=/probe/home/cache",
            "--env", "TMPDIR=/probe/temp", "--env", "TMP=/probe/temp", "--env", "TEMP=/probe/temp",
        ]
        for source, target, readonly in (
            (ROOT / "tests/migration" / args.probe, "/workspace/tests/migration/" + args.probe, True),
            (ROOT / "src/frontend/autotable-src/package.json", "/workspace/src/frontend/autotable-src/package.json", True),
            (ROOT / "src/frontend/autotable-src/node_modules/@playwright/test", "/workspace/src/frontend/autotable-src/node_modules/@playwright/test", True),
            (ROOT / "src/frontend/autotable-src/node_modules/playwright", "/workspace/src/frontend/autotable-src/node_modules/playwright", True),
            (ROOT / "src/frontend/autotable-src/node_modules/playwright-core", "/workspace/src/frontend/autotable-src/node_modules/playwright-core", True),
            (fixture, "/probe", False),
        ):
            value = f"type=bind,source={source},target={target}"
            command.extend(("--mount", value + (",readonly" if readonly else "")))
        command.extend((IMAGE, "node", "tests/migration/" + args.probe, api.base))
        receipt["command"] = command
        receipt["mountedPathsOnly"] = [
            "selected tests/migration browser probe only (read-only)",
            "src/frontend/autotable-src/package.json (read-only)",
            "locked @playwright/test, playwright, playwright-core packages only (read-only)",
            "owned project-local browser home/temp fixture (writable)",
        ]
        receipt["apiBoundTo"] = "isolated 127.0.0.1 ephemeral port"
        stage = "browser-process"
        container_attempted = True
        timed_out = None
        try:
            result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=600)
            code = result.returncode
            stdout, stderr = result.stdout, result.stderr
        except subprocess.TimeoutExpired as error:
            code = 124
            timed_out = error
            stdout = error.stdout or ""
            stderr = error.stderr or ""
            if isinstance(stdout, bytes):
                stdout = stdout.decode(errors="replace")
            if isinstance(stderr, bytes):
                stderr = stderr.decode(errors="replace")
            stderr += "\nOwned browser probe timeout\n"
        prefix = args.probe.removesuffix(".mjs")
        (OUTPUT / (prefix + ".log")).write_text(stdout + stderr)
        receipt["probeExitCode"] = code
        print(stdout[-5000:])
        if stderr:
            print(stderr[-2500:], file=sys.stderr)
        if timed_out is not None:
            raise timed_out
        stage = "browser-evidence"
        receipt["browserEvidence"] = parse_browser_evidence(stdout)
        api.receipt["checks"]["realBrowser"] = receipt["browserEvidence"]
        validate_browser_evidence(args.probe, receipt["browserEvidence"])
        if code != 0:
            raise EvidenceFailure(f"Browser process exited with {code}; its evidence cannot pass")
        receipt["status"] = "passed"
    except Exception as error:
        record_failure(receipt, stage, error)
        print(f"Browser integration failed ({stage}): {error}", file=sys.stderr)
    finally:
        try:
            if started:
                api.stop()
                receipt["apiStopped"] = api.receipt["processStopped"]
                receipt["apiGracefulShutdown"] = api.receipt["gracefulShutdown"]
                if not receipt["apiStopped"] or not receipt["apiGracefulShutdown"]:
                    raise RuntimeError("The owned published API did not stop gracefully")
        except Exception as error:
            record_failure(receipt, "api-cleanup", error)
        if api is not None:
            try:
                process = getattr(api, "process", None)
                if process is not None and process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)
                log = getattr(api, "log", None)
                if log is not None and not log.closed:
                    log.close()
                api_fixture = getattr(api, "fixture", None)
                if api_fixture is not None and api_fixture.exists():
                    if api_fixture.parent != WORK or not api_fixture.name.startswith("published-"):
                        raise RuntimeError("Refusing to remove an unowned published API fixture")
                    shutil.rmtree(api_fixture)
                if process is not None:
                    receipt["apiStopped"] = process.poll() is not None
            except Exception as error:
                record_failure(receipt, "api-cleanup", error)
        # A uniquely named container is the only container eligible for cleanup.
        receipt["ownedContainerRemoved"] = False
        try:
            if container_attempted:
                remove_owned_container(name)
            receipt["ownedContainerRemoved"] = True
        except Exception as error:
            record_failure(receipt, "container-cleanup", error)
        receipt["ownedBrowserFixtureRemoved"] = False
        try:
            if fixture.exists():
                shutil.rmtree(fixture)
            receipt["ownedBrowserFixtureRemoved"] = True
        except Exception as error:
            record_failure(receipt, "fixture-cleanup", error)
        receipt["sourceSha256"] = {}
        for path in (
            "tests/migration/run_browser_integration.py",
            "tests/migration/" + args.probe,
            "tests/migration/test_published_integration.py",
            "src/frontend/autotable-src/package-lock.json",
            "src/frontend/autotable/renderer-module-graph.json",
        ):
            try:
                receipt["sourceSha256"][path] = hashlib.sha256((ROOT / path).read_bytes()).hexdigest()
            except Exception as error:
                record_failure(receipt, "source-metadata", error)
        (OUTPUT / (args.probe.removesuffix(".mjs") + "-receipt.json")).write_text(
            json.dumps(receipt, indent=2) + "\n",
        )
    return 0 if receipt["status"] == "passed" else code if code > 0 else 1


if __name__ == "__main__":
    sys.exit(main())
