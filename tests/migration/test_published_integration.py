"""Actual isolated published API/WS/SignalR/chat/browser-shell source checks."""

import base64
from collections import deque
import hashlib
import http.cookiejar
import json
import os
from pathlib import Path
import shutil
import socket
import struct
import subprocess
import sys
import time
import unittest
import urllib.error
import urllib.parse
import urllib.request
import uuid


ROOT = Path(__file__).resolve().parents[2]
WORK = ROOT / "artifacts/test-results/migration-work"
RESULTS = ROOT / "artifacts/test-results"
sys.dont_write_bytecode = True


class Peer:
    def __init__(self, port, path, cookie=""):
        self.socket = socket.create_connection(("127.0.0.1", port), timeout=10)
        self.pending = bytearray()
        self.messages = deque()
        self.transcript = {"sent": [], "received": [], "close": None}
        key = base64.b64encode(os.urandom(16)).decode()
        headers = [
            f"GET {path} HTTP/1.1", f"Host: 127.0.0.1:{port}",
            "Upgrade: websocket", "Connection: Upgrade",
            f"Sec-WebSocket-Key: {key}", "Sec-WebSocket-Version: 13",
        ]
        if cookie:
            headers.append("Cookie: " + cookie)
        self.socket.sendall(("\r\n".join(headers) + "\r\n\r\n").encode())
        response = bytearray()
        while b"\r\n\r\n" not in response:
            chunk = self.socket.recv(65536)
            if not chunk:
                self.socket.close()
                raise AssertionError("Actual WebSocket closed before completing the HTTP handshake")
            response.extend(chunk)
            if len(response) > 65536:
                raise AssertionError("WebSocket HTTP headers exceeded the bounded limit")
        raw, remainder = response.split(b"\r\n\r\n", 1)
        lines = raw.decode().split("\r\n")
        if not lines[0].startswith("HTTP/1.1 101"):
            self.socket.close()
            raise AssertionError("Actual WebSocket upgrade did not return 101")
        values = {
            name.lower(): value for name, value in (
                line.split(": ", 1) for line in lines[1:] if ": " in line
            )
        }
        expected = base64.b64encode(hashlib.sha1(
            (key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()
        ).digest()).decode()
        if values.get("sec-websocket-accept") != expected:
            raise AssertionError("Actual WebSocket accept digest did not match")
        self.pending.extend(remainder)

    def read(self, size):
        while len(self.pending) < size:
            chunk = self.socket.recv(max(65536, size - len(self.pending)))
            if not chunk:
                raise AssertionError("Actual WebSocket closed before the expected response")
            self.pending.extend(chunk)
        data = bytes(self.pending[:size])
        del self.pending[:size]
        return data

    def send_bytes(self, payload, opcode=1):
        mask = os.urandom(4)
        length = len(payload)
        header = bytes([0x80 | opcode, 0x80 | min(length, 126)])
        if length >= 65536:
            header = bytes([0x80 | opcode, 0x80 | 127]) + struct.pack("!Q", length)
        elif length >= 126:
            header += struct.pack("!H", length)
        self.socket.sendall(header + mask + bytes(
            value ^ mask[index % 4] for index, value in enumerate(payload)
        ))

    def send(self, value, signalr=False):
        self.transcript["sent"].append(value)
        text = json.dumps(value, separators=(",", ":")) + ("\x1e" if signalr else "")
        self.send_bytes(text.encode())

    def receive(self):
        while not self.messages:
            data = bytearray()
            while True:
                first, second = self.read(2)
                size = second & 127
                if size == 126:
                    size = struct.unpack("!H", self.read(2))[0]
                elif size == 127:
                    size = struct.unpack("!Q", self.read(8))[0]
                if size > 8 * 1024 * 1024:
                    raise AssertionError("Actual WebSocket message exceeded the source-test bound")
                mask = self.read(4) if second & 128 else None
                payload = self.read(size)
                if mask:
                    payload = bytes(value ^ mask[index % 4] for index, value in enumerate(payload))
                opcode = first & 15
                if opcode == 8:
                    code = struct.unpack("!H", payload[:2])[0] if len(payload) >= 2 else None
                    reason = payload[2:].decode("utf-8", errors="replace") if len(payload) > 2 else ""
                    self.transcript["close"] = {"code": code, "reason": reason}
                    raise AssertionError(f"Actual WebSocket closed before the expected message ({code}: {reason})")
                if opcode == 9:
                    self.send_bytes(payload, 10)
                    continue
                if opcode == 10:
                    continue
                if opcode not in (0, 1):
                    raise AssertionError("Expected JSON text from the actual source transport")
                data.extend(payload)
                if first & 128:
                    break
            for text in data.decode().split("\x1e"):
                if text:
                    message = json.loads(text)
                    self.messages.append(message)
                    self.transcript["received"].append(message)
        return self.messages.popleft()

    def wait(self, predicate, timeout=10):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            self.socket.settimeout(max(0.1, deadline - time.monotonic()))
            message = self.receive()
            if predicate(message):
                return message
        raise AssertionError("Actual source transport did not emit the expected shape")

    def close(self):
        if self.socket is not None:
            try:
                self.send_bytes(struct.pack("!H", 1000), 8)
            except OSError:
                pass
            self.socket.close()
            self.socket = None


class PublishedIntegrationTests(unittest.TestCase):
    runtime_environment = "Development"
    build_sha = "migration-native-source-test"
    listen_port = 0

    @classmethod
    def setUpClass(cls):
        binary = ROOT / "artifacts/api/Mahjong.Autotable.Api.dll"
        if not binary.is_file():
            raise unittest.SkipTest("Actual published API unavailable; run the documented Release dotnet publish first")
        WORK.mkdir(parents=True, exist_ok=True)
        cls.fixture = WORK / ("published-" + uuid.uuid4().hex)
        content = cls.fixture / "host/backend/src/Api"
        content.mkdir(parents=True)
        home = cls.fixture / "home"
        home.mkdir()
        frontend = cls.fixture / "host/frontend"
        frontend.mkdir(parents=True)
        (frontend / "autotable").symlink_to(ROOT / "src/frontend/autotable", target_is_directory=True)
        shutil.copyfile(
            ROOT / "src/backend/src/Mahjong.Autotable.Api/appsettings.json",
            content / "appsettings.json",
        )
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", cls.listen_port))
            cls.port = probe.getsockname()[1]
        cls.base = f"http://127.0.0.1:{cls.port}"
        cls.peers = []
        cls.receipt = {
            "qualification": "Actual published ASP.NET source/transport and unavailable browser shell, not Unity or runtime Docker",
            "requests": [], "checks": {}, "privateConfigurationLogged": False,
        }
        cls.environment = {
            key: os.environ[key] for key in ("PATH", "HOME", "DOTNET_ROOT", "LD_LIBRARY_PATH")
            if key in os.environ
        }
        cls.environment.update({
            "HOME": str(home), "DOTNET_CLI_HOME": str(home),
            "XDG_DATA_HOME": str(home / ".local/share"),
            "XDG_CONFIG_HOME": str(home / ".config"),
            "XDG_CACHE_HOME": str(home / ".cache"),
            "ASPNETCORE_ENVIRONMENT": cls.runtime_environment, "DOTNET_ENVIRONMENT": cls.runtime_environment,
            "ASPNETCORE_URLS": cls.base,
            "Persistence__Provider": "Sqlite",
            "ConnectionStrings__Sqlite": f"Data Source={cls.fixture / 'state.db'};Pooling=False",
            "Authentication__JwtSigningKeys__0": base64.b64encode(os.urandom(48)).decode(),
            "Auth__JwtSigningKeys__0": base64.b64encode(os.urandom(48)).decode(),
            "Logging__LogLevel__Default": "Warning", "RateLimiting__Enabled": "false",
            "Sentry__Dsn": "", "TMPDIR": str(WORK), "TMP": str(WORK), "TEMP": str(WORK),
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1",
            "BUILD_SHA": cls.build_sha,
        })
        cls.log = (RESULTS / "migration-published-api.log").open("w")
        cls.receipt["publishedBinarySha256"] = hashlib.sha256(binary.read_bytes()).hexdigest()
        cls.process = subprocess.Popen(
            ["dotnet", str(binary), "--contentRoot", str(content)],
            cwd=content, env=cls.environment, stdout=cls.log, stderr=subprocess.STDOUT,
        )
        try:
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                if cls.process.poll() is not None:
                    raise AssertionError("Actual isolated API exited before health became responsive")
                try:
                    with urllib.request.urlopen(cls.base + "/api/health", timeout=0.5) as response:
                        if response.status == 200:
                            return
                except (urllib.error.URLError, TimeoutError):
                    time.sleep(0.1)
            raise AssertionError("Actual isolated API did not become responsive")
        except BaseException:
            cls.stop()
            raise

    @classmethod
    def stop(cls):
        for peer in cls.peers:
            peer.close()
        if cls.process.poll() is None:
            cls.process.terminate()
            try:
                cls.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                cls.process.kill()
                cls.process.wait(timeout=5)
        cls.log.close()
        log_text = (RESULTS / "migration-published-api.log").read_text()
        if any(cls.environment[key] in log_text for key in (
            "Authentication__JwtSigningKeys__0", "Auth__JwtSigningKeys__0",
        )):
            shutil.rmtree(cls.fixture)
            raise AssertionError("Ephemeral test signing material unexpectedly appeared in the API log")
        cls.receipt["processExitCode"] = cls.process.returncode
        cls.receipt["processStopped"] = cls.process.poll() is not None
        cls.receipt["gracefulShutdown"] = cls.process.returncode == 0
        (RESULTS / "migration-published-api.json").write_text(json.dumps(cls.receipt, indent=2) + "\n")
        shutil.rmtree(cls.fixture)

    @classmethod
    def tearDownClass(cls):
        cls.stop()
        if not cls.receipt["gracefulShutdown"]:
            raise AssertionError("The owned published API did not stop gracefully")

    def request(self, path, method="GET", body=None, opener=None):
        payload = json.dumps(body).encode() if body is not None else (b"" if method == "POST" else None)
        request = urllib.request.Request(
            self.base + path, data=payload, method=method,
            headers={"Content-Type": "application/json"} if body is not None else {},
        )
        try:
            response = (opener or urllib.request.build_opener()).open(request, timeout=10)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            content = response.read()
            self.receipt["requests"].append({
                "path": path.split("?")[0], "method": method, "status": response.status,
                "contentType": response.headers.get_content_type(), "bytes": len(content),
            })
            return response.status, response.headers, content

    def identity(self):
        jar = http.cookiejar.CookieJar()
        opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
        status, headers, content = self.request("/api/identity", "POST", opener=opener)
        self.assertEqual(200, status)
        identity = json.loads(content)
        self.assertTrue({"playerId", "displayName", "avatarColor", "createdAt", "lastSeenAt"} <= identity.keys())
        cookies = [cookie for cookie in jar if cookie.name == "mahjong_pid"]
        self.assertEqual(1, len(cookies))
        self.assertNotEqual(identity["playerId"], cookies[0].value)
        self.assertIn("httponly", headers["Set-Cookie"].lower())
        return identity, opener, "mahjong_pid=" + cookies[0].value

    def peer(self, path, cookie=""):
        peer = Peer(self.port, path, cookie)
        self.peers.append(peer)
        self.receipt.setdefault("nativeWire", []).append(peer.transcript)
        return peer

    def test_01_actual_health_lobby_static_404_headers_and_transport_negotiation(self):
        status, _, content = self.request("/health")
        self.assertEqual(200, status)
        health = json.loads(content)
        self.assertEqual("healthy", health["status"])
        self.assertTrue(health["db"]["connected"])
        status, _, content = self.request("/api/health")
        self.assertEqual(200, status)
        self.assertEqual("ok", json.loads(content)["status"])
        status, _, content = self.request("/api/matchmaking/lobby")
        self.assertEqual(200, status)
        self.assertEqual([], json.loads(content)["games"])
        status, _, content = self.request("/autotable/")
        self.assertEqual(200, status)
        self.assertIn(b'id="lobby-panel"', content)
        for path in (
            "/autotable/unity/renderer-build.json", "/autotable/unity/Build/missing.loader.js",
            "/autotable/unity/Build/missing.wasm", "/autotable/unity/Build/missing.data",
        ):
            status, headers, content = self.request(path)
            self.assertEqual(404, status)
            self.assertEqual(b"", content)
            self.assertNotEqual("text/html", headers.get_content_type())
            policy = headers["Content-Security-Policy"]
            self.assertIn("'wasm-unsafe-eval'", policy)
            self.assertNotIn("'unsafe-eval'", policy)
            self.assertNotIn("Cross-Origin-Opener-Policy", headers)
            self.assertNotIn("Cross-Origin-Embedder-Policy", headers)
            self.assertIn("no-cache", headers["Cache-Control"])
            self.assertIn("must-revalidate", headers["Cache-Control"])
        status, _, content = self.request("/hubs/changsha/negotiate?negotiateVersion=1", "POST")
        self.assertEqual(200, status)
        self.assertEqual(
            {"WebSockets", "ServerSentEvents", "LongPolling"},
            {transport["transport"] for transport in json.loads(content)["availableTransports"]},
        )
        status, _, _ = self.request("/autotable/ws")
        self.assertEqual(400, status)
        self.receipt["checks"]["initialHttpAndMissingBuild"] = True

    def test_02_actual_signed_room_signalr_public_lobby_chat_and_reconnect(self):
        identity, opener, cookie = self.identity()
        alias = "migration-native-" + uuid.uuid4().hex
        query = urllib.parse.urlencode({
            "gameId": alias, "variant": "changsha", "seat": 0,
            "botCount": 0, "dealMode": "manual", "seed": 20260917,
        })
        owner = self.peer("/autotable/ws?" + query, cookie)
        owner.send({"type": "NEW"})
        joined = owner.wait(lambda message: message.get("type") == "JOINED")
        self.assertEqual(alias, joined["gameId"])
        self.assertEqual(identity["playerId"], joined["playerId"])
        owner.send({"type": "UPDATE", "entries": [["seats", identity["playerId"], {"seat": 0}]]})
        snapshot = owner.wait(lambda message:
            message.get("type") == "UPDATE" and message.get("viewer", {}).get("seat") == 0)
        self.assertEqual(alias, snapshot["viewer"]["roomId"])
        self.assertIsInstance(snapshot["viewer"]["revision"], int)
        self.assertTrue(all(isinstance(entry, list) and len(entry) == 3 for entry in snapshot["entries"]))
        status, _, content = self.request("/api/games/" + alias, opener=opener)
        self.assertEqual(200, status)
        metadata = json.loads(content)
        self.assertEqual(alias, metadata["gameId"])
        self.assertTrue(metadata["viewerIsOwner"])
        self.assertEqual(1, metadata["seatedCount"])
        self.assertEqual(3, metadata["openHumanSeats"])
        self.assertEqual(0, metadata["botCount"])
        self.assertTrue(metadata["canMakePublic"])
        status, _, content = self.request("/hubs/changsha/negotiate?negotiateVersion=1", "POST", opener=opener)
        self.assertEqual(200, status)
        token = json.loads(content)["connectionToken"]
        hub = self.peer("/hubs/changsha?id=" + urllib.parse.quote(token, safe=""), cookie)
        hub.send({"protocol": "json", "version": 1}, signalr=True)
        hub.wait(lambda message: message == {})
        hub.send({"type": 1, "invocationId": "1", "target": "JoinLobby", "arguments": []}, signalr=True)
        result = hub.wait(lambda message: message.get("type") == 3 and message.get("invocationId") == "1")
        self.assertNotIn("error", result)
        self.assertTrue({"revision", "players", "invites"} <= result["result"].keys())
        self.assertIsInstance(result["result"]["revision"], int)
        self.assertIsInstance(result["result"]["players"], list)
        hub.send({
            "type": 1, "invocationId": "2", "target": "SetGamePublic",
            "arguments": [alias, True, "Migration source room"],
        }, signalr=True)
        public = hub.wait(lambda message: message.get("type") == 3 and message.get("invocationId") == "2")
        self.assertNotIn("error", public)
        self.assertTrue(public["result"]["success"])
        status, _, content = self.request("/api/matchmaking/lobby", opener=opener)
        self.assertEqual(200, status)
        listing = next(game for game in json.loads(content)["games"] if game["gameId"] == alias)
        self.assertEqual(1, listing["seatedCount"])
        self.assertEqual(3, listing["openHumanSeats"])
        self.assertEqual(4, listing["maxSeats"])
        chat_path = "/api/games/" + alias + "/chat"
        status, _, content = self.request(chat_path, "POST", {"body": "Source smoke", "channel": "table"})
        self.assertEqual(401, status)
        self.assertEqual("identity-required", json.loads(content)["error"])
        status, _, content = self.request(chat_path, "POST", {"body": "Source smoke", "channel": "table"}, opener)
        self.assertEqual(200, status)
        message = json.loads(content)
        self.assertEqual(alias, message["gameId"])
        self.assertEqual("Source smoke", message["body"])
        self.assertEqual(identity["playerId"], message["senderPlayerId"])
        self.assertEqual(message["senderPlayerId"], message["playerId"])
        self.assertEqual(message["sentUtc"], message["at"])
        status, _, content = self.request(chat_path + "?limit=50", opener=opener)
        self.assertEqual(200, status)
        history = json.loads(content)
        self.assertEqual(alias, history["gameId"])
        self.assertIn(message["id"], [entry["id"] for entry in history["messages"]])
        status, _, _ = self.request("/api/chat/send", "POST", {
            "gameId": alias, "body": "Compatibility route smoke", "channel": "table",
        }, opener)
        self.assertEqual(200, status)
        _, stranger, _ = self.identity()
        status, _, _ = self.request(chat_path, opener=stranger)
        self.assertEqual(403, status)
        # A sole human owner leaving a public seating room intentionally retires it.
        hub.send({
            "type": 1, "invocationId": "3", "target": "SetGamePublic",
            "arguments": [alias, False, None],
        }, signalr=True)
        private = hub.wait(lambda message: message.get("type") == 3 and message.get("invocationId") == "3")
        self.assertNotIn("error", private)
        self.assertTrue(private["result"]["success"])
        status, _, content = self.request("/api/matchmaking/lobby", opener=opener)
        self.assertEqual(200, status)
        self.assertNotIn(alias, [game["gameId"] for game in json.loads(content)["games"]])
        owner.close()
        reconnect = self.peer("/autotable/ws?" + urllib.parse.urlencode({
            "gameId": alias, "variant": "changsha", "join": 1,
        }), cookie)
        reconnect.send({"type": "JOIN", "gameId": alias})
        rejoined = reconnect.wait(lambda message: message.get("type") == "JOINED")
        self.assertEqual(alias, rejoined["gameId"])
        self.assertEqual(identity["playerId"], rejoined["playerId"])
        restored = reconnect.wait(lambda message: message.get("type") == "UPDATE" and message.get("full") is True)
        self.assertEqual(alias, restored["viewer"]["roomId"])
        self.assertEqual(0, restored["viewer"]["seat"])
        status, _, content = self.request("/health")
        self.assertEqual(200, status)
        self.assertEqual(1, json.loads(content)["activeGames"])
        self.receipt["checks"]["signedRawWsRoomLobbyChatReconnect"] = {
            "rawHandshake101": True, "signedIdentityReattached": True,
            "viewerSeat": restored["viewer"]["seat"], "publicLobbyShape": sorted(listing),
            "chatShape": sorted(message), "strangerChatDenied": True,
            "runtimeCountAfterReconnect": 1, "reconnectRoomVisibility": "private",
        }

    @unittest.skipUnless(shutil.which("google-chrome") and shutil.which("node"), "Native Chrome/Node unavailable")
    def test_03_actual_browser_lobby_and_visible_missing_build(self):
        result = subprocess.run(
            ["node", "tests/migration/browser-unavailable.mjs", self.base],
            cwd=ROOT, env=self.environment, text=True, capture_output=True, timeout=90,
        )
        (RESULTS / "migration-browser-unavailable.log").write_text(result.stdout + result.stderr)
        if result.returncode == 77:
            self.receipt["checks"]["nativeBrowser"] = json.loads(result.stdout)
            self.skipTest("Native Chrome launch unavailable after default and GPU-disabled attempts; no app page loaded")
        self.assertEqual(0, result.returncode, result.stderr[-2500:])
        self.receipt["checks"]["nativeBrowser"] = json.loads(result.stdout)


if __name__ == "__main__":
    unittest.main()
