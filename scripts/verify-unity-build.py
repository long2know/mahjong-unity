#!/usr/bin/env python3
"""Validate the frozen BuildManifest and its uncompressed Unity Web artifacts."""

import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys


ROOT = Path(__file__).resolve().parent.parent
CONTRACT_VERSION = "1.0.0"
UNITY_VERSION = "6000.3.24f1"
UNITY_REVISION = "4e7b9b5b6244"
BASELINE_REVISION = "d10d351e543aa3e341960de9800828b6c0662a82"
PURPOSE_SUFFIXES = {
    "loader": ".loader.js",
    "framework": ".framework.js",
    "wasm": ".wasm",
    "data": ".data",
}
REAL_BUILD_COMMAND = (
    "UNITY_EDITOR=/approved/Unity/Hub/Editor/6000.3.24f1/Editor/Unity "
    "bash scripts/build-unity-webgl.sh"
)
CONTAINER_BUILD_COMMAND = "bash scripts/build-unity-docker.sh --test-and-build"


class ValidationFailure(ValueError):
    pass


def read_json(path):
    def unique_keys(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValidationFailure(f"Duplicate JSON property: {key}")
            result[key] = value
        return result

    try:
        return json.loads(
            path.read_text(encoding="utf-8-sig"),
            object_pairs_hook=unique_keys,
            parse_constant=lambda value: (_ for _ in ()).throw(ValidationFailure("Non-finite JSON number")),
        )
    except (UnicodeError, json.JSONDecodeError) as error:
        raise ValidationFailure(f"Invalid JSON in {path.name}: {error}") from error


def validate_schema(value, rule, schema, location):
    if "$ref" in rule:
        prefix = "#/definitions/"
        if not rule["$ref"].startswith(prefix):
            raise ValidationFailure("Unsupported frozen schema reference.")
        return validate_schema(value, schema["definitions"][rule["$ref"][len(prefix):]], schema, location)
    if "anyOf" in rule:
        for candidate in rule["anyOf"]:
            try:
                validate_schema(value, candidate, schema, location)
                return
            except ValidationFailure:
                pass
        raise ValidationFailure(f"{location}: no permitted schema value matched.")
    if "const" in rule and (type(value) is not type(rule["const"]) or value != rule["const"]):
        raise ValidationFailure(f"{location}: expected pinned value {rule['const']!r}.")
    expected = rule.get("type")
    valid_types = {
        "object": lambda item: type(item) is dict,
        "string": lambda item: type(item) is str,
        "integer": lambda item: type(item) is int,
        "boolean": lambda item: type(item) is bool,
        "null": lambda item: item is None,
    }
    if expected and (expected not in valid_types or not valid_types[expected](value)):
        raise ValidationFailure(f"{location}: expected {expected}.")
    if expected == "object":
        properties = rule.get("properties", {})
        missing = set(rule.get("required", [])) - set(value)
        if missing:
            raise ValidationFailure(f"{location}: missing properties {', '.join(sorted(missing))}.")
        if rule.get("additionalProperties") is False:
            additional = set(value) - set(properties)
            if additional:
                raise ValidationFailure(f"{location}: unexpected properties {', '.join(sorted(additional))}.")
        for name, child in value.items():
            if name in properties:
                validate_schema(child, properties[name], schema, f"{location}.{name}")
    if expected == "string":
        if not rule.get("minLength", 0) <= len(value) <= rule.get("maxLength", sys.maxsize):
            raise ValidationFailure(f"{location}: invalid string length.")
        if "pattern" in rule and re.search(rule["pattern"], value) is None:
            raise ValidationFailure(f"{location}: invalid schema pattern.")
    if expected == "integer" and not rule.get("minimum", -sys.maxsize) <= value <= rule.get("maximum", sys.maxsize):
        raise ValidationFailure(f"{location}: integer outside schema bounds.")


def no_symlinks(path):
    absolute = path.absolute()
    for candidate in reversed((absolute, *absolute.parents)):
        if candidate.is_symlink():
            raise ValidationFailure(f"Symlink is not an artifact input: {path.name}")


def relative_path(value, purpose):
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9_./-]+", value):
        raise ValidationFailure(f"{purpose}.path: unsafe relative path.")
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in ("", ".", "..") for part in value.split("/")):
        raise ValidationFailure(f"{purpose}.path: traversal/absolute/noncanonical paths are forbidden.")
    return path


def digest(path):
    hasher = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest()


def frozen_schema():
    schema = read_json(ROOT / "contracts/renderer-v1.schema.json")
    contract = read_json(ROOT / "contracts/migration-contract.json")
    if schema != contract["renderer"]["schema"]:
        raise ValidationFailure("Renderer schema drift: schema and accepted migration contract differ.")
    pins = {
        "contractVersion": CONTRACT_VERSION,
        "unityVersion": UNITY_VERSION,
        "unityRevision": UNITY_REVISION,
        "baselineRevision": BASELINE_REVISION,
        "compression": "none",
        "threading": "single-threaded",
        "artifactKind": "unity-webgl",
    }
    properties = schema["definitions"]["BuildManifest"]["properties"]
    if any(properties[key].get("const") != value for key, value in pins.items()):
        raise ValidationFailure("Frozen BuildManifest toolchain/contract pins do not match this consumer.")
    if (contract["contractVersion"] != CONTRACT_VERSION
            or contract["unityBuild"]["pinnedEditor"] != UNITY_VERSION
            or contract["unityBuild"]["pinnedRevision"] != UNITY_REVISION):
        raise ValidationFailure("Accepted editor/contract pin mismatch.")
    return schema


def inspect_tree(root):
    files = []
    names = set()
    forbidden_suffixes = {".ulf", ".alf", ".pem", ".pfx", ".p12", ".key", ".db", ".sqlite", ".sqlite3"}
    for path in sorted(root.rglob("*")):
        no_symlinks(path)
        relative = path.relative_to(root)
        key = relative.as_posix().casefold()
        if key in names:
            raise ValidationFailure(f"Case-insensitive artifact tree collision: {relative}")
        names.add(key)
        if (any(part.startswith(".") for part in relative.parts)
                or path.name == "appsettings.Development.json"
                or path.suffix.lower() in forbidden_suffixes):
            raise ValidationFailure(f"Private/hidden configuration is not a build artifact: {relative}")
        mode = path.stat().st_mode
        if stat.S_ISDIR(mode):
            continue
        if not stat.S_ISREG(mode):
            raise ValidationFailure(f"Non-regular artifact file: {relative}")
        files.append(relative.as_posix())
    return files


def verify(manifest_path, allow_source_dirty=False):
    manifest_path = Path(manifest_path)
    no_symlinks(manifest_path)
    if not manifest_path.is_file():
        raise ValidationFailure(f"Unity build unavailable: missing manifest {manifest_path}")
    root = manifest_path.parent.resolve()
    schema = frozen_schema()
    manifest = read_json(manifest_path)
    validate_schema(manifest, schema["definitions"]["BuildManifest"], schema, "BuildManifest")
    if manifest["developmentBuild"]:
        raise ValidationFailure("BuildManifest.developmentBuild: production artifacts are required.")
    if manifest["sourceDirty"] and not allow_source_dirty:
        raise ValidationFailure(
            "BuildManifest.sourceDirty: release rejects dirty source; "
            "--allow-source-dirty (or ALLOW_SOURCE_DIRTY=1) is explicit local qualification only."
        )
    try:
        built = datetime.datetime.fromisoformat(manifest["builtAtUtc"].replace("Z", "+00:00"))
    except ValueError as error:
        raise ValidationFailure("BuildManifest.builtAtUtc: expected an ISO8601 UTC timestamp.") from error
    if built.tzinfo is None or built.utcoffset() != datetime.timedelta(0):
        raise ValidationFailure("BuildManifest.builtAtUtc: expected UTC, not local/unspecified time.")

    unique = {manifest_path.name.casefold()}
    verified = {}
    for purpose, suffix in PURPOSE_SUFFIXES.items():
        artifact = manifest[purpose]
        relative = relative_path(artifact["path"], purpose)
        key = relative.as_posix().casefold()
        if key in unique:
            raise ValidationFailure(f"BuildManifest.{purpose}.path: duplicate artifact purpose/path.")
        unique.add(key)
        if not relative.name.endswith(suffix):
            raise ValidationFailure(f"BuildManifest.{purpose}.path: required uncompressed {suffix} artifact.")
        path = root.joinpath(*relative.parts)
        no_symlinks(path)
        if not path.is_file() or not path.resolve().is_relative_to(root):
            raise ValidationFailure(f"BuildManifest.{purpose}.path: missing/non-regular artifact {relative}")
        length = path.stat().st_size
        if length < 1 or length != artifact["bytes"]:
            raise ValidationFailure(f"BuildManifest.{purpose}.bytes: size mismatch for {relative}")
        actual_digest = digest(path)
        if actual_digest != artifact["sha256"]:
            raise ValidationFailure(f"BuildManifest.{purpose}.sha256: digest mismatch for {relative}")
        if purpose == "wasm":
            with path.open("rb") as stream:
                if stream.read(8) != b"\0asm\x01\0\0\0":
                    raise ValidationFailure(f"BuildManifest.wasm: invalid WebAssembly magic/version in {relative}")
        verified[purpose] = {"path": relative.as_posix(), "bytes": length, "sha256": actual_digest}

    streaming = manifest["streamingAssetsPath"]
    if streaming is not None:
        relative = relative_path(streaming, "streamingAssetsPath")
        path = root.joinpath(*relative.parts)
        no_symlinks(path)
        if not path.is_dir() or not path.resolve().is_relative_to(root):
            raise ValidationFailure("BuildManifest.streamingAssetsPath: missing/non-directory streaming assets.")
    tree_files = inspect_tree(root)
    return {
        "status": "integrity-verified",
        "buildId": manifest["buildId"],
        "contractVersion": manifest["contractVersion"],
        "unityVersion": manifest["unityVersion"],
        "unityRevision": manifest["unityRevision"],
        "baselineRevision": manifest["baselineRevision"],
        "rendererSourceRevision": manifest["rendererSourceRevision"],
        "rendererSourceTreeSha256": manifest["rendererSourceTreeSha256"],
        "sourceDirty": manifest["sourceDirty"],
        "allowSourceDirty": allow_source_dirty,
        "developmentBuild": manifest["developmentBuild"],
        "files": verified,
        "artifactTreeFileCount": len(tree_files),
        "qualification": "Manifest/file integrity only; actual editor provenance and browser/device behavior require separate evidence.",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", nargs="?", default="artifacts/unity-webgl/renderer-build.json")
    parser.add_argument("--allow-source-dirty", action="store_true", help="Local qualification only; no integrity/toolchain/development bypass")
    parser.add_argument("--json", action="store_true", help="Print the verified inventory instead of a short receipt")
    args = parser.parse_args()
    try:
        flag = os.environ.get("ALLOW_SOURCE_DIRTY", "0")
        if flag not in ("0", "1"):
            raise ValidationFailure("ALLOW_SOURCE_DIRTY must be exactly 0 or 1.")
        report = verify(args.manifest, args.allow_source_dirty or flag == "1")
        if args.json:
            print(json.dumps(report, indent=2))
        else:
            disclosure = " [LOCAL DIRTY-SOURCE QUALIFICATION]" if report["sourceDirty"] else ""
            print(f"Verified Unity Web build {report['buildId']} ({UNITY_VERSION}, contract {CONTRACT_VERSION}){disclosure}")
    except (ValidationFailure, OSError, KeyError, TypeError) as error:
        print(f"Unity build validation failed: {error}", file=sys.stderr)
        print(f"Generate real outputs on a provisioned licensed editor:\n  {REAL_BUILD_COMMAND}", file=sys.stderr)
        print(f"Repo-managed Docker alternative (requires privately provisioned valid licensing):\n  {CONTAINER_BUILD_COMMAND}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
