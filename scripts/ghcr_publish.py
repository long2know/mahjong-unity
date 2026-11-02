#!/usr/bin/env python3
"""Package approved Unity output, or validate inputs for the GHCR workflows."""

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import tarfile


ROOT = Path(__file__).resolve().parents[1]
REPO = "long2know/mahjong-unity"
IMAGE = "ghcr.io/" + REPO
MAX_BYTES = 512 * 1024 * 1024
BUNDLE_ROOT = PurePosixPath("artifacts/unity-webgl")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def checked(value, pattern, name):
    require(re.fullmatch(pattern, value) is not None, "Invalid " + name)
    return value


def command(*args):
    return subprocess.check_output(args, text=True).strip()


def api(endpoint):
    return json.loads(command("gh", "api", f"repos/{REPO}/{endpoint}"))


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def provenance(source, artifacts, source_sha):
    checked(source_sha, r"[0-9a-f]{40}", "source SHA")
    verifier = module("unity_verify", ROOT / "scripts/verify-unity-build.py")
    report = verifier.verify(artifacts / "renderer-build.json", allow_source_dirty=False)
    metadata = module("unity_source", ROOT / "unity/MahjongRenderer/Tools/source_metadata.py")
    actual = metadata.metadata(source)
    require(actual["rendererSourceRevision"] == source_sha, "Checkout HEAD differs from source SHA")
    for key in ("rendererSourceRevision", "rendererSourceTreeSha256", "sourceDirty"):
        require(report[key] == actual[key], "BuildManifest differs from checkout: " + key)
    require(actual["sourceDirty"] is False, "Dirty renderer source is forbidden")
    manifest = verifier.read_json(artifacts / "renderer-build.json")
    # This handoff deliberately supports the current four-file, no-streaming build.
    require(manifest["streamingAssetsPath"] is None, "Streaming assets need a separately reviewed bundle contract")
    inventory = {"renderer-build.json"} | {item["path"] for item in report["files"].values()}
    return inventory


def bundle(source, artifacts, source_sha, output):
    inventory = provenance(source, artifacts, source_sha)
    require(not output.exists(), "Refusing to overwrite bundle")
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("xb") as stream, tarfile.open(fileobj=stream, mode="w:gz") as archive:
        for name in sorted(inventory):
            archive.add(artifacts / name, arcname=str(BUNDLE_ROOT / name), recursive=False)
    print(json.dumps({"source_sha": source_sha, "bundle_sha256": sha256(output), "bundle": str(output)}))


def extract(archive_path, destination, expected_hash):
    checked(expected_hash, r"[0-9a-f]{64}", "bundle SHA256")
    require(sha256(archive_path) == expected_hash, "Bundle SHA256 mismatch")
    require(not any(path.is_symlink() for path in (destination, *destination.parents)),
            "Linked artifact destination is forbidden")
    require(not destination.exists(), "Artifact destination already exists")
    with tarfile.open(archive_path, "r:gz") as archive:
        members = []
        seen = set()
        total = 0
        for member in archive:
            members.append(member)
            require(len(members) <= 64, "Invalid bundle member count")
            path = PurePosixPath(member.name)
            require(
                re.fullmatch(r"[A-Za-z0-9_./-]+", member.name) is not None
                and not path.is_absolute()
                and all(part not in ("", ".", "..") for part in member.name.split("/")),
                "Unsafe bundle path",
            )
            require(path.is_relative_to(BUNDLE_ROOT) and path != BUNDLE_ROOT, "Unexpected bundle root")
            require(member.isfile() and not member.issparse(), "Only regular, non-sparse bundle files are allowed")
            require(member.name.casefold() not in seen, "Duplicate bundle path")
            seen.add(member.name.casefold())
            total += member.size
            require(0 < member.size <= MAX_BYTES and total <= MAX_BYTES, "Bundle size limit exceeded")
        require(members, "Empty bundle")
        destination.mkdir(parents=True)
        for member in members:
            target = destination / PurePosixPath(member.name).relative_to(BUNDLE_ROOT)
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.extractfile(member) as source, target.open("xb") as output:
                while chunk := source.read(1024 * 1024):
                    output.write(chunk)
    return {str(PurePosixPath(member.name).relative_to(BUNDLE_ROOT)) for member in members}


def prepare(source, source_sha, source_pr, release_id, asset_id, expected_hash):
    checked(source_sha, r"[0-9a-f]{40}", "source SHA")
    checked(expected_hash, r"[0-9a-f]{64}", "bundle SHA256")
    checked(release_id, r"[1-9][0-9]*", "release ID")
    checked(asset_id, r"[1-9][0-9]*", "asset ID")
    checked(source_pr, r"[1-9][0-9]*", "source PR")
    require(command("git", "-C", str(source), "rev-parse", "HEAD") == source_sha, "Source checkout mismatch")
    pr = api(f"pulls/{source_pr}")
    require(pr["head"]["repo"] is not None and pr["head"]["repo"]["full_name"] == REPO
            and pr["head"]["sha"] == source_sha and pr["base"]["repo"]["full_name"] == REPO
            and pr["base"]["ref"] == "dev" and (pr["state"] == "open" or pr["merged_at"] is not None),
            "Source must be the exact same-repository dev PR head (open or merged)")
    release = api(f"releases/{release_id}")
    require(release["draft"] is True, "Build input must remain a DRAFT release")
    assets = [asset for asset in release["assets"] if str(asset["id"]) == asset_id]
    require(len(assets) == 1, "Asset ID is not in the same-repository draft release")
    asset = assets[0]
    require(asset["state"] == "uploaded" and 0 < asset["size"] <= MAX_BYTES, "Invalid uploaded asset size/state")
    archive = ROOT / ".work/ghcr-input.tar.gz"
    archive.parent.mkdir(parents=True, exist_ok=True)
    with archive.open("xb") as output:
        subprocess.run(["gh", "api", "-H", "Accept: application/octet-stream",
                        f"repos/{REPO}/releases/assets/{asset_id}"], stdout=output, check=True)
    require(archive.stat().st_size == asset["size"], "Downloaded asset size mismatch")
    destination = source / "artifacts/unity-webgl"
    inventory = extract(archive, destination, expected_hash)
    require(inventory == provenance(source, destination, source_sha), "Unexpected files in artifact bundle")
    print(json.dumps({"source_sha": source_sha, "bundle_sha256": expected_hash, "asset_id": asset_id}))


def promote(digest, source_sha, run_id, attempt, acceptance_pr, confirmation):
    checked(digest, r"sha256:[0-9a-f]{64}", "image digest")
    checked(run_id, r"[1-9][0-9]*", "candidate run ID")
    checked(attempt, r"[1-9][0-9]*", "candidate attempt")
    checked(acceptance_pr, r"[1-9][0-9]*", "runtime acceptance PR")
    checked(source_sha, r"[0-9a-f]{40}", "source SHA")
    require(confirmation == "runtime-qualified:" + digest, "Explicit runtime qualification confirmation required")
    command("git", "-C", str(ROOT), "merge-base", "--is-ancestor", source_sha, "origin/main")
    environment = api("environments/ghcr-production")
    require(any(rule["type"] == "required_reviewers" and rule.get("reviewers")
                for rule in environment["protection_rules"]), "ghcr-production must have required reviewers")
    run = api(f"actions/runs/{run_id}/attempts/{attempt}")
    require(run["conclusion"] == "success" and run["event"] == "workflow_dispatch"
            and run["head_branch"] == "main" and run["path"] == ".github/workflows/ghcr-publish.yml",
            "Candidate must come from a successful main GHCR packaging workflow attempt")
    pr = api(f"pulls/{acceptance_pr}")
    require(pr["base"]["repo"]["full_name"] == REPO, "Acceptance PR must be in this repository")
    tag = f"{IMAGE}:candidate-{run_id}-{attempt}"
    descriptor = json.loads(command("docker", "buildx", "imagetools", "inspect", tag,
                                    "--format", "{{json .Manifest}}"))
    require(descriptor["digest"] == digest, "Candidate tag no longer resolves to accepted digest")
    source_tag = f"{IMAGE}:sha-{source_sha}-{run_id}-{attempt}"
    source_descriptor = json.loads(command("docker", "buildx", "imagetools", "inspect", source_tag,
                                           "--format", "{{json .Manifest}}"))
    require(source_descriptor["digest"] == digest, "Candidate source SHA tag differs from accepted digest")
    subprocess.run(["docker", "buildx", "imagetools", "create", "--prefer-index=false",
                    "--tag", IMAGE + ":latest", IMAGE + "@" + digest], check=True)
    latest = json.loads(command("docker", "buildx", "imagetools", "inspect", IMAGE + ":latest",
                                "--format", "{{json .Manifest}}"))
    require(latest["digest"] == digest, "Published latest differs from accepted digest")
    print(json.dumps({"image": IMAGE + ":latest", "digest": digest, "acceptance_pr": acceptance_pr}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="operation", required=True)
    pack = commands.add_parser("bundle")
    pack.add_argument("--source", type=Path, required=True)
    pack.add_argument("--artifacts", type=Path, required=True)
    pack.add_argument("--source-sha", required=True)
    pack.add_argument("--output", type=Path, required=True)
    commands.add_parser("prepare")
    commands.add_parser("promote")
    args = parser.parse_args()
    if args.operation == "bundle":
        bundle(args.source.resolve(), args.artifacts.resolve(), args.source_sha, args.output)
    elif args.operation == "prepare":
        prepare(Path(os.environ["SOURCE_ROOT"]).resolve(), os.environ["SOURCE_SHA"],
                os.environ["SOURCE_PR"], os.environ["RELEASE_ID"], os.environ["ASSET_ID"],
                os.environ["BUNDLE_SHA256"])
    else:
        promote(os.environ["IMAGE_DIGEST"], os.environ["SOURCE_SHA"], os.environ["CANDIDATE_RUN_ID"],
                os.environ["CANDIDATE_RUN_ATTEMPT"], os.environ["ACCEPTANCE_PR"], os.environ["CONFIRMATION"])


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, tarfile.TarError, subprocess.CalledProcessError) as error:
        raise SystemExit("GHCR input/publication rejected: " + str(error))
