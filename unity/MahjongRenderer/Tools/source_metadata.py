#!/usr/bin/env python3
"""Hash the renderer's actual source inputs; never inspect operator/license files."""

import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

INPUT_DIRS = (
    "unity/MahjongRenderer/Assets",
    "unity/MahjongRenderer/Packages",
    "unity/MahjongRenderer/ProjectSettings",
    "unity/MahjongRenderer/Tools",
    "unity/MahjongRenderer/Documentation",
)
PRODUCER = "scripts/build-unity-webgl.sh"
GENERATED = {
    "unity/MahjongRenderer/Assets/Resources/renderer-build-identity.json",
    "unity/MahjongRenderer/Assets/Resources/renderer-build-identity.json.meta",
}
IGNORED_DIRS = {"__pycache__", "Library", "Temp", "Obj", "BuildCache", "Logs", "UserSettings"}


def forbidden(name):
    lower = name.lower()
    return (
        name == ".env" or name.startswith(".env.") or lower.endswith((".ulf", ".alf", ".pem", ".key"))
        or "license" in lower or lower in {"credentials", "secrets.json"}
    )


def source_files(root):
    found = []
    for relative in INPUT_DIRS:
        directory = root / relative
        if not directory.is_dir() or directory.is_symlink():
            raise ValueError("Renderer source directory is missing or linked: " + relative)
        for folder, dirs, files in os.walk(directory, followlinks=False):
            for name in list(dirs):
                path = Path(folder) / name
                if name in IGNORED_DIRS or forbidden(name):
                    dirs.remove(name)
                elif path.is_symlink():
                    raise ValueError("Linked renderer source directory is not allowed.")
            for name in files:
                path = Path(folder) / name
                rel = path.relative_to(root).as_posix()
                if rel in GENERATED or forbidden(name) or name.endswith(".pyc"):
                    continue
                if path.is_symlink():
                    raise ValueError("Linked renderer source file is not allowed.")
                found.append(rel)
    producer = root / PRODUCER
    if not producer.is_file() or producer.is_symlink():
        raise ValueError("Renderer build producer is missing or linked.")
    found.append(PRODUCER)
    return sorted(found)


def metadata(root):
    paths = source_files(root)
    digest = hashlib.sha256()
    for relative in paths:
        digest.update(relative.encode("utf-8"))
        digest.update(b"\0")
        content = hashlib.sha256()
        with (root / relative).open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                content.update(chunk)
        digest.update(content.digest())
        digest.update(b"\0")
    revision = subprocess.check_output(
        ["git", "-C", str(root), "rev-parse", "--verify", "HEAD"], text=True
    ).strip()
    if len(revision) != 40 or any(c not in "0123456789abcdef" for c in revision):
        raise ValueError("Actual git HEAD is not a full source revision.")
    status = subprocess.check_output(
        ["git", "-C", str(root), "status", "--porcelain=v1", "-z", "--untracked-files=all", "--",
         *INPUT_DIRS, PRODUCER,
         *[":(exclude)" + p for p in sorted(GENERATED)],
         ":(exclude)**/__pycache__/**", ":(exclude)**/*.pyc",
         ":(exclude)**/.env", ":(exclude)**/.env.*",
         ":(exclude)**/*.ulf", ":(exclude)**/*.alf", ":(exclude)**/*licenses*/**"]
    )
    return {
        "rendererSourceRevision": revision,
        "rendererSourceTreeSha256": digest.hexdigest(),
        "sourceDirty": bool(status),
    }


def main():
    root = Path(sys.argv[1]).resolve() if len(sys.argv) == 2 else Path(__file__).resolve().parents[3]
    print(json.dumps(metadata(root), separators=(",", ":")))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print("Renderer source metadata unavailable: " + str(error), file=sys.stderr)
        sys.exit(1)
