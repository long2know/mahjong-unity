# syntax=docker/dockerfile:1.7
FROM node:20-alpine AS frontend-build
WORKDIR /src/frontend/autotable-src
COPY src/frontend/autotable-src/package.json src/frontend/autotable-src/package-lock.json ./
RUN --mount=type=cache,id=mahjong-web-npm,target=/root/.npm \
    npm ci --no-audit --no-fund --prefer-offline
COPY src/frontend/autotable-src/ ./
COPY contracts/renderer-v1.schema.json /contracts/renderer-v1.schema.json
COPY src/backend/tools/collect-npm-notices.mjs /collect-npm-notices.mjs
ARG BUILD_SHA="local"
RUN npm run typecheck && npm run build \
    && test -f ../autotable/web-build.json \
    && node /collect-npm-notices.mjs /src/frontend/autotable-src /out/licenses/npm

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_EnableDiagnostics=0
COPY src/backend/ ./backend/
RUN --mount=type=tmpfs,target=/build-work \
    TMPDIR=/build-work dotnet restore backend/src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj --disable-build-servers
RUN --mount=type=tmpfs,target=/build-work \
    TMPDIR=/build-work dotnet publish backend/src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj \
    -c Release -o /out/api --no-restore --disable-build-servers /p:UseSharedCompilation=false /p:UseAppHost=false
RUN bash backend/tools/collect-nuget-notices.sh \
    backend/src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj /out/licenses/nuget

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN --mount=type=tmpfs,target=/build-work \
    --mount=type=tmpfs,target=/var/lib/apt/lists \
    --mount=type=tmpfs,target=/var/cache/apt \
    chmod 1777 /build-work \
    && TMPDIR=/build-work apt-get update \
    && TMPDIR=/build-work apt-get install -y --no-install-recommends curl tini
WORKDIR /app
ARG BUILD_SHA="local"
ENV BUILD_SHA=${BUILD_SHA}
LABEL org.opencontainers.image.source="https://github.com/long2know/mahjong-web" \
    org.opencontainers.image.revision="${BUILD_SHA}" \
    org.mahjong-web.engine="webgl2"
COPY --from=backend-build /out/api/ ./
COPY --from=frontend-build /src/frontend/autotable/ /frontend/autotable/
COPY --from=frontend-build /out/licenses/npm/ /licenses/npm/
COPY --from=backend-build /out/licenses/nuget/ /licenses/nuget/
COPY src/frontend/autotable-src/COPYING src/frontend/autotable-src/UPSTREAM_SHA /licenses/autotable/
COPY src/frontend/autotable-src/art/source/FluffyStuff/LICENSE.md /licenses/art/FluffyStuff-LICENSE.md
COPY src/frontend/autotable-src/art/tiles/import-provenance.json /licenses/art/import-provenance.json
COPY contracts/source-provenance.json /licenses/source-provenance.json
COPY THIRD_PARTY_NOTICES.txt /licenses/THIRD_PARTY_NOTICES.txt
RUN if ! getent group 1000 >/dev/null; then groupadd -g 1000 mahjong; fi \
    && if ! getent passwd 1000 >/dev/null; then useradd -u 1000 -g 1000 -M -s /usr/sbin/nologin mahjong; fi \
    && mkdir -p /data /app/data && chown -R 1000:1000 /data /app && chmod 755 /data
ENV ASPNETCORE_URLS=http://+:8080 ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true DOTNET_EnableDiagnostics=0 \
    HOME=/data TMPDIR=/data Security__CspStrict=true \
    ConnectionStrings__Sqlite="Data Source=/data/mahjong-autotable.db" Persistence__Provider=Sqlite
USER 1000:1000
EXPOSE 8080
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["/usr/bin/tini", "--", "dotnet", "Mahjong.Autotable.Api.dll"]
