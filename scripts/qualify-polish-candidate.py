#!/usr/bin/env python3
"""Run one fixed issue-20 acceptance partition; never build, publish or deploy."""

import argparse
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tarfile


ROOT = Path(__file__).resolve().parents[1]
SOURCE = "0dae531a58d87548488d76a55297700d5ff3589c"
IMAGE = "ghcr.io/long2know/mahjong-unity@sha256:f9c6420c81206d250b79ab37c32a60045696b61a4dca56bc6f6a701168764f49"
CONFIG = "sha256:b0d050bea10783403714e9065c1360716e2a60d4e011c1ee43b0f1bdd05e3027"
RELEASE = "430946697"
ASSET = "700020559"
BUNDLE_SHA = "86468491b6de1dda82e7568825caf213d2a0e57305acb5e59bc40969d1127dfa"
EDITOR = "artifacts/test-results/polish/editor-0dae531/receipt.json"
XML = "artifacts/test-results/polish/editor-0dae531/unity-editmode.xml"
PLAN = "artifacts/test-results/polish/runtime-final/case-plan-0dae531.json"
GROUPS = ("general", "focus", "camera", "relay", "mobile")
BROWSER = ("mcr.microsoft.com/playwright:v1.60.0-noble@"
           "sha256:83192064c7510f7ee73dd63dc5f22a5e01a92c81a2e6a9c715d9e3fe55471fd9")
HASHES = {
    EDITOR: "64e15e456e539f9669dec52ab69751be0174187972b3bf7be7e743cd80831d64",
    XML: "b79fc58760df11693af00be30a4783ff0445a9178d1c47dd271abcafd399ffcb",
    PLAN: "a7c1b77b219a49d9e5163d68029540e4068f34dad976be4b9d183f541a4ed4be",
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def checked_source(source):
    actual = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    require(actual == SOURCE, "Source checkout differs from the genuine approved source")
    require(not subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"]),
            "Source checkout must remain clean")


def extract_evidence(archive_path, source, expected_hash=BUNDLE_SHA):
    require(digest(archive_path) == expected_hash, "Supplemental bundle SHA256 mismatch")
    payloads = {}
    with tarfile.open(archive_path, "r:gz") as archive:
        for member in archive:
            require(member.name in HASHES and member.name not in payloads, "Unexpected or duplicate evidence path")
            require(member.isfile() and not member.issparse() and 0 < member.size <= 2 * 1024 * 1024,
                    "Evidence must be a bounded regular file")
            target = source / member.name
            require(not any(p.is_symlink() for p in (target, *target.parents)), "Linked evidence destination")
            require(not target.exists(), "Existing evidence must not be overwritten")
            payload = archive.extractfile(member).read()
            require(hashlib.sha256(payload).hexdigest() == HASHES[member.name],
                    "Original Editor/XML/case-plan bytes differ")
            payloads[member.name] = payload
        require(set(payloads) == set(HASHES), "Incomplete genuine evidence inventory")
    for relative, payload in payloads.items():
        target = source / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("xb") as output:
            output.write(payload)


def prepare(source):
    checked_source(source)
    release = json.loads(subprocess.check_output(
        ["gh", "api", f"repos/long2know/mahjong-unity/releases/{RELEASE}"], text=True))
    require(release["draft"] is True, "Evidence release must remain draft")
    assets = [a for a in release["assets"] if str(a["id"]) == ASSET]
    require(len(assets) == 1 and assets[0]["state"] == "uploaded"
            and 0 < assets[0]["size"] <= 2 * 1024 * 1024, "Invalid same-draft evidence asset")
    destination = ROOT / ".work/polish-runtime-evidence.tar.gz"
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("xb") as output:
        subprocess.run(["gh", "api", f"repos/long2know/mahjong-unity/releases/assets/{ASSET}",
                        "-H", "Accept: application/octet-stream"], stdout=output, check=True)
    require(destination.stat().st_size == assets[0]["size"], "Downloaded evidence size mismatch")
    extract_evidence(destination, source)


def validate_image(image):
    require(image["Id"] == CONFIG, "Pulled config ID differs from the approved immutable candidate")
    require(image["Os"] == "linux" and image["Architecture"] == "amd64", "Wrong candidate platform")
    require(IMAGE in image["RepoDigests"], "Exact published digest is absent")
    labels = image["Config"]["Labels"]
    require(labels["org.opencontainers.image.revision"] == SOURCE, "Wrong image source")
    require(labels["org.mahjong-unity.allow-source-dirty"] == "0", "Dirty-image override is forbidden")
    require("BUILD_SHA=" + SOURCE in image["Config"]["Env"], "Wrong runtime BUILD_SHA")
    require(image["Config"]["User"] == "1000:1000", "Candidate must retain its non-root identity")


def player_command(group, plan, run_id, attempt):
    require(group in GROUPS and run_id.isdecimal() and attempt.isdecimal(), "Invalid fixed partition/run")
    name = f"migration-player-polish-hosted-0dae531-{group}-run{run_id}-{attempt}"
    args = ["python3", "tests/migration/run_runtime_player_integration.py", "--image", CONFIG,
            "--editor-receipt", EDITOR, "--port", str(plan["ports"][group]), "--output-name", name]
    args.extend(["--mobile-playability"] if group == "mobile"
                else ["--cases", ",".join(plan["groups"][group])])
    return args, name


def qualify(source, group):
    require(group in GROUPS and os.getuid() != 0, "A fixed partition and non-root runner are required")
    checked_source(source)
    for relative, expected in HASHES.items():
        require(digest(source / relative) == expected, "Genuine evidence changed: " + relative)
    plan = json.loads((source / PLAN).read_text())
    args, name = player_command(group, plan, os.environ["GITHUB_RUN_ID"], os.environ["GITHUB_RUN_ATTEMPT"])
    out = source / "artifacts/test-results/polish/runtime-hosted"
    out.mkdir(parents=True, exist_ok=True)
    record = {
        "group": group, "command": args, "status": "running",
        "runtimeReceipt": f"artifacts/test-results/{name}/runtime-receipt.json",
        "playerReceipt": f"artifacts/test-results/{name}-browser/player-receipt.json",
    }
    receipt = {
        "artifactKind": "polish-exact-image-hosted-partition", "status": "running",
        "sourceCommit": SOURCE, "publishedImage": IMAGE, "expectedImageId": CONFIG,
        "evidenceAssetId": ASSET, "evidenceBundleSha256": BUNDLE_SHA,
        "originalEvidence": HASHES, "workflowRun": os.environ["GITHUB_RUN_ID"],
        "workflowAttempt": os.environ["GITHUB_RUN_ATTEMPT"], "groups": [record],
        "runtimeLocation": "Isolated GitHub-hosted runner, not the protected preview host",
        "combinedQualification": False, "physicalDeviceQualification": False,
        "independentTesterApproved": False, "deploymentAuthorized": False,
    }
    path = out / (group + "-group.json")

    def save():
        path.write_text(json.dumps(receipt, indent=2) + "\n")

    save()
    try:
        with (out / (group + "-pull.log")).open("w") as log:
            for image in (IMAGE, BROWSER):
                subprocess.run(["docker", "pull", "--platform", "linux/amd64", image],
                               stdout=log, stderr=subprocess.STDOUT, check=True, timeout=600)
        inspected = json.loads(subprocess.check_output(["docker", "image", "inspect", IMAGE], text=True))[0]
        validate_image(inspected)
        receipt.update(imageId=inspected["Id"], actualHostedRegistryPullPassed=True)
        save()
        with (out / (group + ".log")).open("w") as log:
            result = subprocess.run(args, cwd=source, stdout=log, stderr=subprocess.STDOUT, timeout=2200)
        record["exitCode"] = result.returncode
        for kind in ("runtime", "player"):
            p = source / record[kind + "Receipt"]
            data = json.loads(p.read_text())
            record[kind + "ReceiptSha256"] = digest(p)
            record[kind + "Status"] = data["status"]
            require(data["status"] == "passed", "Actual canonical receipt failed: " + kind)
            if kind == "runtime":
                require(data["imageId"] == CONFIG and data["actualHealth"]["buildSha"] == SOURCE,
                        "Canonical runtime identity differs")
            else:
                record["caseInventory"] = [c["name"] for c in data["browserEvidence"]["cases"]]
                record["caseFailures"] = data["browserEvidence"]["caseFailures"]
        require(result.returncode == 0, "Actual canonical runtime partition failed")
        record["status"] = receipt["status"] = "passed"
    except Exception as error:
        record["status"] = receipt["status"] = "failed"
        receipt["failure"] = {"name": type(error).__name__, "message": str(error)}
        raise
    finally:
        receipt["completedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        save()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("prepare", "qualify"))
    parser.add_argument("--group", choices=GROUPS)
    args = parser.parse_args()
    source = Path(os.environ["SOURCE_ROOT"]).resolve(strict=True)
    prepare(source) if args.operation == "prepare" else qualify(source, args.group)


if __name__ == "__main__":
    main()
