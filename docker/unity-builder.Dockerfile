FROM unityci/editor:ubuntu-6000.3.24f1-webgl-3.2.2@sha256:f3c6661a8c5bc975df168f114eb3882766546b2ad09765664f5b2bb186857ddb

# Build tools stay in this large, build-only image, never the ASP.NET runtime.
RUN command -v bash >/dev/null \
    && command -v git >/dev/null \
    && command -v python3 >/dev/null \
    && test -x /opt/unity/Editor/Unity \
    && test -d /opt/unity/Editor/Data/PlaybackEngines/WebGLSupport
COPY --chmod=755 docker/unity-build-entrypoint.sh /usr/local/bin/mahjong-unity-build
WORKDIR /workspace
ENTRYPOINT ["/usr/local/bin/mahjong-unity-build"]
CMD ["--test-and-build"]
