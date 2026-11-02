#!/usr/bin/env python3
"""Reproducible source .meta generation/checking, not Unity import/build evidence."""

import hashlib
from pathlib import Path
import sys


def guid(relative):
    return hashlib.sha256(("mahjong-renderer-source-v1:" + relative).encode()).hexdigest()[:32]


def metadata(path, root):
    relative = path.relative_to(root).as_posix()
    base = f"fileFormatVersion: 2\nguid: {guid(relative)}\n"
    if path.is_dir():
        return base + "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n"
    extension = path.suffix
    if extension == ".cs":
        return base + "MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n"
    if extension == ".shader":
        return base + "ShaderImporter:\n  externalObjects: {}\n  defaultTextures: []\n  nonModifiableTextures: []\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n"
    if extension == ".asmdef":
        importer = "AssemblyDefinitionImporter"
    elif extension == ".svg":
        return base + (
            "ScriptedImporter:\n  internalIDToNameTable: []\n  externalObjects: {}\n"
            "  serializedVersion: 2\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
            "  script: {fileID: 12408, guid: 0000000000000000e000000000000000, type: 0}\n"
            "  svgType: 3\n  texturedSpriteMeshType: 0\n  svgPixelsPerUnit: 100\n"
            "  gradientResolution: 64\n  alignment: 0\n  customPivot: {x: 0, y: 0}\n"
            "  generatePhysicsShape: 0\n  viewportOptions: 0\n  preserveViewport: 0\n"
            "  advancedMode: 0\n  tessellationMode: 1\n  predefinedResolutionIndex: 1\n"
            "  targetResolution: 1080\n  resolutionMultiplier: 1\n  stepDistance: 10\n"
            "  samplingStepDistance: 100\n  maxCordDeviationEnabled: 0\n  maxCordDeviation: 1\n"
            "  maxTangentAngleEnabled: 0\n  maxTangentAngle: 5\n  keepTextureAspectRatio: 1\n"
            "  textureSize: 256\n  textureWidth: 256\n  textureHeight: 256\n  wrapMode: 0\n"
            "  filterMode: 1\n  sampleCount: 4\n  preserveSVGImageAspect: 0\n  useSVGPixelsPerUnit: 0\n"
            "  spriteData:\n    TessellationDetail: 0\n    SpriteName: \n"
            "    SpritePivot: {x: 0, y: 0}\n    SpriteAlignment: 0\n"
            "    SpriteBorder: {x: 0, y: 0, z: 0, w: 0}\n    SpriteRect:\n"
            "      serializedVersion: 2\n      x: 0\n      y: 0\n      width: 0\n      height: 0\n"
            "    SpriteID: \n    PhysicsOutlines: []\n"
        )
    elif extension in {".png", ".jpg"}:
        runtime = relative.startswith("Assets/Resources/Art/")
        fabric = path.name == "TableFabric.jpg"
        return base + (
            "TextureImporter:\n  internalIDToNameTable: []\n  externalObjects: {}\n"
            "  serializedVersion: 13\n  mipmaps:\n    mipMapMode: 0\n"
            f"    enableMipMap: {int(runtime)}\n    sRGBTexture: 1\n"
            "    linearTexture: 0\n    fadeOut: 0\n    borderMipMap: 0\n"
            "    mipMapsPreserveCoverage: 0\n    alphaTestReferenceValue: 0.5\n"
            "    mipMapFadeDistanceStart: 1\n    mipMapFadeDistanceEnd: 3\n"
            "  bumpmap:\n    convertToNormalMap: 0\n    externalNormalMap: 0\n"
            "    heightScale: 0.25\n    normalMapFilter: 0\n"
            "  isReadable: 0\n  streamingMipmaps: 0\n  streamingMipmapsPriority: 0\n"
            "  vTOnly: 0\n  ignoreMipmapLimit: 0\n  grayScaleToAlpha: 0\n"
            "  generateCubemap: 6\n  cubemapConvolution: 0\n  seamlessCubemap: 0\n"
            "  textureFormat: 1\n  maxTextureSize: 2048\n"
            "  textureSettings:\n    serializedVersion: 2\n"
            f"    filterMode: {2 if runtime else 1}\n    aniso: {4 if fabric else 1}\n    mipBias: 0\n"
            f"    wrapU: {0 if fabric else 1}\n    wrapV: {0 if fabric else 1}\n    wrapW: 1\n"
            "  nPOTScale: 0\n  lightmap: 0\n  compressionQuality: 50\n"
            "  spriteMode: 0\n  spriteExtrude: 1\n  spriteMeshType: 1\n"
            "  alignment: 0\n  spritePivot: {x: 0.5, y: 0.5}\n  spritePixelsToUnits: 100\n"
            "  spriteBorder: {x: 0, y: 0, z: 0, w: 0}\n"
            f"  alphaUsage: 1\n  alphaIsTransparency: {int(not fabric)}\n"
            "  spriteTessellationDetail: -1\n  textureType: 0\n  textureShape: 1\n"
            "  singleChannelComponent: 0\n  flipbookRows: 1\n  flipbookColumns: 1\n"
            "  maxTextureSizeSet: 0\n  compressionQualitySet: 0\n  textureFormatSet: 0\n"
            "  ignorePngGamma: 0\n  applyGammaDecoding: 0\n"
            "  platformSettings:\n  - serializedVersion: 4\n    buildTarget: DefaultTexturePlatform\n"
            "    maxTextureSize: 2048\n    resizeAlgorithm: 0\n    textureFormat: -1\n"
            "    textureCompression: 1\n    compressionQuality: 50\n"
            "    crunchedCompression: 0\n    allowsAlphaSplitting: 0\n    overridden: 0\n"
            "    ignorePlatformSupport: 0\n    androidETC2FallbackOverride: 0\n"
            "    forceMaximumCompressionQuality_BC6H_BC7: 0\n"
            "  spriteSheet:\n    serializedVersion: 2\n    sprites: []\n    outline: []\n"
            "    physicsShape: []\n    bones: []\n    spriteID:\n    internalID: 0\n"
            "    vertices: []\n    indices:\n    edges: []\n    weights: []\n    secondaryTextures: []\n"
            "    nameFileIdTable: {}\n  mipmapLimitGroupName:\n"
            "  pSDRemoveMatte: 0\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n"
        )
    elif extension in {".json", ".txt", ".xml", ".html", ".js", ".css"}:
        importer = "TextScriptImporter"
    elif extension == ".jslib":
        return base + (
            "PluginImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  iconMap: {}\n"
            "  executionOrder: {}\n  defineConstraints: []\n  isPreloaded: 0\n"
            "  isOverridable: 0\n  isExplicitlyReferenced: 0\n  validateReferences: 1\n"
            "  platformData:\n  - first:\n      Any:\n    second:\n      enabled: 0\n      settings: {}\n"
            "  - first:\n      WebGL: WebGL\n    second:\n      enabled: 1\n      settings: {}\n"
            "  userData:\n  assetBundleName:\n  assetBundleVariant:\n"
        )
    else:
        importer = "DefaultImporter"
    return base + f"{importer}:\n  externalObjects: {{}}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n"


def main():
    root = Path(__file__).resolve().parents[1]
    write = sys.argv[1:] == ["--write"]
    errors = []
    assets = root / "Assets"
    for path in sorted(assets.rglob("*")):
        if path.suffix == ".meta" or "Tests/Migration" in path.as_posix():
            continue
        if path.name.startswith("renderer-build-identity"):
            continue
        target = Path(str(path) + ".meta")
        expected = metadata(path, root)
        if write:
            if target.exists() and target.read_text() != expected:
                errors.append("Refuse to overwrite nonmatching asset metadata: " + str(target.relative_to(root)))
            elif not target.exists():
                target.write_text(expected, encoding="utf-8")
        else:
            actual = target.read_text() if target.is_file() else ""
            relative = path.relative_to(root).as_posix()
            importer = expected.splitlines()[2]
            if "guid: " + guid(relative) not in actual or importer not in actual:
                errors.append("Missing/inconsistent source .meta: " + str(target.relative_to(root)))
            if path.suffix in {".png", ".jpg"}:
                for setting in ("  isReadable: 0", "  nPOTScale: 0", "    textureCompression: 1"):
                    if setting not in actual:
                        errors.append("Texture import intent drift: " + str(target.relative_to(root)) + ": " + setting.strip())
    if errors:
        raise SystemExit("\n".join(errors))
    print("Deterministic source asset metadata " + ("written." if write else "checked."))


if __name__ == "__main__":
    main()
