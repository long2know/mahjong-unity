#!/usr/bin/env python3
"""Use the same source image behind an actual pinned nginx TLS/prefix proxy."""
import argparse
import base64
import datetime
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import socket
import ssl
import subprocess
import time
import urllib.error
import urllib.request
import uuid
from runtime_probe import artifact_output, probe_provenance

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "artifacts/base-url/hosted-nginx"
NGINX = "nginx:1.28.0-alpine@sha256:30f1c0d78e0ad60901648be663a710bdadf19e4c10ac6782c235200619158284"


def command(*args, **kwargs):
    return subprocess.check_output(args, text=True, **kwargs).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--output-dir", default="artifacts/base-url/hosted-nginx",
                        help="Checkout-relative artifact directory; defaults preserve publisher paths.")
    args = parser.parse_args()
    output = artifact_output(ROOT, args.output_dir)
    browser_output = output if output != OUT else ROOT / "artifacts/base-url"
    trace_output = output if output != OUT else ROOT / "artifacts/runtime-input-trace/canonical"
    if not trace_output.is_relative_to(ROOT / "artifacts/runtime-input-trace"):
        raise ValueError("Isolated diagnostic outputs must remain under artifacts/runtime-input-trace.")
    output.mkdir(parents=True, exist_ok=True)
    receipt = {"status": "running", "source": args.source_sha, "image": args.image, "nginx": NGINX,
               "applicationSourceSha": args.source_sha, **probe_provenance(ROOT),
               "scope": "Actual hardened app image behind nginx TLS and path-preserving proxy; real host-Chrome input/WS/SW",
               "recordedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "cases": []}
    save = lambda: (output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    environment = dict(os.environ, Authentication__JwtSigningKeys__0=base64.b64encode(os.urandom(48)).decode(),
                       Auth__JwtSigningKeys__0=base64.b64encode(os.urandom(48)).decode())
    context = ssl._create_unverified_context()  # Synthetic loopback test certificate only.
    command("docker", "pull", NGINX)
    image = json.loads(command("docker", "image", "inspect", args.image))[0]
    assert image["Config"]["Labels"]["org.opencontainers.image.revision"] == args.source_sha
    receipt["imageId"] = image["Id"]
    save()
    try:
        for index, prefix in enumerate(("", "/mahjong", "/games/mahjong", "/My%20Game")):
            name = "base-url-" + uuid.uuid4().hex
            network, app, proxy = name + "-net", name + "-app", name + "-nginx"
            fixture = output / name
            fixture.mkdir()
            case = {"prefix": prefix, "status": "running"}
            receipt["cases"].append(case)
            attempted = []
            try:
                command("docker", "network", "create", network); attempted.append(("network", network))
                info = json.loads(command("docker", "network", "inspect", network))[0]
                subnet = ipaddress.ip_network(info["IPAM"]["Config"][0]["Subnet"])
                # Let Docker select a free pool, then make its IPAM subnet explicit for fixed peer IPs.
                command("docker", "network", "rm", network)
                command("docker", "network", "create", "--subnet", str(subnet), network)
                app_ip, proxy_ip = str(subnet.network_address + 2), str(subnet.network_address + 3)
                with socket.socket() as sock:
                    sock.bind(("127.0.0.1", 0)); port = sock.getsockname()[1]
                assert port != 5086
                origin = f"https://127.0.0.1:{port}"
                command("openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                        "-subj", "/CN=localhost", "-addext", "subjectAltName=IP:127.0.0.1",
                        "-keyout", str(fixture / "test.key"), "-out", str(fixture / "test.crt"),
                        stderr=subprocess.DEVNULL)
                (fixture / "test.key").chmod(0o644)  # Owned one-day synthetic key, readable by nginx UID101.
                config = """pid /var/run/nginx.pid;
error_log stderr warn;
events {}
http {
  access_log off;
  client_body_temp_path /var/cache/nginx/client;
  proxy_temp_path /var/cache/nginx/proxy;
  map $http_upgrade $connection_upgrade { default upgrade; '' close; }
  server {
    listen 8443 ssl;
    ssl_certificate /test/test.crt;
    ssl_certificate_key /test/test.key;
    location / {
      proxy_pass http://mahjong-app:8080;
      proxy_http_version 1.1;
      proxy_set_header Host $http_host;
      proxy_set_header Upgrade $http_upgrade;
      proxy_set_header Connection $connection_upgrade;
      proxy_set_header X-Forwarded-Proto $scheme;
      proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
      proxy_read_timeout 60s;
    }
  }
}
"""
                (fixture / "nginx.conf").write_text(config)
                attempted.append(("container", app))
                command("docker", "run", "--detach", "--name", app, "--network", network, "--ip", app_ip,
                        "--network-alias", "mahjong-app", "--user", "1000:1000", "--read-only",
                        "--cap-drop=ALL", "--security-opt=no-new-privileges:true",
                        "--tmpfs", "/data:rw,uid=1000,gid=1000,mode=700",
                        "--tmpfs", "/app/data:rw,uid=1000,gid=1000,mode=700",
                        "--env", "Authentication__JwtSigningKeys__0", "--env", "Auth__JwtSigningKeys__0",
                        "--env", "BASE_URL=" + prefix, "--env", "ReverseProxy__KnownProxies__0=" + proxy_ip,
                        "--env", "Logging__LogLevel__Default=Warning", "--env", "Sentry__Dsn=", args.image, env=environment)
                attempted.append(("container", proxy))
                command("docker", "run", "--detach", "--name", proxy, "--network", network, "--ip", proxy_ip,
                        "--user", "101:101", "--read-only", "--cap-drop=ALL", "--security-opt=no-new-privileges:true",
                        "--publish", f"127.0.0.1:{port}:8443",
                        "--mount", f"type=bind,source={fixture},target=/test,readonly",
                        "--tmpfs", "/var/cache/nginx:rw,uid=101,gid=101,mode=700",
                        "--tmpfs", "/var/run:rw,uid=101,gid=101,mode=700",
                        "--entrypoint", "nginx", NGINX, "-c", "/test/nginx.conf", "-g", "daemon off;")
                for _ in range(120):
                    try:
                        with urllib.request.urlopen(origin + prefix + "/health", context=context, timeout=2) as response:
                            health = json.load(response)
                            if health["status"] == "healthy":
                                assert health["buildSha"] == args.source_sha
                                case["health"] = health
                                break
                    except (OSError, ValueError):
                        time.sleep(.5)
                else:
                    raise ValueError("nginx-prefixed synthetic app never became healthy")
                with urllib.request.urlopen(origin + prefix + "/web-build.json", context=context) as response:
                    data = response.read(); build = json.loads(data)
                assert build["sourceRevision"] == args.source_sha and build["engine"] == "webgl2"
                case["webBuildSha256"] = hashlib.sha256(data).hexdigest()
                if prefix:
                    try:
                        urllib.request.urlopen(origin + "/api/identity", context=context)
                        raise AssertionError("Wrong-root API unexpectedly escaped the configured prefix.")
                    except urllib.error.HTTPError as error:
                        assert error.code == 404
                browser_name = "hosted-nginx-" + str(index)
                env = dict(os.environ, TMPDIR=str(ROOT / ".work"), PREFIX_CASE_NAME=browser_name,
                           PREFIX_OUTPUT_ROOT=browser_output.relative_to(ROOT).as_posix(),
                           PREFIX_TRACE_ROOT=trace_output.relative_to(ROOT).as_posix(),
                           PREFIX_QUALIFICATION="Actual pinned nginx TLS/prefix proxy to hardened canonical Docker image",
                           EXPECTED_SOURCE_SHA=args.source_sha)
                (ROOT / ".work").mkdir(exist_ok=True)
                with (fixture / "browser.log").open("w") as stream:
                    subprocess.run(["node", "tests/migration/browser-prefix.mjs", origin + prefix],
                                   cwd=ROOT, env=env, stdout=stream, stderr=subprocess.STDOUT, check=True)
                proof = json.loads((browser_output / browser_name / "receipt.json").read_text())
                assert proof["status"] == "passed" and proof["source"] == args.source_sha
                assert proof["probeSourceSha"] == receipt["probeSourceSha"] and proof["probeFiles"] == receipt["probeFiles"]
                assert proof["identityCookie"]["secure"] and proof["identityCookie"]["path"] == prefix + "/"
                assert all(socket["protocol"] == "wss:" and socket["path"].startswith(prefix + "/") for socket in proof["sockets"])
                case.update(status="passed", origin=origin,
                            browserReceipt=(browser_output / browser_name / "receipt.json").relative_to(ROOT).as_posix(),
                            trustedProxyIp=proxy_ip, transport="TLS/WSS, path preserved, explicit known immediate peer",
                            fixtureConfigSha256=hashlib.sha256(config.encode()).hexdigest())
                save()
            except Exception as error:
                case["status"] = "failed"; case["error"] = str(error)
                for resource in (app, proxy):
                    result = subprocess.run(["docker", "logs", resource], capture_output=True, text=True, check=False)
                    (fixture / (resource + ".log")).write_text((result.stdout + result.stderr)[-131072:])
                raise
            finally:
                for kind, resource in reversed(attempted):
                    subprocess.run(["docker", "rm", "--force", resource] if kind == "container"
                                   else ["docker", "network", "rm", resource], capture_output=True, check=False)
                (fixture / "test.key").unlink(missing_ok=True)
                (fixture / "test.crt").unlink(missing_ok=True)
                case["cleanup"] = "only owned synthetic containers/network removed"
                save()
        assert len({case["webBuildSha256"] for case in receipt["cases"]}) == 1, "Runtime prefix must not rewrite built assets."
        receipt["status"] = "passed"
    except Exception as error:
        receipt["status"] = "failed"; receipt["error"] = str(error); raise
    finally:
        save()


if __name__ == "__main__":
    main()
