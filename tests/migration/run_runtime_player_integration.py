#!/usr/bin/env python3
"""Run the exact genuine runtime image with isolated keys/state and actual player."""

import base64
import argparse
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid


ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "artifacts/test-results/migration-player-cycle1-runtime"
WORK = ROOT / "artifacts/test-results/migration-player-work"
sys.dont_write_bytecode = True


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    loaded = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(loaded)
    return loaded


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cases", default="")
    parser.add_argument("--output-name", default="migration-player-cycle1-runtime")
    parser.add_argument("--image", required=True, help="The freshly built exact combined runtime image/tag")
    parser.add_argument("--editor-receipt", type=Path,
                        default=ROOT / "artifacts/test-results/unity-clean-source-build/receipt.json")
    parser.add_argument("--mobile-playability", action="store_true")
    parser.add_argument("--port", type=int, default=5095)
    args = parser.parse_args()
    assert args.port != 5086, "The live user preview port is excluded from every test operation"
    global OUTPUT
    assert args.output_name.startswith("migration-player-") and "/" not in args.output_name
    OUTPUT = ROOT / "artifacts/test-results" / args.output_name
    browser_output = args.output_name + "-browser"
    OUTPUT.mkdir(parents=True, exist_ok=True)
    WORK.mkdir(parents=True, exist_ok=True)
    fixture = WORK / ("runtime-" + uuid.uuid4().hex)
    fixture.mkdir()
    (fixture / "bootstrap-cache").mkdir()
    container = "migration-player-runtime-" + uuid.uuid4().hex
    editor_path = args.editor_receipt.resolve()
    assert editor_path.is_relative_to(ROOT / "artifacts/test-results") and editor_path.suffix == ".json"
    clean_editor = json.loads(editor_path.read_text())
    image = subprocess.check_output(["docker", "image", "inspect", args.image, "--format", "{{.Id}}"],
                                    text=True, timeout=30).strip()
    receipt = {
        "artifactKind": "migration-actual-runtime-player-review",
        "recordedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "imageId": image, "status": "failed", "licensingAcceptedByActualEditor": True,
        "licenseAccountOrOperatorFilesRead": False, "ephemeralSigningValuesLogged": False,
        "sourceDirty": False, "developmentBuild": False, "cleanup": {},
        "cleanEditorReceipt": str(editor_path.relative_to(ROOT)),
        "protectedUserPreviewExcluded": {"name": "mahjong-unity-preview", "port": 5086},
    }
    stage = "exact-runtime-image"
    attempted = False
    peers = []
    code = 1
    environment = {key: os.environ[key] for key in ("PATH", "HOME", "DOCKER_HOST", "DOCKER_CONTEXT") if key in os.environ}
    environment.update({
        "Authentication__JwtSigningKeys__0": base64.b64encode(os.urandom(48)).decode(),
        "Auth__JwtSigningKeys__0": base64.b64encode(os.urandom(48)).decode(),
    })
    try:
        info = json.loads(subprocess.check_output(["docker", "image", "inspect", image], text=True, timeout=30))[0]
        assert info["Id"] == image and info["Config"]["User"] == "1000:1000"
        assert info["Config"]["Labels"]["org.mahjong-unity.allow-source-dirty"] == "0"
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", args.port))
        port = args.port
        base = f"http://127.0.0.1:{port}"
        receipt["origin"] = base
        command = [
            "docker", "create", "--name", container, "--platform", "linux/amd64",
            "--user", "1000:1000", "--read-only", "--cap-drop=ALL",
            "--security-opt=no-new-privileges:true",
            "--label", "org.mahjong-unity.qualification=owned-player-combined-clean",
            "--publish", f"127.0.0.1:{port}:8080",
            "--mount", f"type=bind,source={fixture},target=/data",
            "--mount", f"type=bind,source={fixture / 'bootstrap-cache'},target=/app/data",
            "--env", "Authentication__JwtSigningKeys__0", "--env", "Auth__JwtSigningKeys__0",
            "--env", "ASPNETCORE_ENVIRONMENT=Production",
            "--env", "ConnectionStrings__Sqlite=Data Source=/data/state.db;Pooling=False",
            "--env", "Logging__LogLevel__Default=Warning",
            "--env", "Sentry__Dsn=", image,
        ]
        receipt["createCommandWithoutSigningValues"] = command
        attempted = True
        subprocess.run(command, env=environment, check=True, text=True, capture_output=True, timeout=660)
        subprocess.run(["docker", "start", container], check=True, capture_output=True, timeout=180)
        stage = "actual-runtime-readiness"
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            try:
                with urllib.request.urlopen(base + "/health", timeout=1) as response:
                    health = json.loads(response.read())
                    if response.status == 200 and health["status"] == "healthy" and health["db"]["connected"]:
                        receipt["actualHealth"] = health
                        break
            except (urllib.error.URLError, TimeoutError, ConnectionResetError):
                time.sleep(0.25)
        else:
            raise AssertionError("The exact runtime image did not expose its actual healthy persistent API")
        inspected = json.loads(subprocess.check_output(["docker", "container", "inspect", container], text=True, timeout=30))[0]
        assert inspected["Image"] == image and inspected["Config"]["User"] == "1000:1000"
        assert inspected["HostConfig"]["ReadonlyRootfs"] and not inspected["HostConfig"]["Privileged"]
        assert inspected["HostConfig"]["CapDrop"] == ["ALL"]
        receipt["runtimeHardening"] = {
            "user": "1000:1000", "readOnlyRootfs": True, "capDrop": ["ALL"],
            "noNewPrivileges": True, "port": f"127.0.0.1:{port}:8080",
            "mountedStateOnly": ["/data", "/app/data"],
            "bootstrapCacheReason": "Retained Program.cs creates ContentRootPath/data even with an absolute SQLite connection string",
            "imageIdentityMatches": True,
        }
        (OUTPUT / "runtime-progress.json").write_text(json.dumps(receipt, indent=2) + "\n")
        probe_module = module("runtime_published_contract", ROOT / "tests/migration/test_published_integration.py")
        api = probe_module.PublishedIntegrationTests
        api.base, api.port, api.peers = base, port, peers
        api.receipt = {"requests": [], "checks": {}, "qualification": "Actual Docker-hosted native API/WS/SignalR outcomes"}
        tester = api(methodName="runTest")
        manifest_path = ROOT / "artifacts/unity-webgl/renderer-build.json"
        manifest = json.loads(manifest_path.read_text())
        manifest_sha = hashlib.sha256(manifest_path.read_bytes()).hexdigest()
        assert manifest_sha == clean_editor["manifest"]["sha256"]
        assert manifest["sourceDirty"] is False and manifest["developmentBuild"] is False
        static = []
        for path, mime, digest in [
            ("/autotable/unity/renderer-build.json", "application/json", manifest_sha),
            *[("/autotable/unity/" + manifest[purpose]["path"], mime, manifest[purpose]["sha256"])
              for purpose, mime in (("loader", "application/javascript"), ("framework", "application/javascript"),
                                    ("wasm", "application/wasm"), ("data", "application/octet-stream"))],
        ]:
            status, headers, content = tester.request(path)
            assert status == 200 and headers.get_content_type() == mime
            assert hashlib.sha256(content).hexdigest() == digest
            assert "'wasm-unsafe-eval'" in headers["Content-Security-Policy"]
            assert "'unsafe-eval'" not in headers["Content-Security-Policy"]
            assert "no-cache" in headers["Cache-Control"] and "must-revalidate" in headers["Cache-Control"]
            assert "Cross-Origin-Embedder-Policy" not in headers and "Cross-Origin-Opener-Policy" not in headers
            static.append({"path": path, "mime": mime, "bytes": len(content), "sha256": digest})
        status, headers, content = tester.request("/autotable/unity/Build/missing.wasm")
        assert status == 404 and content == b"" and headers.get_content_type() != "text/html"
        receipt["genuineStaticHashesMimesCacheCspAnd404"] = static
        receipt["manifestSha256"] = manifest_sha
        browser_assets = []
        for asset in sorted((ROOT / "src/frontend/autotable").iterdir()):
            if not asset.is_file() or (asset.suffix not in (".js", ".css") and asset.name != "index.html"):
                continue
            path = "/autotable/" + asset.name
            status, _, content = tester.request(path)
            digest = hashlib.sha256(asset.read_bytes()).hexdigest()
            assert status == 200 and hashlib.sha256(content).hexdigest() == digest, (
                "Runtime must serve the exact current combined Vite module graph: " + path
            )
            browser_assets.append({"path": path, "bytes": len(content), "sha256": digest})
        assert browser_assets and any("/scene-shell." in asset["path"] for asset in browser_assets)
        receipt["exactCurrentCombinedViteAssets"] = browser_assets
        notice = subprocess.run([
            "docker", "exec", container, "/bin/sh", "-c",
            "id -u && test -s /licenses/THIRD_PARTY_NOTICES.txt && test -s /licenses/source-provenance.json "
            "&& find /licenses/npm /licenses/nuget -type f | wc -l",
        ], check=True, capture_output=True, text=True, timeout=60)
        lines = notice.stdout.splitlines()
        assert lines[0] == "1000" and int(lines[-1]) > 0
        receipt["actualNoticeInventory"] = {"runtimeUid": 1000, "generatedNpmNugetNoticeFiles": int(lines[-1]),
                                            "topLevelNoticesAndProvenanceNonempty": True}
        (OUTPUT / "runtime-progress.json").write_text(json.dumps(receipt, indent=2) + "\n")
        stage = "actual-runtime-native-contract"
        if args.cases:
            receipt["supplementaryNativeLifecycle"] = "Not repeated in focused browser input retake; prior raw native receipts retained separately"
        else:
            try:
                tester.test_02_actual_signed_room_signalr_public_lobby_chat_and_reconnect()
            except Exception as error:
                receipt["nativeContractFailure"] = {"name": type(error).__name__, "message": str(error)[:2000]}
        receipt["nativeApiEvidence"] = api.receipt
        (OUTPUT / "runtime-progress.json").write_text(json.dumps(receipt, indent=2) + "\n")
        stage = "actual-runtime-generated-player"
        player = subprocess.run([
            "python3", "tests/migration/run_player_integration.py", "--origin", base,
            "--scope", "mobile-playability" if args.mobile_playability else "gameplay",
            "--probe", "browser-mobile-playability.mjs" if args.mobile_playability else "browser-gameplay.mjs",
            "--editor-receipt", str(editor_path),
            "--output-name", browser_output,
            "--cases", args.cases,
        ], cwd=ROOT, capture_output=True, text=True, timeout=1720 if args.mobile_playability else 1320)
        (OUTPUT / "player-run.stdout.log").write_text(player.stdout)
        (OUTPUT / "player-run.stderr.log").write_text(player.stderr)
        receipt["playerReceipt"] = "artifacts/test-results/" + browser_output + "/player-receipt.json"
        receipt["playerReturnCode"] = player.returncode
        actual_healthcheck = json.loads(subprocess.check_output([
            "docker", "container", "inspect", container, "--format", "{{json .State.Health}}",
        ], text=True, timeout=30))
        receipt["actualDockerHealthcheck"] = {
            "status": actual_healthcheck["Status"],
            "lastExitCode": actual_healthcheck["Log"][-1]["ExitCode"],
        }
        assert receipt["actualDockerHealthcheck"] == {"status": "healthy", "lastExitCode": 0}
        if player.returncode != 0:
            raise AssertionError("Actual Docker-hosted Unity gameplay failed; see the player receipt")
        if receipt.get("nativeContractFailure"):
            raise AssertionError("Actual native lifecycle contract failed; see nativeContractFailure and nativeApiEvidence")
        receipt["status"] = "passed"
        code = 0
    except Exception as error:
        receipt["failure"] = {"stage": stage, "name": type(error).__name__, "message": str(error)[:4000]}
    finally:
        for peer in peers:
            peer.close()
        if attempted:
            try:
                exists = subprocess.run(["docker", "container", "inspect", container], capture_output=True, timeout=30)
                if exists.returncode == 0:
                    result = subprocess.run(["docker", "stop", "--time", "15", container],
                                            capture_output=True, text=True, timeout=60)
                    assert result.returncode == 0
                    state = json.loads(subprocess.check_output(
                        ["docker", "container", "inspect", container], text=True, timeout=30,
                    ))[0]["State"]
                    receipt["cleanup"]["runtimeGracefulExitCode"] = state["ExitCode"]
                    assert not state["Running"]
                    receipt["cleanup"]["runtimeGracefulShutdown"] = state["ExitCode"] == 0
                    logs = subprocess.run(["docker", "logs", container], capture_output=True, text=True, timeout=60)
                    output = logs.stdout + logs.stderr
                    if any(environment[key] in output for key in ("Authentication__JwtSigningKeys__0", "Auth__JwtSigningKeys__0")):
                        raise AssertionError("Ephemeral runtime signing material appeared in logs")
                    (OUTPUT / "runtime.log").write_text(output)
                    subprocess.run(["docker", "rm", container], check=True, capture_output=True, timeout=180)
                    if receipt.get("actualHealth") and not receipt["cleanup"]["runtimeGracefulShutdown"]:
                        raise AssertionError("A qualified ready runtime did not shut down gracefully")
                receipt["cleanup"]["runtimeContainerAbsent"] = subprocess.run(
                    ["docker", "container", "inspect", container], capture_output=True, timeout=30,
                ).returncode != 0
                receipt["persistentDatabaseBeforeCleanup"] = {
                    "exists": (fixture / "state.db").is_file(),
                    "bytes": (fixture / "state.db").stat().st_size if (fixture / "state.db").is_file() else 0,
                }
                assert receipt["cleanup"]["runtimeContainerAbsent"]
            except Exception as error:
                receipt["cleanup"]["failure"] = str(error)
                code = 1
        shutil.rmtree(fixture)
        receipt["cleanup"]["runtimeFixtureRemoved"] = not fixture.exists()
        receipt["completedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        if code:
            receipt["status"] = "failed"
        (OUTPUT / "runtime-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
        print(json.dumps({"status": receipt["status"], "receipt": str(OUTPUT.relative_to(ROOT) / "runtime-receipt.json"),
                          "failure": receipt.get("failure"), "cleanup": receipt["cleanup"]}, indent=2))
    return code


if __name__ == "__main__":
    sys.exit(main())
