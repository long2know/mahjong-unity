import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import vm from "node:vm";
import { measuredViewport, safeArtifactPath, validateManifest } from "../Assets/WebGLTemplates/MahjongHost/host.mjs";

const pluginSource = await readFile(new URL("../Assets/Plugins/WebGL/MahjongBridge.jslib", import.meta.url), "utf8");

function plugin(callback) {
  const library = {};
  const diagnostics = [];
  const context = {
    window: callback === undefined ? {} : { MahjongUnityBridge: { emit: callback } },
    console: { error: message => diagnostics.push(message) },
    LibraryManager: { library },
    mergeInto: (target, values) => Object.assign(target, values),
    UTF8ToString: value => value
  };
  vm.runInNewContext(pluginSource, context, { filename: "actual source MahjongBridge.jslib" });
  return { emit: library.MahjongUnityEmit, diagnostics };
}

test("source-only jslib sends exact JSON once through frozen UTF8 callback", () => {
  const events = [];
  const p = plugin(value => events.push(value));
  const json = '{"contractVersion":"1.0.0","type":"frameRendered","instanceId":"source","sessionId":"epoch","frameId":7,"layoutRevision":2}';
  assert.equal(p.emit(json), 1);
  assert.deepEqual(events, [json]);
  assert.deepEqual(p.diagnostics, []);
});

test("source-only jslib fails explicitly without callback and never prints DTO data", () => {
  const p = plugin();
  assert.equal(p.emit('{"privateFace":"not-for-logs"}'), 0);
  assert.equal(p.diagnostics.length, 1);
  assert.ok(!p.diagnostics[0].includes("privateFace"));
  const throwing = plugin(() => { throw new Error("browser error"); });
  assert.equal(throwing.emit("{}"), 0);
  assert.equal(throwing.diagnostics.length, 1);
});

test("source viewport math caps DPR and preserves exact frozen fields after rotation", () => {
  const portrait = measuredViewport({ width: 390, height: 844 }, 3,
    { top: 20, right: 8, bottom: 16, left: 8 }, 112, "instance", 1);
  assert.equal(portrait.widthPx, 780);
  assert.equal(portrait.heightPx, 1688);
  assert.equal(portrait.renderPixelRatio, 2);
  assert.equal(portrait.devicePixelRatio, 3);
  const landscape = measuredViewport({ width: 844, height: 390 }, 1,
    { top: 0, right: 0, bottom: 0, left: 0 }, 0, "instance", 2);
  assert.equal(landscape.widthPx, 844);
  assert.equal(landscape.handBandCssPx, 0);
  assert.equal(landscape.resizeRevision, 2);
  assert.throws(() => measuredViewport({ width: 390, height: 100 }, 2,
    { top: 0, right: 0, bottom: 0, left: 0 }, 112, "instance", 3));
});

test("source host artifact paths cannot traverse or escape origin", () => {
  for (const path of ["/Build/x.wasm", "../x.wasm", "Build/../x.wasm", "Build//x.wasm",
    "https://other.test/x.wasm", "Build/%2e%2e/x.wasm", "Build\\x.wasm", "./Build/x.wasm"])
    assert.equal(safeArtifactPath(path), false, path);
  assert.equal(safeArtifactPath("Build/MahjongRenderer.wasm"), true);
});

test("source host requires actual artifact metadata, not an implicit generated-file fallback", () => {
  const metadata = {
    artifactKind: "unity-webgl", contractVersion: "1.0.0", unityVersion: "6000.3.24f1",
    unityRevision: "4e7b9b5b6244", compression: "none", threading: "single-threaded", buildId: "source-test-only",
    developmentBuild: false, baselineRevision: "d10d351e543aa3e341960de9800828b6c0662a82",
    rendererSourceRevision: "a".repeat(40), rendererSourceTreeSha256: "b".repeat(64), sourceDirty: true,
    builtAtUtc: "2026-10-10T00:00:00Z", streamingAssetsPath: null
  };
  for (const [purpose, suffix] of [["loader", "loader.js"], ["framework", "framework.js"], ["data", "data"], ["wasm", "wasm"]])
    metadata[purpose] = { path: "Build/source-test-only." + suffix, bytes: 1, sha256: "c".repeat(64) };
  assert.equal(validateManifest(metadata), metadata);
  const missing = structuredClone(metadata);
  delete missing.wasm;
  assert.throws(() => validateManifest(missing), /wasm/);
  const incompatible = structuredClone(metadata);
  incompatible.unityVersion = "6000.0.1f1";
  assert.throws(() => validateManifest(incompatible), /incompatible/);
  const duplicate = structuredClone(metadata);
  duplicate.data.path = duplicate.wasm.path;
  assert.throws(() => validateManifest(duplicate));
});
