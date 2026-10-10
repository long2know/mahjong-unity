#!/usr/bin/env python3
"""One-time, allowlisted import from the accepted baseline's immutable git objects."""

import argparse
import collections
import datetime
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys


BASELINE_REVISION = "d10d351e543aa3e341960de9800828b6c0662a82"
BASELINE_URL = "https://github.com/long2know/mahjong-autotable"
FRONTEND = "src/frontend/autotable-src/"
BACKEND = "src/backend/"
ROOT_FILES = frozenset({
    "Dockerfile", ".dockerignore", "docker-compose.yml",
    "docker-compose.postgres.yml", "docker-compose.sqlserver.yml",
    "build.sh", "build.ps1", "scripts/compose-bootstrap.sh",
})
FRONTEND_ROOT_FILES = frozenset({
    ".eslintrc.json", ".gitattributes", ".gitignore", "COPYING", "README.md",
    "UPSTREAM_SHA", "index.html", "about.html", "manifest.webmanifest",
    "package.json", "package-lock.json", "tsconfig.json", "vite.config.ts",
    "yarn.lock", "sw.js",
})
BACKEND_SUFFIXES = frozenset({".cs", ".csproj", ".slnx", ".props", ".targets", ".json", ".yaml", ".html", ".md"})
FRONTEND_SUBTREES = {
    "src": frozenset({".ts", ".css", ".html", ".json"}),
    "tests": frozenset({".ts", ".mjs", ".json", ".md"}),
    "scripts": frozenset({".js", ".json"}),
}
FORBIDDEN_PARTS = frozenset({
    ".git", ".squad", ".github", ".copilot", ".work", ".vscode", ".audit-work",
    "node_modules", "bin", "obj", "playtest-artifacts", "readme-res",
    "img", "sound", "static", "screenshots", "test-results", "playwright-report",
})
FORBIDDEN_SUFFIXES = frozenset({
    ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico", ".pdf", ".wav",
    ".mp3", ".ogg", ".flac", ".ttf", ".otf", ".woff", ".woff2", ".glb",
    ".gltf", ".fbx", ".blend", ".pfx", ".p12", ".pem", ".key", ".ulf",
})
CONFIG_FILES = frozenset({
    BACKEND + "src/Mahjong.Autotable.Api/appsettings.json",
    BACKEND + "src/Mahjong.Autotable.Api/appsettings.Production.json",
    BACKEND + "src/Mahjong.Autotable.Api/appsettings.Development.example.json",
    BACKEND + "src/Mahjong.Autotable.Api/Properties/launchSettings.json",
})


class ImportFailure(ValueError):
    pass


def git_command(repo, *arguments):
    # An explicitly supplied bare store must remain explicit under Git's
    # safe.bareRepository=explicit policy, rather than weakening that policy.
    root = Path(repo)
    if (root / "HEAD").is_file() and (root / "objects").is_dir():
        return ["git", "--git-dir", str(root), *arguments]
    return ["git", "-C", str(root), *arguments]


def git(repo, *arguments):
    result = subprocess.run(git_command(repo, *arguments), capture_output=True, check=False)
    if result.returncode:
        raise ImportFailure("Git object inspection failed; verify the repository and pinned revision.")
    return result.stdout


def safe_path(value, output=True):
    path = PurePosixPath(value)
    if (not value or path.is_absolute() or "\\" in value or output and ":" in value
            or any(part in ("", ".", "..") for part in value.split("/"))
            or any(ord(char) < 32 for char in value)):
        raise ImportFailure(f"Unsafe git/output path: {value!r}")
    return path


def exclusion_reason(path, mode):
    parts = safe_path(path, output=False).parts
    name = parts[-1]
    if mode not in ("100644", "100755"):
        return "symlink, submodule, or unsupported git mode"
    if name == ".mcp.json" or name == ".env" or name.startswith(".env."):
        return "secret/configuration or governance file"
    if name == "appsettings.Development.json":
        return "live development configuration; safe example only"
    if any(part in FORBIDDEN_PARTS for part in parts) or parts[0].startswith("Phase_"):
        return "excluded governance, generated output, or external asset tree"
    if path.startswith(FRONTEND) and any(
        part.lower().endswith(("-snapshots", "-screenshots")) or part.lower() == "screenshots"
        for part in parts[:-1]
    ):
        return "external screenshot/snapshot tree"
    if PurePosixPath(path).suffix.lower() in FORBIDDEN_SUFFIXES:
        return "external binary/art/audio/font/model/PDF or private material"
    if path in ROOT_FILES:
        return None
    if path.startswith(BACKEND):
        if PurePosixPath(path).suffix in BACKEND_SUFFIXES:
            return None
    if path.startswith(FRONTEND):
        relative = path[len(FRONTEND):]
        if relative in FRONTEND_ROOT_FILES or relative in ("server/protocol.ts", "sounds/CREDITS.md"):
            return None
        first = relative.split("/", 1)[0]
        if first in FRONTEND_SUBTREES and PurePosixPath(path).suffix in FRONTEND_SUBTREES[first]:
            return None
        if first == "server":
            return "legacy TypeScript server runtime; protocol.ts only"
    return "outside explicit source allowlist"


def inspect_tree(repo):
    revision = git(repo, "rev-parse", "--verify", BASELINE_REVISION + "^{commit}").decode().strip()
    if revision != BASELINE_REVISION:
        raise ImportFailure("Pinned commit mismatch.")
    entries = []
    for record in git(repo, "ls-tree", "-r", "-z", BASELINE_REVISION).split(b"\0"):
        if not record:
            continue
        metadata, raw_path = record.split(b"\t", 1)
        mode, kind, oid = metadata.decode("ascii").split()
        path = raw_path.decode("utf-8", "strict")
        safe_path(path, output=False)
        entries.append({"path": path, "mode": mode, "kind": kind, "gitBlob": oid})
    return entries


def read_blobs(repo, entries):
    # Batch reads are by object ID, never by mirror working-tree path.
    requests = b"".join(entry["gitBlob"].encode("ascii") + b"\n" for entry in entries)
    process = subprocess.run(
        git_command(repo, "cat-file", "--batch"),
        input=requests, capture_output=True, check=False,
    )
    if process.returncode:
        raise ImportFailure("Pinned blob batch read failed.")
    cursor = 0
    blobs = {}
    for entry in entries:
        end = process.stdout.index(b"\n", cursor)
        oid, kind, size = process.stdout[cursor:end].decode("ascii").split()
        length = int(size)
        if oid != entry["gitBlob"] or kind != "blob":
            raise ImportFailure(f"Unexpected git object for {entry['path']}.")
        start = end + 1
        blobs[entry["path"]] = process.stdout[start:start + length]
        cursor = start + length + 1
    if cursor != len(process.stdout):
        raise ImportFailure("Unexpected trailing git batch output.")
    return blobs


def sanitize_config(content):
    document = json.loads(content.decode("utf-8-sig"))
    changed = []

    def visit(value, location=""):
        if isinstance(value, dict):
            for key, item in value.items():
                field = f"{location}.{key}".lstrip(".")
                compact = re.sub(r"[^a-z]", "", key.lower())
                if not key.startswith("//") and (
                    compact in {"clientsecret", "apikey", "password", "pwd", "jwtsigningkey", "jwtsigningkeys",
                                "signingkey", "signingkeys", "secret", "token", "redisconnection", "dsn"}
                    or location.endswith("ConnectionStrings") and key != "Sqlite"
                ):
                    replacement = [] if isinstance(item, list) else ""
                    if isinstance(item, str) and item.startswith("env:"):
                        replacement = item
                    if item != replacement:
                        value[key] = replacement
                        changed.append(field)
                else:
                    visit(item, field)
        elif isinstance(value, list):
            for index, item in enumerate(value):
                visit(item, f"{location}[{index}]")

    visit(document)
    if changed:
        return (json.dumps(document, indent=2, ensure_ascii=False) + "\n").encode(), changed
    return content, []


def check_private_material(path, content):
    # Do not print matching values. Test keys/placeholders are source behavior,
    # not operator credentials; actual token/private-key signatures fail closed.
    signatures = (
        rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----[\r\n]+[A-Za-z0-9+/=\r\n]{64,}-----END ",
        rb"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{50,})\b",
        rb"\bAKIA[0-9A-Z]{16}\b",
        rb"\bsk-(?:proj-)?[A-Za-z0-9_-]{40,}\b",
    )
    if any(re.search(pattern, content) for pattern in signatures):
        raise ImportFailure(f"Credential-like private material detected in {path}; no files were imported.")


def check_destination(root, relative, existing_allowed=False):
    safe_path(relative)
    cursor = root
    for part in PurePosixPath(relative).parts:
        cursor = cursor / part
        if cursor.is_symlink():
            raise ImportFailure(f"Destination symlink collision: {relative}")
        if cursor == root / relative and cursor.is_file() and existing_allowed:
            continue
        if cursor.exists() and (cursor == root / relative or not cursor.is_dir()):
            raise ImportFailure(f"Destination collision; refusing to overwrite: {relative}")


def run(args):
    root = Path(args.destination)
    if root.is_absolute() or ".." in root.parts:
        raise ImportFailure("Destination must be relative to the current project directory.")
    root = Path.cwd() / root
    if root.is_symlink():
        raise ImportFailure("Destination root cannot be a symlink.")
    root = root.resolve()
    if not root.is_relative_to(Path.cwd().resolve()):
        raise ImportFailure("Destination must remain inside the current project directory.")
    contract_bytes = Path(args.contract).read_bytes()
    contract = json.loads(contract_bytes)
    pin = next(item for item in contract["sourcePins"] if item["id"] == "behavior-baseline")
    if pin["revision"] != BASELINE_REVISION or contract["minimumSharedImport"]["revision"] != BASELINE_REVISION:
        raise ImportFailure("Accepted contract does not match the immutable baseline pin.")
    schema = contract["renderer"]["schema"]
    if schema["definitions"]["BuildManifest"]["properties"]["baselineRevision"]["const"] != BASELINE_REVISION:
        raise ImportFailure("Renderer schema baseline pin mismatch.")

    previous = None
    resumable = set()
    if args.resume_before_handoff:
        if not args.apply:
            raise ImportFailure("--resume-before-handoff requires --apply.")
        receipt_path = root / "contracts/source-provenance.json"
        if receipt_path.is_symlink() or not receipt_path.is_file():
            raise ImportFailure("Resume requires the original importer-created B0 receipt.")
        previous = json.loads(receipt_path.read_bytes())
        if (previous["baselineRevision"] != BASELINE_REVISION
                or previous["contractSha256"] != hashlib.sha256(contract_bytes).hexdigest()):
            raise ImportFailure("Resume receipt baseline/contract mismatch.")
        for entry in previous["inventory"]["selected"]:
            path = entry["path"]
            check_destination(root, path, existing_allowed=True)
            if hashlib.sha256((root / path).read_bytes()).hexdigest() != entry["sha256"]:
                raise ImportFailure(f"Resume refuses modified owner source: {path}")
            resumable.add(path)
        resumable.update({
            "contracts/migration-contract.json", "contracts/renderer-v1.schema.json",
            "contracts/source-provenance.json",
        })

    selected, excluded = [], []
    for entry in inspect_tree(args.source_repo):
        reason = exclusion_reason(entry["path"], entry["mode"])
        if reason:
            excluded.append({"path": entry["path"], "reason": reason, "gitBlob": entry["gitBlob"]})
        else:
            selected.append(entry)
    if not selected:
        raise ImportFailure("No allowlisted source files found.")
    paths = {entry["path"] for entry in selected}
    for path in paths:
        safe_path(path)
    required = {
        BACKEND + "Mahjong.Autotable.slnx",
        BACKEND + "src/Mahjong.Autotable.Api/Program.cs",
        BACKEND + "tests/Mahjong.Autotable.Api.Tests/Mahjong.Autotable.Api.Tests.csproj",
        FRONTEND + "COPYING", FRONTEND + "UPSTREAM_SHA", FRONTEND + "package-lock.json",
        FRONTEND + "src/client.ts", FRONTEND + "server/protocol.ts",
    }
    if not required <= paths:
        raise ImportFailure("Pinned tree is missing required source/notice/project files.")
    folded = [path.casefold() for path in paths]
    if len(set(folded)) != len(folded):
        raise ImportFailure("Case-insensitive source path collision.")
    outputs = {
        "contracts/migration-contract.json": contract_bytes,
        "contracts/renderer-v1.schema.json": (json.dumps(schema, indent=2, ensure_ascii=False) + "\n").encode(),
    }
    planned = paths | set(outputs) | {"contracts/source-provenance.json"}
    for path in sorted(planned):
        check_destination(root, path, existing_allowed=path in resumable)
    blobs = read_blobs(args.source_repo, selected)
    imported = []
    for entry in selected:
        path = entry["path"]
        content = blobs[path]
        transformations = []
        if path in CONFIG_FILES:
            content, transformations = sanitize_config(content)
        check_private_material(path, content)
        imported.append({
            **entry,
            "sourceSha256": hashlib.sha256(blobs[path]).hexdigest(),
            "sha256": hashlib.sha256(content).hexdigest(),
            "bytes": len(content),
            "sanitizedConfigurationFields": transformations,
        })
        outputs[path] = content
    upstream = outputs[FRONTEND + "UPSTREAM_SHA"].decode().strip()
    expected_upstream = next(item for item in contract["sourcePins"] if item["id"] == "renderer-upstream")["revision"]
    if upstream != expected_upstream:
        raise ImportFailure("Inherited renderer UPSTREAM_SHA does not match the accepted contract.")
    receipt = {
        "receiptVersion": "1.0.0",
        "milestone": "B0",
        "baselineRepository": BASELINE_URL,
        "baselineRevision": BASELINE_REVISION,
        "inheritedRendererRevision": upstream,
        "importMethod": "git ls-tree and cat-file by exact immutable blob ID; no working-tree copy",
        "importedAtUtc": previous["importedAtUtc"] if previous else datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "contractSha256": hashlib.sha256(contract_bytes).hexdigest(),
        "rendererSchemaSha256": hashlib.sha256(outputs["contracts/renderer-v1.schema.json"]).hexdigest(),
        "contractApproval": "Coordinator-approved frozen contract; original audit status retained byte-for-byte",
        "auditMetadataOnly": [
            "migration-contract.json teamRoot/repositoryRoot/referenceRoot/sourcePins[].localPath",
            "Historical audit/session tool paths and commands are provenance, not runtime dependencies.",
        ],
        "inventory": {"selectedCount": len(selected), "excludedCount": len(excluded), "selected": imported, "excluded": excluded},
        "excludedReasonCounts": dict(collections.Counter(item["reason"] for item in excluded)),
        "frontendOwnershipTransfer": {
            "from": "Backend (one-time pinned seed)",
            "to": "Frontend",
            "path": "src/frontend/autotable-src",
            "backendWritesAfterReceipt": False,
            "assetDirectoriesExcluded": contract["minimumSharedImport"]["frontendAssetDirsExcluded"],
            "readiness": "Source-only, asset-free baseline; not a working app or generated renderer.",
        },
        "backendProjects": {
            "solution": BACKEND + "Mahjong.Autotable.slnx",
            "api": BACKEND + "src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj",
            "tests": BACKEND + "tests/Mahjong.Autotable.Api.Tests/Mahjong.Autotable.Api.Tests.csproj",
        },
        "licenseObligations": contract["licensing"],
        "retainedNotices": [FRONTEND + "COPYING", FRONTEND + "UPSTREAM_SHA", FRONTEND + "README.md", FRONTEND + "sounds/CREDITS.md"],
        "configurationPolicy": "No live .env or appsettings.Development.json read/imported; credential-bearing safe config fields blanked; SQLite defaults retained.",
        "phaseArchivePolicy": "Root Phase_* archives are excluded; nested C# regression source/tests remain included.",
    }
    outputs["contracts/source-provenance.json"] = (json.dumps(receipt, indent=2, ensure_ascii=False) + "\n").encode()
    if args.apply:
        written = []
        try:
            for path in sorted(outputs, key=lambda path: (path == "contracts/source-provenance.json", path)):
                content = outputs[path]
                destination = root / path
                destination.parent.mkdir(parents=True, exist_ok=True)
                check_destination(root, path, existing_allowed=path in resumable)
                if destination.exists():
                    if path == "contracts/source-provenance.json":
                        destination.write_bytes(content)
                        continue
                    if destination.read_bytes() != content:
                        raise ImportFailure(f"Resume refuses non-identical source/contract: {path}")
                    continue
                with destination.open("xb") as stream:
                    stream.write(content)
                written.append(destination)
                if path in ROOT_FILES and path.endswith(".sh"):
                    destination.chmod(0o755)
        except Exception:
            # Only files created by this invocation can be removed.
            for destination in reversed(written):
                destination.unlink()
            raise
    print(json.dumps({
        "mode": "imported" if args.apply else "dry-run",
        "baselineRevision": BASELINE_REVISION,
        "selectedCount": len(selected),
        "excludedCount": len(excluded),
        "receipt": "contracts/source-provenance.json",
        "frontendOwnership": "Frontend after successful --apply; never re-import over owner edits",
    }, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-repo", required=True, help="Local git object store; never its working-tree files")
    parser.add_argument("--contract", required=True, help="Coordinator-approved frozen migration JSON")
    parser.add_argument("--destination", default=".", help="New output tree, relative to the project (default: .)")
    parser.add_argument("--apply", action="store_true", help="Perform one-time import; default only validates/dry-runs")
    parser.add_argument("--resume-before-handoff", action="store_true",
                        help="Complete an importer-owned partial seed only; refuses any modified owner source")
    args = parser.parse_args()
    try:
        run(args)
    except (ImportFailure, OSError, UnicodeError, json.JSONDecodeError, KeyError, StopIteration) as error:
        print(f"Baseline import failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
