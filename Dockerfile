# syntax=docker/dockerfile:1.7
#
# Mahjong Unity — single-process deployment; the licensed Editor is build-only.
#
# Stage 1 — frontend bundle (Node 20 Alpine + Vite, Phase K Wave 7 swap)
# Stage 2 — backend build (.NET 10 SDK)
# Stage 3 — runtime (.NET 10 ASP.NET) hosting the published API + bundled
#           autotable assets. SQLite database lives on a /data volume.
#
# Phase J Wave 3 — Apone (DevOps).

############################
# Stage 0 — Unity gate      #
############################
FROM python:3.12-alpine AS unity-artifacts-check
WORKDIR /validation
COPY scripts/verify-unity-build.py ./scripts/
COPY contracts/renderer-v1.schema.json contracts/migration-contract.json ./contracts/
ARG ALLOW_SOURCE_DIRTY=0
# Bind the filtered context rather than COPY a possibly absent directory: missing
# outputs fail through the validator with the exact licensed-editor command.
RUN --mount=type=bind,source=.,target=/build-input,readonly \
    ALLOW_SOURCE_DIRTY="$ALLOW_SOURCE_DIRTY" python3 scripts/verify-unity-build.py \
        /build-input/artifacts/unity-webgl/renderer-build.json \
    && mkdir -p /verified \
    && cp -a /build-input/artifacts/unity-webgl/. /verified/

############################
# Stage 1 — frontend build #
############################
FROM node:20-alpine AS frontend-build
WORKDIR /src/frontend/autotable-src
# Establish the artifact gate before npm/build work, even with parallel BuildKit.
COPY --from=unity-artifacts-check /verified/renderer-build.json /unity-build-verified.json

# Install dependencies first so the layer caches when source changes only.
# Phase J Wave 8 (Apone): BuildKit cache mount on /root/.npm so re-runs
# (incl. CI) skip the network download when package-lock.json is stable.
COPY src/frontend/autotable-src/package.json src/frontend/autotable-src/package-lock.json ./
RUN --mount=type=cache,id=mahjong-npm,target=/root/.npm \
    npm ci --no-audit --no-fund --prefer-offline

# Copy the rest of the bundle source and produce the static assets.
# Phase K Wave 7 swapped Parcel → Vite (see vite.config.ts header). Vite
# writes to `../autotable` relative to the project root, so we land assets
# at /src/frontend/autotable. The Stage 3 COPY pulls from there.
# BuildKit cache mount on Vite's `node_modules/.vite` keeps incremental
# rebuilds (CI on a small source change) cheap. The cache is content-
# keyed so we don't need to invalidate manually.
COPY src/frontend/autotable-src/ ./
COPY src/backend/tools/collect-npm-notices.mjs /collect-npm-notices.mjs
# The same public identity is embedded in the loaded UI and runtime /health.
# Keep this after dependency installation to preserve the npm ci cache.
ARG BUILD_SHA=""
RUN --mount=type=cache,id=mahjong-vite,target=/src/frontend/autotable-src/node_modules/.vite \
    npm run build \
    && test -f /src/frontend/autotable/index.html \
    && node /collect-npm-notices.mjs /src/frontend/autotable-src /out/licenses/npm

############################
# Stage 2 — backend build  #
############################
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY --from=unity-artifacts-check /verified/renderer-build.json /unity-build-verified.json
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_EnableDiagnostics=0

# Bring in the whole backend tree so the .slnx + .csproj references resolve
# without hand-curated file lists. Tests are excluded via .dockerignore, so
# the restore stays scoped to the Api project.
COPY src/backend/ ./backend/

# Keep transient compiler/NuGet state out of image layers and off the backing
# filesystem; build servers and their sockets must not survive publication.
RUN --mount=type=tmpfs,target=/build-work \
    TMPDIR=/build-work dotnet restore backend/src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj \
    --disable-build-servers
RUN --mount=type=tmpfs,target=/build-work \
    TMPDIR=/build-work dotnet publish backend/src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj \
    -c Release \
    -o /out/api \
    --no-restore \
    --disable-build-servers \
    /p:UseSharedCompilation=false \
    /p:UseAppHost=false
RUN bash backend/tools/collect-nuget-notices.sh \
    backend/src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj /out/licenses/nuget

############################
# Stage 3 — runtime        #
############################
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
COPY --from=unity-artifacts-check /verified/renderer-build.json /unity-build-verified.json

# `curl` powers the HEALTHCHECK directive. tini gives PID 1 a proper signal
# handler so `docker stop` shuts the dotnet host down cleanly.
# apt's unprivileged verifier needs private directories with real POSIX modes.
# tmpfs supplies those even when Docker's backing storage cannot preserve chmod.
# Signature verification, the shipped keyring and apt's sandbox stay enabled.
RUN --mount=type=tmpfs,target=/build-work \
    --mount=type=tmpfs,target=/var/lib/apt/lists \
    --mount=type=tmpfs,target=/var/cache/apt \
    chmod 1777 /build-work \
    && TMPDIR=/build-work apt-get update \
    && TMPDIR=/build-work apt-get install -y --no-install-recommends curl tini

WORKDIR /app

# Phase J Wave 4 — CI publish wiring (Apone). The `/health` endpoint reads
# BUILD_SHA from the environment at request time and falls back to "dev" when
# unset or empty. To surface the actual commit SHA in published images we
# accept it as a build-arg here and promote it into the runtime ENV so the
# value bakes into the layer that runs `dotnet Mahjong.Autotable.Api.dll`.
# Local `docker build` without `--build-arg BUILD_SHA=...` keeps the empty
# default, which Program.cs resolves to "dev" — matching pre-CI behavior.
ARG BUILD_SHA=""
ARG ALLOW_SOURCE_DIRTY=0
ENV BUILD_SHA=${BUILD_SHA}
LABEL org.mahjong-unity.allow-source-dirty="${ALLOW_SOURCE_DIRTY}"

COPY --from=backend-build /out/api/ ./

# Program.cs resolves the autotable bundle at:
#   Path.GetFullPath(Path.Combine(ContentRootPath, "../../../frontend/autotable"))
# With WORKDIR=/app the path collapses to /frontend/autotable, so we drop the
# Vite output exactly there. Keeping the source-tree layout means the .glb /
# .gltf MIME-type extensions registered in Program.cs are exercised on every
# request — no backend change required. Phase K Wave 7 swapped the bundler
# to Vite which writes assets to /src/frontend/autotable (relative to the
# project root); the source path tracks that change.
COPY --from=frontend-build /src/frontend/autotable/ /frontend/autotable/
COPY --from=unity-artifacts-check --chown=1000:1000 /verified/ /frontend/autotable/unity/
COPY --from=frontend-build /out/licenses/npm/ /licenses/npm/
COPY --from=backend-build /out/licenses/nuget/ /licenses/nuget/
COPY src/frontend/autotable-src/COPYING src/frontend/autotable-src/UPSTREAM_SHA /licenses/autotable/
COPY contracts/source-provenance.json /licenses/source-provenance.json
COPY THIRD_PARTY_NOTICES.txt /licenses/THIRD_PARTY_NOTICES.txt

# Phase J Wave 7 — Apone (container hardening). Run as a fixed non-root
# UID/GID so the runtime image is safe to schedule on Kubernetes clusters
# that enforce `runAsNonRoot: true` (see infra/k8s/base/deployment.yaml).
# UID 1000 is the conventional first non-system user across both Debian
# (the aspnet base image) and the Kubernetes Pod Security Standard
# `restricted` profile. The /data volume is owned by that UID so SQLite
# can open/write its DB file without root.
RUN if ! getent group 1000 >/dev/null; then groupadd -g 1000 mahjong; fi \
    && if ! getent passwd 1000 >/dev/null; then useradd -u 1000 -g 1000 -M -s /usr/sbin/nologin mahjong; fi \
    && mkdir -p /data \
    && chown -R 1000:1000 /data /app \
    && chmod 755 /data

# Inherited /data volumes need not contain any new image-created subdirectory.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0 \
    HOME=/data \
    TMPDIR=/data \
    ConnectionStrings__Sqlite="Data Source=/data/mahjong-autotable.db" \
    Persistence__Provider=Sqlite

USER 1000:1000
EXPOSE 8080
VOLUME ["/data"]

# Healthcheck hits Bishop's canonical `/health` endpoint (Phase J Wave 3
# Task 3, commit 9235859 — `feat(api): add /health endpoint for Docker
# HEALTHCHECK + Linux deploy`). Falls back to the long-standing
# `/api/health` short-form probe (used by the frontend) so older builds
# or rollbacks still report healthy.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8080/health \
      || curl -fsS http://127.0.0.1:8080/api/health \
      || exit 1

ENTRYPOINT ["/usr/bin/tini", "--", "dotnet", "Mahjong.Autotable.Api.dll"]
