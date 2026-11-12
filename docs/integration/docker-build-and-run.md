# Source-only browser/hosting image

`bash build.sh --tag mahjong-web:local` (or `./build.ps1 -Tag mahjong-web:local`)
builds the canonical Dockerfile. Node produces the default WebGL bundle; the
.NET SDK publishes the existing API; the ASP.NET runtime image serves both.
There is no required prebuilt engine output or license input.

New installations:

```sh
./scripts/compose-bootstrap.sh
docker compose up -d --build
```

The bootstrap creates a local signing key only when absent. Never copy another
operator's `.env`, keys or database. Existing installations must retain the
existing Compose project/volume name and their stable signing configuration;
use `docker compose -p EXISTING_PROJECT ...` when a changed checkout folder
would otherwise select a new project. Internal API/database namespaces and
the compatibility `/autotable/` address are intentionally unchanged.

Compose binds loopback 8950 by default, mounts persistent `/data` plus the
retained bootstrap-cache directory, and enforces non-root/read-only/cap-drop/
no-new-privileges hardening. `MAHJONG_IMAGE`, `MAHJONG_HOST_PORT` and
`MAHJONG_BIND_ADDRESS` remain explicit overrides. Do not select or promote a
registry image merely because a repository was renamed.

For a disposable native fixture, restore the locked Node/.NET dependencies and
run `bash scripts/run-web.sh`. It uses fresh synthetic state and loopback 5087,
not the operator's persistence. Stop the foreground process normally.

PR CI builds the actual default image from the exact PR head without pushing
it. `tests/migration/container_smoke.py` starts an owned hardened fixture and
verifies health/source labels, every served file hash, strict CSP, retained
notices, and absence/rejection of engine/player artifacts, then removes only
that fixture. Browser gameplay is independently exercised by
`tests/migration/browser-webgl.mjs`. Build/health smoke alone is not device or
complete multiplayer qualification.
