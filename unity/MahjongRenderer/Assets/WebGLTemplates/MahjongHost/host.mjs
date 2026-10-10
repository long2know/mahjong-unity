export function safeArtifactPath(value) {
  return typeof value === "string" && /^[A-Za-z0-9_./-]+$/.test(value) &&
    !value.startsWith("/") && value.split("/").every(part => part !== "" && part !== "." && part !== "..");
}

export function validateManifest(value) {
  const keys = ["artifactKind", "contractVersion", "buildId", "unityVersion", "unityRevision", "compression",
    "threading", "developmentBuild", "baselineRevision", "rendererSourceRevision", "rendererSourceTreeSha256",
    "sourceDirty", "builtAtUtc", "loader", "framework", "data", "wasm", "streamingAssetsPath"];
  if (!value || typeof value !== "object" || Array.isArray(value))
    throw new Error("Actual Unity build manifest is incompatible.");
  const missing = keys.find(key => !Object.hasOwn(value, key));
  if (missing) throw new Error("Actual Unity build manifest is missing " + missing + ".");
  if (Object.keys(value).length !== keys.length)
    throw new Error("Actual Unity build manifest contains unknown fields.");
  if (value.artifactKind !== "unity-webgl" || value.contractVersion !== "1.0.0" ||
      value.unityVersion !== "6000.3.24f1" || value.unityRevision !== "4e7b9b5b6244" ||
      value.compression !== "none" || value.threading !== "single-threaded" ||
      value.developmentBuild !== false || typeof value.sourceDirty !== "boolean" ||
      value.baselineRevision !== "d10d351e543aa3e341960de9800828b6c0662a82" ||
      typeof value.rendererSourceRevision !== "string" || !/^[a-f0-9]{40}$/.test(value.rendererSourceRevision) ||
      typeof value.rendererSourceTreeSha256 !== "string" || !/^[a-f0-9]{64}$/.test(value.rendererSourceTreeSha256) ||
      typeof value.builtAtUtc !== "string" || value.builtAtUtc.length < 1 || value.builtAtUtc.length > 128 ||
      typeof value.buildId !== "string" || value.buildId.length < 1 || value.buildId.length > 128) {
    throw new Error("Actual Unity build manifest is incompatible.");
  }
  const files = new Set();
  for (const [kind, extension] of [["loader", ".loader.js"], ["framework", ".framework.js"], ["wasm", ".wasm"], ["data", ".data"]]) {
    const file = value[kind];
    if (!file || Object.keys(file).length !== 3 || !["path", "bytes", "sha256"].every(key => Object.hasOwn(file, key)) ||
        !safeArtifactPath(file.path) || !file.path.endsWith(extension) ||
        files.has(file.path) || !Number.isSafeInteger(file.bytes) || file.bytes <= 0 ||
        typeof file.sha256 !== "string" || !/^[a-f0-9]{64}$/.test(file.sha256)) {
      throw new Error("Actual Unity artifact is missing or unsafe: " + kind);
    }
    files.add(file.path);
  }
  if (value.streamingAssetsPath !== null && !safeArtifactPath(value.streamingAssetsPath))
    throw new Error("Actual StreamingAssets path is unsafe.");
  return value;
}

export function measuredViewport(rect, dpr, insets, handBand, instanceId, revision) {
  const ratio = Math.min(dpr, 2);
  if (![rect.width, rect.height, dpr].every(number => Number.isFinite(number) && number > 0) ||
      !Object.values(insets).every(number => Number.isFinite(number) && number >= 0) ||
      !Number.isFinite(handBand) || handBand < 0 ||
      insets.left + insets.right >= rect.width ||
      insets.top + insets.bottom + handBand >= rect.height)
    throw new Error("Canvas has no safe renderer viewport.");
  return {
    contractVersion: "1.0.0", instanceId, type: "viewport", resizeRevision: revision,
    widthCssPx: rect.width, heightCssPx: rect.height, devicePixelRatio: dpr, renderPixelRatio: ratio,
    widthPx: Math.round(rect.width * ratio), heightPx: Math.round(rect.height * ratio),
    insetsCssPx: insets, handBandCssPx: handBand
  };
}

async function boot() {
  const canvas = document.getElementById("mahjong-unity");
  const status = document.getElementById("renderer-status");
  const instanceId = crypto.randomUUID();
  const sessionId = crypto.randomUUID();
  const abort = new AbortController();
  let unity = null;
  let ready = false;
  let disposed = false;
  let resizeRevision = 0;
  let viewportFingerprint = "";
  let layout = null;
  let frame = null;
  let handBand = 0;
  let manifest;
  let readyResolve;
  let readyReject;
  const awaitingReady = new Promise((resolve, reject) => { readyResolve = resolve; readyReject = reject; });
  awaitingReady.catch(() => {});
  const fail = error => {
    status.value = "Unity renderer unavailable: " + error.message;
    status.dataset.failure = "true";
    delete document.documentElement.dataset.unityRendererReady;
    readyReject(error);
  };
  const send = (method, message) => {
    if (disposed || !unity) throw new Error("Unity renderer instance is unavailable.");
    unity.SendMessage("MahjongBridge", method, JSON.stringify(message));
  };
  const updateViewport = () => {
    if (!ready || disposed) return;
    const style = getComputedStyle(document.documentElement);
    const css = name => Math.max(0, Number.parseFloat(style.getPropertyValue(name)) || 0);
    const value = measuredViewport(canvas.getBoundingClientRect(), window.devicePixelRatio || 1,
      { top: css("--safe-top"), right: css("--safe-right"), bottom: css("--safe-bottom"), left: css("--safe-left") },
      handBand, instanceId, resizeRevision + 1);
    const fingerprint = JSON.stringify({ ...value, resizeRevision: 0 });
    if (fingerprint === viewportFingerprint) return;
    viewportFingerprint = fingerprint;
    resizeRevision++;
    canvas.width = value.widthPx;
    canvas.height = value.heightPx;
    send("SetViewport", value);
  };
  const callback = {
    emit(json) {
      if (disposed) return;
      let event;
      try { event = JSON.parse(json); } catch { fail(new Error("Unity emitted malformed renderer JSON.")); return; }
      if (event.contractVersion !== "1.0.0" || event.instanceId !== instanceId) return;
      if (event.type === "ready") {
        if (event.buildId !== manifest.buildId || event.unityVersion !== "6000.3.24f1")
          return fail(new Error("Unity Ready does not match the real build."));
        ready = true;
        readyResolve();
      } else if (event.type === "error") {
        fail(new Error(event.code + ": " + event.message));
      } else if (event.sessionId === sessionId) {
        if (event.type === "frameRendered" && frame && layout &&
            event.frameId === frame.frameId && event.layoutRevision === layout.layoutRevision) {
          document.documentElement.dataset.unityRendererReady = "true";
          status.value = "";
        }
        if (event.type === "action" || event.type === "frameRendered")
          window.dispatchEvent(new CustomEvent("mahjong-unity-renderer", { detail: event }));
      }
    }
  };
  if (window.MahjongUnityBridge) throw new Error("Another browser renderer bridge already owns this page.");
  window.MahjongUnityBridge = callback;
  const api = {
    instanceId, sessionId,
    applyLayout(value) {
      layout = value;
      if (ready) send("ApplyLayout", value);
    },
    applyFrame(value) {
      frame = value;
      handBand = value.presentation.handLayout === "separate" ?
        Math.min(112, canvas.getBoundingClientRect().height * 0.3) : 0;
      if (ready) { updateViewport(); send("ApplyFrame", value); }
    },
    actionResult(value) { if (ready) send("ReceiveActionResult", value); },
    async dispose() {
      if (disposed) return;
      if (ready) send("Clear", { contractVersion: "1.0.0", instanceId, sessionId: crypto.randomUUID(), type: "clear", reason: "dispose" });
      disposed = true;
      ready = false;
      frame = null;
      layout = null;
      abort.abort();
      observer.disconnect();
      if (window.MahjongUnityBridge === callback) delete window.MahjongUnityBridge;
      if (window.MahjongRendererSourceHost === api) delete window.MahjongRendererSourceHost;
      if (unity) await unity.Quit();
    }
  };
  window.MahjongRendererSourceHost = api;
  const observer = new ResizeObserver(() => { try { updateViewport(); } catch (error) { fail(error); } });
  observer.observe(canvas);
  window.addEventListener("resize", updateViewport, { signal: abort.signal });
  window.addEventListener("orientationchange", updateViewport, { signal: abort.signal });
  window.visualViewport?.addEventListener("resize", updateViewport, { signal: abort.signal });
  canvas.addEventListener("contextmenu", event => event.preventDefault(), { signal: abort.signal });
  document.addEventListener("visibilitychange", () => {
    if (ready) send("SetPlayback", { contractVersion: "1.0.0", instanceId, type: "playback", suspended: document.hidden });
    if (!document.hidden) updateViewport();
  }, { signal: abort.signal });
  try {
    const response = await fetch("renderer-build.json", { cache: "no-cache", signal: abort.signal });
    if (!response.ok) throw new Error("Actual Unity build manifest HTTP " + response.status + ".");
    manifest = validateManifest(await response.json());
    const base = new URL(".", response.url);
    const url = path => {
      const resolved = new URL(path, base);
      if (resolved.origin !== location.origin) throw new Error("Unity artifacts must be same-origin.");
      return resolved.href;
    };
    await new Promise((resolve, reject) => {
      const script = document.createElement("script");
      script.src = url(manifest.loader.path);
      script.onload = resolve;
      script.onerror = () => reject(new Error("Actual Unity loader is unavailable."));
      document.head.append(script);
    });
    if (typeof window.createUnityInstance !== "function") throw new Error("Actual Unity loader entry point is missing.");
    unity = await window.createUnityInstance(canvas, {
      dataUrl: url(manifest.data.path), frameworkUrl: url(manifest.framework.path), codeUrl: url(manifest.wasm.path),
      streamingAssetsUrl: manifest.streamingAssetsPath === null ? "" : url(manifest.streamingAssetsPath),
      companyName: "MahjongRenderer", productName: "MahjongRenderer", productVersion: "1.0.0",
      matchWebGLToCanvasSize: false, devicePixelRatio: Math.min(window.devicePixelRatio || 1, 2)
    }, progress => { status.value = "Loading actual Unity: " + Math.round(progress * 100) + "%"; });
    const timeout = setTimeout(() => readyReject(new Error("Unity Configure/Ready handshake timed out.")), 15000);
    send("Configure", { contractVersion: "1.0.0", instanceId, type: "configure", buildId: manifest.buildId });
    await awaitingReady;
    clearTimeout(timeout);
    send("Clear", { contractVersion: "1.0.0", instanceId, sessionId, type: "clear", reason: "join" });
    updateViewport();
    if (layout) send("ApplyLayout", layout);
    if (frame) send("ApplyFrame", frame);
    status.value = "Unity loaded; waiting for a viewer-safe complete layout/frame.";
  } catch (error) {
    fail(error);
  }
}

if (typeof window !== "undefined" && typeof document !== "undefined") {
  window.addEventListener("DOMContentLoaded", () => { boot().catch(error => {
    const status = document.getElementById("renderer-status");
    status.value = "Unity renderer unavailable: " + error.message;
    status.dataset.failure = "true";
  }); }, { once: true });
}
