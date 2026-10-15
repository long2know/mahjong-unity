#!/usr/bin/env python3
"""Portable, fail-closed source checks and evidence; never run a Unity Editor."""

import argparse
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
RESULTS = "artifacts/test-results/migration-ci"
WORK = "artifacts/test-results/migration-work"
PIN = "d10d351e543aa3e341960de9800828b6c0662a82"
BASELINE_URL = "https://github.com/long2know/mahjong-autotable.git"
FRONTEND = "src/frontend/autotable-src"
BACKEND_PROJECT = "src/backend/tests/Mahjong.Autotable.Api.Tests/Mahjong.Autotable.Api.Tests.csproj"
SYNTAX_PROJECT = "unity/MahjongRenderer/Tools/CSharpSyntax/CSharpSyntax.csproj"
BACKEND_SELECTORS = (
    ".Autotable.ViewerAuthorityContractTests",
    ".Autotable.ChangshaPrivacyStartupValidatorTests",
    ".Autotable.ChangshaPrivacyProjectorDelegationTests",
    ".Autotable.HumanClaimWireContractTests",
    ".Autotable.ResultTombstoneLifecycleTests",
    ".Chat.ChatMessageTests",
    ".Changsha.Acceptance.VariantSwitchAcceptanceTests",
    ".Hosting.UnityStaticHostingTests",
    ".Observability.SecurityHeadersMiddlewareTests",
    ".Autotable.ExistingRoomJoinTests",
    ".Autotable.AutotableWsRelayTests",
    ".Autotable.AutotableConfigJoinSemanticsTests",
    ".Autotable.HandResultAckWsTests",
    ".Autotable.AutotableHandshakeAndDealTriggerContractTests",
)
sys.dont_write_bytecode = True


def command(name, argv, *, cwd=".", kind=None, expected=None, dependency=False):
    return dict(name=name, argv=argv, cwd=cwd, kind=kind, expected=expected, dependency=dependency)


def plans():
    frontend_tests = [
        "tests/node/room-startup-readiness.test.mjs",
        "tests/node/hand-result-render-and-wire.test.mjs",
        "tests/node/sc2-slot-owner.invariants.test.mjs",
        "tests/node/perspective-settings.test.mjs",
        *sorted(path.relative_to(ROOT / FRONTEND).as_posix()
                for path in (ROOT / FRONTEND / "tests/node").glob("unity-*.test.mjs")),
        "../../../tests/migration/browser-source-integration.test.mjs",
    ]
    syntax_properties = [
        "-p:UseSharedCompilation=false",
        "-p:BaseIntermediateOutputPath={work}/syntax-obj/",
    ]
    return {
        "source-tools": [
            command("shell-syntax", [
                "bash", "-n", "build.sh", "scripts/build-unity-docker.sh",
                "docker/unity-build-entrypoint.sh", "scripts/compose-bootstrap.sh",
                "src/backend/tools/collect-nuget-notices.sh",
            ]),
            command("baseline-init", ["git", "init", "--quiet", "--bare", "{work}/baseline.git"]),
            command("baseline-fetch", [
                "git", "-c", "credential.helper=", "-c", "core.askPass=",
                "--git-dir", "{work}/baseline.git", "fetch", "--quiet", "--no-tags",
                "--depth=1", BASELINE_URL, PIN,
            ]),
            command("baseline-proof", [
                "git", "--git-dir", "{work}/baseline.git", "rev-parse", "--verify", PIN + "^{commit}",
            ], kind="baseline"),
            command("backend-tools", ["python3", "tests/migration/run_backend_tools.py"],
                    kind="unittest", expected=33),
            command("source-gates", ["python3", "tests/migration/test_source_gate.py", "-v"],
                    kind="unittest", expected=9),
            command("player-packaging", ["python3", "tests/migration/test_player_packaging.py", "-v"],
                    kind="unittest", expected=1),
            command("missing-unity-artifact", [
                "docker", "build", "--target", "unity-artifacts-check", "--progress", "plain", ".",
            ], kind="missing-unity"),
        ],
        "backend": [
            command("dependencies", [
                "dotnet", "restore", BACKEND_PROJECT, "--disable-build-servers",
            ], dependency=True),
            command("backend-tests", [
                "dotnet", "test", BACKEND_PROJECT, "--no-restore", "--disable-build-servers",
                "-p:UseSharedCompilation=false", "--filter",
                "|".join("FullyQualifiedName~" + selector for selector in BACKEND_SELECTORS),
                "--logger", "trx;LogFileName=backend.trx",
                "--results-directory", RESULTS + "/backend",
            ], kind="trx", expected=147),
            command("api-publish", [
                "dotnet", "publish", "src/backend/src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj",
                "--no-restore", "--disable-build-servers", "-p:UseSharedCompilation=false",
                "-p:UseAppHost=false", "-c", "Release", "-o", "{work}/api",
            ], kind="publish"),
        ],
        "frontend-source": [
            command("dependencies", ["npm", "ci", "--no-audit", "--no-fund"],
                    cwd=FRONTEND, dependency=True),
            command("workflow-contracts", [
                "node", "--test", "--test-reporter=tap", "tests/migration/source-ci.test.mjs",
            ], kind="node", expected=12),
            command("frontend-tests", ["node", "--test", "--test-reporter=tap", *frontend_tests],
                    cwd=FRONTEND, kind="node", expected=85),
            command("renderer-regressions", [
                "node", "--test", "--test-reporter=tap",
                "tests/migration/renderer-backend-shape.test.mjs",
                "tests/migration/relay-authority-delta.test.mjs",
            ], kind="node", expected=6),
            command("typecheck", ["npm", "run", "typecheck"], cwd=FRONTEND),
            command("vite-build", ["npm", "run", "build"], cwd=FRONTEND, kind="vite"),
        ],
        "unity-source": [
            command("asset-metadata", ["python3", "unity/MahjongRenderer/Tools/asset_metadata.py"]),
            command("source-contract", [
                "python3", "unity/MahjongRenderer/Tools/check-source.py",
                "--frozen-contract", "contracts/migration-contract.json",
            ]),
            command("bridge-tests", [
                "node", "--test", "--test-reporter=tap", "unity/MahjongRenderer/Tools/bridge-source.test.mjs",
            ], kind="node", expected=5),
            command("dependencies", [
                "dotnet", "restore", SYNTAX_PROJECT, "--disable-build-servers", *syntax_properties,
            ], dependency=True),
            command("syntax-build", [
                "dotnet", "build", SYNTAX_PROJECT, "--no-restore", "--disable-build-servers",
                *syntax_properties, "-o", "{work}/syntax-bin",
            ]),
            command("csharp-syntax", [
                "dotnet", "{work}/syntax-bin/CSharpSyntax.dll", "unity/MahjongRenderer",
            ], kind="syntax", expected=26),
        ],
    }


TOOLS = {
    "source-tools": ("git", "python3", "bash", "docker", "pwsh", "node"),
    "backend": ("git", "dotnet"),
    "frontend-source": ("git", "python3", "node", "npm"),
    "unity-source": ("git", "python3", "node", "dotnet"),
}


def evidence_paths(group):
    directory = RESULTS + "/" + group
    paths = [directory + "/receipt.json", directory + "/prerequisites.log"]
    paths += [directory + "/" + item["name"] + ".log" for item in plans()[group]]
    if group == "backend":
        paths.append(directory + "/backend.trx")
    if group == "frontend-source":
        paths.append("src/frontend/autotable/renderer-module-graph.json")
    return paths


def validate_output(item, output):
    kind = item["kind"]
    if kind == "unittest":
        skipped = re.search(r"skipped=(\d+)", output)
        if skipped and int(skipped[1]):
            raise ValueError("Mandatory source tests skipped; missing prerequisites are failures.")
        match = re.search(r"Ran (\d+) tests? in ", output)
        if not match or int(match[1]) != item["expected"] or not re.search(r"^OK$", output, re.M):
            raise ValueError("Missing/wrong unittest inventory or unsuccessful suite.")
        return dict(tests=int(match[1]), passed=int(match[1]), skipped=0)
    if kind == "node":
        counts = {key: int(value) for key, value in re.findall(
            r"^# (tests|pass|fail|cancelled|skipped) (\d+)$", output, re.M,
        )}
        expected = {"tests": item["expected"], "pass": item["expected"],
                    "fail": 0, "cancelled": 0, "skipped": 0}
        if counts != expected:
            raise ValueError("Missing/wrong Node test inventory, failure, cancellation or skip.")
        return counts
    if kind == "baseline" and output.strip() != PIN:
        raise ValueError("Public git object store does not contain the exact pinned commit.")
    if kind == "syntax":
        match = re.search(r"Source-only C#9 syntax: (\d+) actual source files, editor/player branches, (\d+) errors", output)
        if not match or int(match[1]) != item["expected"] or int(match[2]) != 0:
            raise ValueError("Incomplete or failed actual C#9 editor/player source parsing.")
        return dict(sourceFiles=int(match[1]), configurations=2, syntaxErrors=0)
    return None


def file_evidence(path):
    path = Path(path)
    if path.is_symlink() or not path.is_file() or not path.stat().st_size:
        raise ValueError("Missing, empty or linked evidence: " + str(path))
    return dict(path=path.relative_to(ROOT).as_posix(), bytes=path.stat().st_size,
                sha256=hashlib.sha256(path.read_bytes()).hexdigest())


class SourceRun:
    def __init__(self, group, install, results):
        self.group = group
        self.install = install
        self.results = results
        self.work = ROOT / WORK / ("source-ci-" + group + "-" + uuid.uuid4().hex)
        self.work.mkdir(parents=True)
        self.environment = dict(os.environ)
        for key in list(self.environment):
            if key.startswith("UNITY_LICENSE") or key in ("UNITY_EDITOR", "ALLOW_SOURCE_DIRTY"):
                del self.environment[key]
        self.environment.update({
            "TMPDIR": str(self.work), "TMP": str(self.work), "TEMP": str(self.work),
            "PYTHONDONTWRITEBYTECODE": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1", "DOTNET_NOLOGO": "1",
            "GIT_TERMINAL_PROMPT": "0", "GIT_ASKPASS": "", "SSH_ASKPASS": "",
            "MIGRATION_BASELINE_GIT_STORE": str(self.work / "baseline.git"),
        })
        self.receipt = {
            "artifactKind": "migration-source-ci", "issue": "long2know/mahjong-unity#3",
            "scope": "SOURCE-ONLY", "job": group, "status": "running",
            "recordedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "unityEditorApiShaderEditModeWebGlQualification": False,
            "browserDeviceQualification": "not-run",
            "dependencyInstallationRequested": install,
            "baselineRevision": PIN, "commands": [], "producedArtifacts": [],
            "toolVersions": {},
            "declaredEvidencePaths": [
                path.replace(RESULTS + "/" + group, results.relative_to(ROOT).as_posix())
                for path in evidence_paths(group)
            ],
        }
        self.save()

    def collect_sources(self):
        source = [
            ".github/workflows/migration-build.yml", "tests/migration/run_source_ci.py",
            "tests/migration/source-ci.test.mjs", "contracts/migration-contract.json",
            "contracts/renderer-v1.schema.json", "contracts/source-provenance.json",
        ]
        source += {
            "source-tools": [
                "tests/migration/run_backend_tools.py", "tests/migration/test_source_gate.py",
                "tests/migration/test_player_packaging.py",
                FRONTEND + "/src/renderer/renderer-contract.ts",
                "scripts/import-baseline.py", "scripts/verify-unity-build.py",
                ".gitignore", ".dockerignore", "Dockerfile",
                *[p.relative_to(ROOT).as_posix() for p in
                  (ROOT / "src/backend/tests/Mahjong.Autotable.Api.Tests/Hosting").glob("test_*.py")],
            ],
            "backend": [BACKEND_PROJECT, "src/backend/src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj"],
            "frontend-source": [
                FRONTEND + "/package.json", FRONTEND + "/package-lock.json",
                FRONTEND + "/tsconfig.json", FRONTEND + "/vite.config.ts",
                "tests/migration/browser-source-integration.test.mjs",
                "tests/migration/renderer-backend-shape.test.mjs",
                "tests/migration/relay-authority-delta.test.mjs",
                FRONTEND + "/tests/node/unity-test-support.mjs",
                FRONTEND + "/src/client.ts", FRONTEND + "/src/world.ts", FRONTEND + "/src/types.ts",
                FRONTEND + "/src/renderer/renderer-frame.ts",
                FRONTEND + "/src/renderer/renderer-sink.ts",
                FRONTEND + "/src/renderer/renderer-contract.ts",
            ],
            "unity-source": [SYNTAX_PROJECT, "unity/MahjongRenderer/Tools/CSharpSyntax/Program.cs"],
        }[self.group]
        if self.group == "unity-source":
            spec = importlib.util.spec_from_file_location(
                "ci_source_metadata", ROOT / "unity/MahjongRenderer/Tools/source_metadata.py",
            )
            metadata = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(metadata)
            renderer = metadata.source_files(ROOT)
            source += renderer
            self.receipt["rendererInventory"] = {
                "projectSourceFiles": len(renderer) - 1, "inputsIncludingProducer": len(renderer),
                "assetMetaFiles": sum(path.endswith(".meta") for path in renderer),
            }
        self.receipt["sourceSha256"] = {
            path: file_evidence(ROOT / path)["sha256"] for path in sorted(set(source))
        }
        self.save()

    def save(self):
        (self.results / "receipt.json").write_text(json.dumps(self.receipt, indent=2) + "\n")

    def execute(self, item):
        argv = [value.replace("{work}", str(self.work)).replace(
            RESULTS + "/" + self.group, self.results.relative_to(ROOT).as_posix(),
        ) for value in item["argv"]]
        log = self.results / (item["name"] + ".log")
        record = dict(name=item["name"], argv=argv, cwd=item["cwd"], status="running",
                      evidence=log.relative_to(ROOT).as_posix())
        self.receipt["commands"].append(record)
        self.save()
        print("Source CI: " + item["name"], flush=True)
        with log.open("w") as stream:
            result = subprocess.run(argv, cwd=ROOT / item["cwd"], env=self.environment,
                                    stdout=stream, stderr=subprocess.STDOUT, check=False)
        record["exitCode"] = result.returncode
        output = log.read_text()
        try:
            if item["kind"] == "missing-unity":
                if result.returncode == 0 or not all(token in output for token in (
                    "Unity build unavailable: missing manifest", "bash scripts/build-unity-webgl.sh",
                )):
                    raise ValueError("Docker did not fail at the actual missing Unity manifest gate.")
                record["expectedFailure"] = True
            elif result.returncode:
                raise ValueError("Command failed; inspect " + record["evidence"])
            record["counts"] = validate_output(item, output)
            if item["kind"] == "baseline":
                self.receipt["baselineGitStore"] = dict(
                    path=(self.work / "baseline.git").relative_to(ROOT).as_posix(),
                    revision=output.strip(), provisioning="public-exact-SHA-fetch", verified=True, retained=False,
                )
            if item["kind"] == "trx":
                counters = ET.parse(self.results / "backend.trx").find(
                    ".//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters",
                )
                counts = {key: int(counters.attrib[key]) for key in (
                    "total", "executed", "passed", "failed", "notExecuted",
                )}
                if counts != dict(total=item["expected"], executed=item["expected"],
                                  passed=item["expected"], failed=0, notExecuted=0):
                    raise ValueError("TRX does not prove every selected backend test passed without skips.")
                record["counts"] = counts
            if item["kind"] == "publish":
                artifact = file_evidence(self.work / "api/Mahjong.Autotable.Api.dll")
                artifact.update(qualification="actual-API-Release-publish-not-Unity", retained=False)
                self.receipt["producedArtifacts"].append(artifact)
            if item["kind"] == "vite":
                graph_path = ROOT / "src/frontend/autotable/renderer-module-graph.json"
                graph = json.loads(graph_path.read_text())
                if graph["artifactKind"] != "browser-source-module-graph" or graph["qualification"] != "source-only" or not graph["modules"]:
                    raise ValueError("Vite did not produce its actual source-only primary module graph.")
                self.receipt["producedArtifacts"].append(file_evidence(graph_path))
            record["status"] = "passed"
        except (ValueError, OSError, KeyError, AttributeError, ET.ParseError) as error:
            record["status"] = "failed"
            record["error"] = str(error)
            print(output[-6000:], file=sys.stderr)
            raise
        finally:
            self.save()

    def run(self):
        log = self.results / "prerequisites.log"
        with log.open("w") as stream:
            self.collect_sources()
            for tool in TOOLS[self.group]:
                if not shutil.which(tool):
                    stream.write("Required source-CI executable unavailable: " + tool + "\n")
                    raise ValueError("Required source-CI executable unavailable: " + tool)
                result = subprocess.run([tool, "--version"], env=self.environment,
                                        capture_output=True, text=True, check=False)
                stream.write(result.stdout + result.stderr)
                if result.returncode:
                    raise ValueError("Required tool cannot execute: " + tool)
                self.receipt["toolVersions"][tool] = result.stdout.strip()
            if "dotnet" in TOOLS[self.group]:
                version = subprocess.check_output(["dotnet", "--version"], env=self.environment, text=True).strip()
                if not version.startswith("10."):
                    raise ValueError("The existing C# source checks require the .NET 10 SDK.")
            if self.group == "source-tools":
                for argv in (
                    ["docker", "version", "--format", "{{.Server.Version}}"],
                    ["docker", "compose", "version"], ["docker", "buildx", "version"],
                ):
                    result = subprocess.run(argv, env=self.environment, stdout=stream, stderr=subprocess.STDOUT)
                    if result.returncode:
                        raise ValueError("Actual Docker daemon, Compose and Buildx are required; no context-test skips.")
        self.receipt["sourceRevision"] = subprocess.check_output(
            ["git", "rev-parse", "--verify", "HEAD"], cwd=ROOT, text=True, env=self.environment,
        ).strip()
        self.receipt["sourceDirty"] = bool(subprocess.check_output(
            ["git", "status", "--porcelain", "--untracked-files=all", "--", *self.receipt["sourceSha256"]],
            cwd=ROOT, env=self.environment,
        ))
        for item in plans()[self.group]:
            if not item["dependency"] or self.install:
                self.execute(item)
        self.receipt["status"] = "passed"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("group", nargs="?", choices=TOOLS)
    parser.add_argument("--install-dependencies", action="store_true",
                        help="CI clean checkout: npm ci / restore the existing .NET manifests.")
    parser.add_argument("--results-dir", type=Path, help="Owned evidence directory below artifacts/test-results.")
    parser.add_argument("--describe", action="store_true")
    args = parser.parse_args()
    if args.describe:
        print(json.dumps(dict(plans=plans(), tools=TOOLS, baselineRevision=PIN,
                              evidencePaths={group: evidence_paths(group) for group in TOOLS})))
        return 0
    if not args.group:
        parser.error("Pass a source job name.")
    results = ROOT / (args.results_dir or Path(RESULTS) / args.group)
    if results.is_symlink() or not results.resolve().is_relative_to(ROOT / "artifacts/test-results"):
        parser.error("Evidence must stay in an owned, project-local artifacts/test-results directory.")
    results.mkdir(parents=True, exist_ok=True)
    run = None
    try:
        run = SourceRun(args.group, args.install_dependencies, results)
        run.run()
        print("PASS: " + args.group + " (source-only; no Unity/browser/device qualification).")
        return 0
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        if run:
            run.receipt.update(status="failed", error=str(error))
        print("Source CI FAILED: " + str(error), file=sys.stderr)
        return 1
    finally:
        if run:
            run.save()
            shutil.rmtree(run.work)


if __name__ == "__main__":
    sys.exit(main())
