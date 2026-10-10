#!/usr/bin/env python3
"""Verify source invariants only. This never generates or certifies a Unity build."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

from asset_metadata import guid


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--frozen-contract", type=Path)
    args = parser.parse_args()
    project = Path(__file__).resolve().parents[1]
    root = project.parent.parent
    load = lambda relative: json.loads((project / relative).read_text(encoding="utf-8"))
    schema = load("Assets/Resources/renderer-v1.schema.json")
    if args.frozen_contract:
        frozen = json.loads(args.frozen_contract.read_text(encoding="utf-8"))
        require(schema == frozen["renderer"]["schema"], "Embedded schema differs from frozen renderer schema.")
        require(load("Assets/Resources/renderer-fixtures.json") == frozen["renderer"]["syntheticSchemaFixtures"],
                "Embedded fixtures differ from explicitly synthetic frozen fixtures.")
    published = root / "contracts/renderer-v1.schema.json"
    if published.is_file():
        require(schema == json.loads(published.read_text()), "Published schema differs from embedded frozen schema.")
    require(schema["$id"] == "urn:mahjong-unity:renderer:1.0.0", "Schema version drift.")
    require((project / "ProjectSettings/ProjectVersion.txt").read_text().splitlines() == [
        "m_EditorVersion: 6000.3.24f1",
        "m_EditorVersionWithRevision: 6000.3.24f1 (4e7b9b5b6244)"
    ], "Editor/version revision drift.")
    manifest = load("Packages/manifest.json")["dependencies"]
    require(manifest == {
        "com.unity.nuget.newtonsoft-json": "3.2.1", "com.unity.test-framework": "1.4.6",
        "com.unity.modules.imgui": "1.0.0", "com.unity.modules.physics": "1.0.0"
    }, "Package inventory/pins drift.")
    runtime = load("Assets/Renderer/Mahjong.Renderer.asmdef")
    editor = load("Assets/Editor/Mahjong.Renderer.Editor.asmdef")
    tests = load("Assets/Tests/Editor/Mahjong.Renderer.Tests.asmdef")
    require(runtime["references"] == ["Unity.Newtonsoft.Json"] and not runtime["includePlatforms"],
            "Runtime assembly must explicitly reference Newtonsoft, not editor assemblies.")
    require(editor["includePlatforms"] == ["Editor"] and runtime["name"] in editor["references"], "Editor assembly drift.")
    require(tests["includePlatforms"] == ["Editor"] and "TestAssemblies" in tests["optionalUnityReferences"], "Test assembly drift.")
    ET.parse(project / "Assets/Renderer/link.xml")
    scene = (project / "Assets/Scenes/MahjongRenderer.unity").read_text()
    require(scene.count("m_Name: MahjongBridge") == 1, "Source scene must connect exactly one bridge receiver.")
    require("guid: " + guid("Assets/Renderer/Bridge/MahjongBridge.cs") in scene, "Scene receiver script reference drift.")
    require("guid: " + guid("Assets/Renderer/Art/Surface.shader") in scene, "Scene original shader reference drift.")
    settings = (project / "ProjectSettings/EditorBuildSettings.asset").read_text()
    require("path: Assets/Scenes/MahjongRenderer.unity" in settings and
            "guid: " + guid("Assets/Scenes/MahjongRenderer.unity") in settings, "Build scene disconnected.")
    bridge = (project / "Assets/Renderer/Bridge/MahjongBridge.cs").read_text()
    expected = {"Configure", "Clear", "ApplyLayout", "ApplyFrame", "SetViewport", "SetPlayback", "ReceiveActionResult"}
    methods = set(re.findall(r"public void (\w+)\(string json\)", bridge))
    require(methods == expected, "Frozen SendMessage receiver method inventory drift.")
    require("WaitForEndOfFrame" in bridge and "Camera.onPostRender" in bridge and
            "tableGeneration != scene.Generation" in bridge and "handGeneration != scene.Generation" in bridge,
            "FrameRendered must require actual camera/end-of-frame receipts.")
    plugin = (project / "Assets/Plugins/WebGL/MahjongBridge.jslib").read_text()
    require("window.MahjongUnityBridge" in plugin and "bridge.emit(UTF8ToString(pointer))" in plugin,
            "Frozen UTF8 browser callback drift.")
    require(not re.search(r"\beval\s*\(|new\s+Function\s*\(", plugin), "Eval-based plugin is prohibited.")
    producer = (project / "Assets/Editor/WebGlBuild.cs").read_text()
    for token in ["BuildPipeline.BuildPlayer", "BuildResult.Succeeded", "report.summary.totalErrors != 0",
                  "BuildTarget.WebGL", "ScriptingImplementation.IL2CPP", "GraphicsDeviceType.OpenGLES3",
                  "WebGL.compressionFormat = WebGLCompressionFormat.Disabled",
                  "WebGL.decompressionFallback = false", "WebGL.threadsSupport = false", "WebGL.wasm2023 = false",
                  "InternalEditorUtility.GetFullUnityVersion()", "rendererSourceTreeSha256", "SHA256.Create()"]:
        require(token in producer, "Actual editor build gate missing: " + token)
    require(producer.index("BuildPipeline.BuildPlayer") < producer.index("File.WriteAllText(manifestPath"),
            "Success manifest must be written only after actual BuildPlayer/report/integrity gates.")
    state = (project / "Assets/Renderer/Contract/RendererState.cs").read_text()
    require("message.FrameId <= lastFrameId" in state and "lastFrameId = 0" in state,
            "Frame sequence must not reset on layout replacement.")
    forbidden = re.compile(r"\b(UnityWebRequest|HttpClient|ClientWebSocket|TcpClient|PhotonNetwork)\b")
    for file in (project / "Assets/Renderer").rglob("*.cs"):
        require(not forbidden.search(file.read_text()), "Runtime renderer networking is prohibited: " + str(file.relative_to(project)))
    for ending in (".wasm", ".data", ".loader.js", ".framework.js"):
        require(not any((project / "Assets").rglob("*" + ending)), "Generated/fabricated player output is forbidden in source Assets.")
    tag = (project / "ProjectSettings/TagManager.asset").read_text()
    layer_block = tag.split("  layers:\n", 1)[1].split("  m_SortingLayers:", 1)[0]
    layers = [line[4:] for line in layer_block.splitlines() if line.startswith("  -")]
    require(len(layers) == 32 and layers[8:12] == ["MahjongTable", "MahjongHand", "MahjongTableSlots", "MahjongHandSlots"],
            "Renderer camera/raycast layer indices drift.")
    subprocess.run([sys.executable, str(project / "Tools/asset_metadata.py")], check=True)
    digest = hashlib.sha256(json.dumps(schema, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    print("Source contract/project gates passed; canonical schema SHA256=" + digest)
    print("NOT Unity compilation, package restore, EditMode execution, WebGL output, browser or device qualification.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, AssertionError, subprocess.CalledProcessError) as error:
        print("Source check FAILED: " + str(error), file=sys.stderr)
        sys.exit(1)
