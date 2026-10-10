#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
PROJECT="$ROOT/unity/MahjongRenderer"
OUTPUT="$ROOT/artifacts/unity-webgl"
RESULTS="$ROOT/artifacts/test-results"
PIN="6000.3.24f1"
REVISION="4e7b9b5b6244"
MODE="${1:---build}"

fail() { printf 'Unity build unavailable: %s\n' "$*" >&2; exit 1; }
case "$MODE" in --build|--test|--test-and-build) ;; *) fail "usage: $0 [--build|--test|--test-and-build]" ;; esac
[[ $# -le 1 ]] || fail "only one build/test mode is accepted"
[[ ! -L "$OUTPUT" && ! -L "$ROOT/artifacts" && ! -L "$RESULTS" ]] || fail "linked output/evidence paths are not accepted"
invalidate_manifest() {
  local result=$?
  if [[ $result -ne 0 && -f "$OUTPUT/renderer-build.json" && ! -L "$OUTPUT/renderer-build.json" ]]; then
    rm -f -- "$OUTPUT/renderer-build.json"
  fi
}
trap invalidate_manifest EXIT
[[ -n "${UNITY_EDITOR:-}" ]] || fail "configure UNITY_EDITOR for a legitimately licensed Unity $PIN ($REVISION) with Web Build Support. No editor/image is installed or activated automatically."
case "$UNITY_EDITOR" in /*) ;; *) fail "UNITY_EDITOR must be an absolute executable path" ;; esac
[[ -f "$UNITY_EDITOR" && -x "$UNITY_EDITOR" ]] || fail "UNITY_EDITOR is not an executable file"
command -v python3 >/dev/null || fail "python3 is required in the approved build environment"
command -v git >/dev/null || fail "git is required for truthful renderer source provenance"
[[ -f "$PROJECT/ProjectSettings/ProjectVersion.txt" ]] || fail "renderer source project is missing"
EDITOR_PATH="$(readlink -f -- "$UNITY_EDITOR")"
MODULE="$(dirname -- "$EDITOR_PATH")/Data/PlaybackEngines/WebGLSupport"
[[ -d "$MODULE" ]] || fail "the configured editor has no WebGLSupport module"
# Unity and subordinate tools use only the mounted project cache, never host temporary paths.
mkdir -p -- "$PROJECT/BuildCache/tool-work" "$RESULTS"
export TMPDIR="$PROJECT/BuildCache/tool-work" TMP="$PROJECT/BuildCache/tool-work" TEMP="$PROJECT/BuildCache/tool-work"
export PYTHONDONTWRITEBYTECODE=1
cd -- "$ROOT"
VERSION="$("$UNITY_EDITOR" -version 2>&1)" || fail "the actual editor could not report its version"
printf '%s\n' "$VERSION" | grep -Eq "^${PIN//./\\.}([[:space:]]|$|\\()" || fail "configured editor is not pinned to $PIN"

if [[ "$MODE" == "--test" || "$MODE" == "--test-and-build" ]]; then
  [[ ! -L "$RESULTS/unity-editmode.xml" && ! -L "$RESULTS/unity-tests.log" ]] || fail "linked test evidence is not accepted"
  rm -f -- "$RESULTS/unity-editmode.xml"
  "$UNITY_EDITOR" -batchmode -nographics -projectPath "$PROJECT" -buildTarget WebGL \
    -runTests -testPlatform EditMode -testResults "$RESULTS/unity-editmode.xml" \
    -logFile "$RESULTS/unity-tests.log"
  python3 - "$RESULTS/unity-editmode.xml" <<'PY'
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
p = Path(sys.argv[1])
if not p.is_file() or p.stat().st_size == 0:
    raise SystemExit("Unity tests failed: actual editor XML is missing/empty.")
root = ET.parse(p).getroot()
cases = list(root.iter("test-case"))
failed = [c for c in cases if c.get("result") not in {"Passed", "Success"}]
if not cases or failed or root.get("result") not in {"Passed", "Success"}:
    raise SystemExit("Unity tests failed or did not execute every discovered case; inspect unity-editmode.xml.")
print(f"Actual Unity EditMode evidence: {len(cases)} passing cases.")
PY
fi

if [[ "$MODE" == "--build" || "$MODE" == "--test-and-build" ]]; then
  [[ ! -L "$RESULTS/unity-webgl-build.log" ]] || fail "linked build log is not accepted"
  if [[ -e "$OUTPUT/renderer-build.json" ]]; then
    [[ -f "$OUTPUT/renderer-build.json" && ! -L "$OUTPUT/renderer-build.json" ]] || fail "unsafe existing manifest"
    rm -f -- "$OUTPUT/renderer-build.json"
  fi
  "$UNITY_EDITOR" -batchmode -nographics -quit -projectPath "$PROJECT" -buildTarget WebGL \
    -executeMethod Mahjong.Renderer.Editor.WebGlBuild.Build -logFile "$RESULTS/unity-webgl-build.log"
  [[ -s "$OUTPUT/renderer-build.json" ]] || fail "actual editor did not produce a successful BuildReport/manifest"
  python3 - "$OUTPUT/renderer-build.json" <<'PY'
import hashlib
import json
from pathlib import Path
import re
import sys
p = Path(sys.argv[1])
m = json.loads(p.read_text())
if (m.get("artifactKind"), m.get("contractVersion"), m.get("unityVersion"), m.get("unityRevision"),
    m.get("compression"), m.get("threading"), m.get("developmentBuild")) != (
    "unity-webgl", "1.0.0", "6000.3.24f1", "4e7b9b5b6244", "none", "single-threaded", False):
    raise SystemExit("Unity build failed: manifest/toolchain drift.")
paths = set()
for purpose, ending in (("loader", ".loader.js"), ("framework", ".framework.js"), ("wasm", ".wasm"), ("data", ".data")):
    a = m.get(purpose, {})
    relative = a.get("path", "")
    if not re.fullmatch(r"[A-Za-z0-9_./-]+", relative) or relative.startswith("/") or any(
        part in {"", ".", ".."} for part in relative.split("/")) or not relative.endswith(ending) or relative in paths:
        raise SystemExit("Unity build failed: unsafe/duplicate artifact path for " + purpose)
    paths.add(relative)
    file = p.parent / relative
    if file.is_symlink() or not file.is_file() or file.stat().st_size <= 0 or file.stat().st_size != a.get("bytes"):
        raise SystemExit("Unity build failed: missing/empty real artifact for " + purpose)
    digest = hashlib.sha256()
    with file.open("rb") as stream:
        first = stream.read(8)
        if purpose == "wasm" and first != b"\x00asm\x01\x00\x00\x00":
            raise SystemExit("Unity build failed: WebAssembly magic/version mismatch.")
        digest.update(first)
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    if digest.hexdigest() != a.get("sha256"):
        raise SystemExit("Unity build failed: real artifact hash mismatch for " + purpose)
print("Actual Unity artifact integrity verified; browser/mobile qualification is separate.")
PY
fi
