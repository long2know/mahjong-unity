#!/usr/bin/env python3
"""Reproducible source checks for the default browser-native application."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
FRONTEND = "src/frontend/autotable-src"
RESULTS = "artifacts/test-results/web-source"
BACKEND_PROJECT = "src/backend/tests/Mahjong.Autotable.Api.Tests/Mahjong.Autotable.Api.Tests.csproj"
BACKEND_SELECTORS = (
    ".Autotable.ViewerAuthorityContractTests", ".Autotable.ChangshaPrivacyStartupValidatorTests",
    ".Autotable.ChangshaPrivacyProjectorDelegationTests", ".Autotable.HumanClaimWireContractTests",
    ".Autotable.ResultTombstoneLifecycleTests", ".Chat.ChatMessageTests",
    ".Changsha.Acceptance.VariantSwitchAcceptanceTests", ".Hosting.BrowserStaticHostingTests",
    ".Observability.SecurityHeadersMiddlewareTests", ".Autotable.ExistingRoomJoinTests",
    ".Autotable.AutotableWsRelayTests", ".Autotable.AutotableConfigJoinSemanticsTests",
    ".Autotable.HandResultAckWsTests", ".Autotable.AutotableHandshakeAndDealTriggerContractTests",
)


def command(name, argv, cwd=".", kind=None, expected=None, dependency=False):
    return dict(name=name, argv=argv, cwd=cwd, kind=kind, expected=expected, dependency=dependency)


def plans():
    tests = ["tests/node/room-startup-readiness.test.mjs", "tests/node/hand-result-render-and-wire.test.mjs",
             "tests/node/sc2-slot-owner.invariants.test.mjs", "tests/node/perspective-settings.test.mjs",
             "tests/node/browser-shell-layout.test.mjs",
             *sorted(path.relative_to(ROOT / FRONTEND).as_posix()
                     for pattern in ("renderer-*.test.mjs", "webgl-*.test.mjs")
                     for path in (ROOT / FRONTEND / "tests/node").glob(pattern)),
             "../../../tests/migration/browser-source-integration.test.mjs"]
    return {
        "source-tools": [
            command("shell-syntax", ["bash", "-n", "build.sh", "scripts/run-web.sh", "scripts/compose-bootstrap.sh"]),
            command("build-wrappers", ["python3", "-B", "src/backend/tests/Mahjong.Autotable.Api.Tests/Hosting/test_build_wrappers.py"],
                    kind="unittest", expected=4),
            command("source-delivery", ["python3", "-B", "tests/migration/test_web_build.py"], kind="unittest", expected=4),
        ],
        "backend": [
            command("dependencies", ["dotnet", "restore", BACKEND_PROJECT, "--disable-build-servers"], dependency=True),
            command("backend-tests", ["dotnet", "test", BACKEND_PROJECT, "--no-restore", "--disable-build-servers",
                    "-p:UseSharedCompilation=false", "--filter",
                    "|".join("FullyQualifiedName~" + selector for selector in BACKEND_SELECTORS),
                    "--logger", "trx;LogFileName=backend.trx", "--results-directory", RESULTS + "/backend"],
                    kind="trx", expected=140),
            command("api-publish", ["dotnet", "publish", "src/backend/src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj",
                    "--no-restore", "--disable-build-servers", "-p:UseSharedCompilation=false", "-p:UseAppHost=false",
                    "-c", "Release", "-o", "artifacts/web-source-api"], kind="publish"),
        ],
        "frontend-source": [
            command("dependencies", ["npm", "ci", "--no-audit", "--no-fund"], cwd=FRONTEND, dependency=True),
            command("workflow-contracts", ["node", "--test", "--test-reporter=tap", "tests/migration/source-ci.test.mjs"],
                    kind="node", expected=4),
            command("frontend-tests", ["node", "--test", "--test-reporter=tap", *tests], cwd=FRONTEND, kind="node", expected=131),
            command("renderer-regressions", ["node", "--test", "--test-reporter=tap",
                    "tests/migration/renderer-backend-shape.test.mjs", "tests/migration/relay-authority-delta.test.mjs"],
                    kind="node", expected=6),
            command("typecheck", ["npm", "run", "typecheck"], cwd=FRONTEND),
            command("vite-build", ["npm", "run", "build"], cwd=FRONTEND, kind="vite"),
        ],
    }


def evidence_paths(group):
    directory = RESULTS + "/" + group
    result = [directory + "/receipt.json", *[directory + "/" + entry["name"] + ".log" for entry in plans()[group]]]
    if group == "backend":
        result.append(directory + "/backend.trx")
    if group == "frontend-source":
        result.extend(["src/frontend/autotable/renderer-module-graph.json", "src/frontend/autotable/web-build.json"])
    return result


def validate_output(entry, text, directory):
    if entry["kind"] == "node":
        counts = {key: int(value) for key, value in re.findall(r"^# (tests|pass|fail|cancelled|skipped) (\d+)$", text, re.M)}
        if counts != dict(tests=entry["expected"], **{"pass": entry["expected"]}, fail=0, cancelled=0, skipped=0):
            raise ValueError("Incomplete or failed mandatory Node inventory: " + entry["name"])
        return counts
    if entry["kind"] == "unittest":
        match = re.search(r"Ran (\d+) tests? in ", text)
        if not match or int(match[1]) != entry["expected"] or not re.search(r"^OK$", text, re.M) or "skipped=" in text:
            raise ValueError("Incomplete or failed mandatory tooling tests")
        return {"tests": entry["expected"], "passed": entry["expected"]}
    if entry["kind"] == "trx":
        counters = ET.parse(directory / "backend.trx").find(".//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters")
        counts = {key: int(counters.attrib[key]) for key in ("total", "executed", "passed", "failed", "notExecuted")}
        if counts != dict(total=entry["expected"], executed=entry["expected"], passed=entry["expected"], failed=0, notExecuted=0):
            raise ValueError("Incomplete or failed mandatory backend TRX inventory")
        return counts
    if entry["kind"] == "vite":
        build = json.loads((ROOT / "src/frontend/autotable/web-build.json").read_text())
        if build["engine"] != "webgl2" or build["artifactKind"] != "mahjong-web-build":
            raise ValueError("Default build did not produce the actual WebGL-only identity")
        return {"engine": build["engine"], "files": len(build["files"])}
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("group", choices=list(plans()), nargs="?")
    parser.add_argument("--install-dependencies", action="store_true")
    parser.add_argument("--describe", action="store_true")
    args = parser.parse_args()
    if args.describe:
        print(json.dumps({"plans": plans(), "evidencePaths": {group: evidence_paths(group) for group in plans()}}))
        return
    if not args.group:
        parser.error("Choose a group")
    directory = ROOT / RESULTS / args.group
    directory.mkdir(parents=True, exist_ok=True)
    work = ROOT / ".work"
    work.mkdir(exist_ok=True)
    environment = dict(os.environ, TMPDIR=str(work), PYTHONDONTWRITEBYTECODE="1",
                       DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    chrome = Path("/usr/bin/google-chrome")
    if chrome.exists():
        environment["CHROMIUM_EXECUTABLE_PATH"] = str(chrome)
    receipt = {"artifactKind": "mahjong-web-source-ci", "engine": "webgl2", "job": args.group, "status": "running",
               "source": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
               "recordedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "commands": [],
               "qualification": "source/tests/build only; container/browser evidence is recorded separately"}
    save = lambda: (directory / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    save()
    try:
        for entry in plans()[args.group]:
            if entry["dependency"] and not args.install_dependencies:
                continue
            log = directory / (entry["name"] + ".log")
            print(args.group + ": " + entry["name"], flush=True)
            with log.open("w") as stream:
                result = subprocess.run(entry["argv"], cwd=ROOT / entry["cwd"], env=environment,
                                        stdout=stream, stderr=subprocess.STDOUT)
            if result.returncode:
                raise ValueError("Command failed: " + str(log.relative_to(ROOT)))
            text = log.read_text()
            receipt["commands"].append({"name": entry["name"], "exitCode": result.returncode,
                "counts": validate_output(entry, text, directory), "logSha256": hashlib.sha256(log.read_bytes()).hexdigest()})
            save()
        receipt["status"] = "passed"
    except Exception as error:
        receipt["status"] = "failed"
        receipt["error"] = str(error)
        raise
    finally:
        save()


if __name__ == "__main__":
    main()
