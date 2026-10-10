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
    if errors:
        raise SystemExit("\n".join(errors))
    print("Deterministic source asset metadata " + ("written." if write else "checked."))


if __name__ == "__main__":
    main()
