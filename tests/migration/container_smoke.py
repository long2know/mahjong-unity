#!/usr/bin/env python3
"""Build-independent smoke of one exact locally loaded WebGL runtime image."""
import argparse
import base64
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "artifacts/test-results/web-container"


def command(*args, **kwargs):
    return subprocess.check_output(args, text=True, **kwargs).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True)
    parser.add_argument("--source-sha", required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"[a-f0-9]{40}", args.source_sha):
        raise ValueError("An exact clean source SHA is required.")
    OUT.mkdir(parents=True, exist_ok=True)
    name = "mahjong-web-smoke-" + uuid.uuid4().hex
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    assert port != 5086
    base = f"http://127.0.0.1:{port}"
    receipt = {"artifactKind": "mahjong-web-container-runtime", "status": "running", "source": args.source_sha,
               "image": args.image, "origin": base, "requests": [], "qualification": "real Docker build/runtime/static smoke; browser gameplay separate",
               "recordedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat()}
    save = lambda: (OUT / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    environment = dict(os.environ, Authentication__JwtSigningKeys__0=base64.b64encode(os.urandom(48)).decode(),
                       Auth__JwtSigningKeys__0=base64.b64encode(os.urandom(48)).decode())
    attempted = False

    def request(path):
        try:
            response = urllib.request.urlopen(base + path, timeout=5)
        except urllib.error.HTTPError as error:
            response = error
        data = response.read()
        receipt["requests"].append({"path": path, "status": response.status, "bytes": len(data),
                                    "sha256": hashlib.sha256(data).hexdigest(),
                                    "contentType": response.headers.get_content_type()})
        return response.status, response.headers, data
    save()
    try:
        identity = json.loads(command("docker", "image", "inspect", args.image))[0]
        assert identity["Config"]["User"] == "1000:1000"
        assert identity["Config"]["Labels"]["org.mahjong-web.engine"] == "webgl2"
        assert identity["Config"]["Labels"]["org.opencontainers.image.revision"] == args.source_sha
        receipt["imageId"] = identity["Id"]
        attempted = True
        command("docker", "run", "--detach", "--name", name, "--user", "1000:1000", "--read-only",
                "--cap-drop=ALL", "--security-opt=no-new-privileges:true",
                "--publish", f"127.0.0.1:{port}:8080",
                "--tmpfs", "/data:rw,uid=1000,gid=1000,mode=700",
                "--tmpfs", "/app/data:rw,uid=1000,gid=1000,mode=700",
                "--env", "Authentication__JwtSigningKeys__0", "--env", "Auth__JwtSigningKeys__0",
                "--env", "Sentry__Dsn=", "--env", "Logging__LogLevel__Default=Warning", args.image, env=environment)
        for _ in range(120):
            try:
                status, _, data = request("/health")
                health = json.loads(data)
                if status == 200 and health["status"] == "healthy" and health["db"]["connected"]:
                    assert health["buildSha"] == args.source_sha
                    receipt["health"] = health
                    break
            except (OSError, ValueError):
                time.sleep(.5)
        else:
            raise ValueError("Synthetic hardened container never became healthy.")
        info = json.loads(command("docker", "inspect", name))[0]
        assert info["Image"] == identity["Id"] and info["HostConfig"]["ReadonlyRootfs"]
        assert info["HostConfig"]["CapDrop"] == ["ALL"]
        assert "no-new-privileges:true" in info["HostConfig"]["SecurityOpt"]
        receipt["hardening"] = {"user": "1000:1000", "readOnly": True, "capDrop": ["ALL"],
                                "noNewPrivileges": True, "syntheticEphemeralStateOnly": True}
        status, headers, body = request("/autotable/web-build.json")
        build = json.loads(body)
        assert status == 200 and build["engine"] == "webgl2" and build["sourceRevision"] == args.source_sha
        assert "'wasm-unsafe-eval'" not in headers["Content-Security-Policy"]
        assert "'unsafe-eval'" not in headers["Content-Security-Policy"]
        for entry in build["files"]:
            assert re.fullmatch(r"[A-Za-z0-9_.-]+", entry["path"])
            status, _, content = request("/autotable/" + entry["path"])
            assert status == 200 and len(content) == entry["bytes"]
            assert hashlib.sha256(content).hexdigest() == entry["sha256"]
        status, _, content = request("/autotable/")
        assert status == 200 and b"Mahjong Web" in content
        status, _, content = request("/autotable/renderer-module-graph.json")
        graph = json.loads(content)
        assert status == 200 and graph["engine"] == "webgl2"
        assert not any(re.search(r"unity-host|/unity/|node_modules/.*autotable", path, re.I) for path in graph["modules"])
        for path in ("/autotable/unity/renderer-build.json", "/autotable/unity/player.wasm", "/autotable/old.loader.js"):
            status, _, data = request(path)
            assert status == 404 and not data
        inventory = command("docker", "exec", name, "find", "/frontend/autotable", "-type", "f").splitlines()
        assert not any(re.search(r"/unity/|\.(wasm|data)$|\.(loader|framework)\.js$", path, re.I) for path in inventory)
        receipt["browserFiles"] = len(inventory)
        receipt["engineOrPlayerFiles"] = 0
        notice = command("docker", "exec", name, "sh", "-c",
                         "test -s /licenses/THIRD_PARTY_NOTICES.txt && test -s /licenses/autotable/COPYING && test -s /licenses/art/FluffyStuff-LICENSE.md && printf retained")
        assert notice == "retained"
        receipt["noticesRetained"] = True
        receipt["status"] = "passed"
    except Exception as error:
        receipt["status"] = "failed"
        receipt["error"] = str(error)
        raise
    finally:
        if attempted:
            subprocess.run(["docker", "rm", "--force", name], check=False, capture_output=True)
        receipt["cleanup"] = "owned synthetic container removed; no registry publication or shared pruning"
        save()


if __name__ == "__main__":
    main()
