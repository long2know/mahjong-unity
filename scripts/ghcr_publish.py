#!/usr/bin/env python3
"""Fail-closed source/digest authorization for the WebGL-only GHCR workflows."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
REPO = "long2know/mahjong-web"
IMAGE = "ghcr.io/" + REPO


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


def prepare(source, sha, number):
    checked(sha, r"[0-9a-f]{40}", "source SHA")
    checked(number, r"[1-9][0-9]*", "source PR")
    require(command("git", "-C", str(source), "rev-parse", "HEAD") == sha, "Source checkout mismatch")
    require(not command("git", "-C", str(source), "status", "--porcelain", "--untracked-files=normal"),
            "Source must be clean")
    pr = api(f"pulls/{number}")
    require(pr["head"]["repo"] is not None and pr["head"]["repo"]["full_name"] == REPO
            and pr["head"]["sha"] == sha and pr["base"]["repo"]["full_name"] == REPO
            and pr["base"]["ref"] == "dev" and (pr["state"] == "open" or pr["merged_at"] is not None),
            "Source must be the exact same-repository dev PR head")
    require(not (source / "unity").exists(), "Retired engine source cannot be a packaging input")
    print(json.dumps({"engine": "webgl2", "source_sha": sha, "source_pr": number,
                      "packaging": "build frontend and API from clean source; no prebuilt player input"}))


def promote(digest, sha, run_id, attempt, number, confirmation):
    checked(digest, r"sha256:[0-9a-f]{64}", "image digest")
    checked(sha, r"[0-9a-f]{40}", "source SHA")
    for value, name in [(run_id, "candidate run"), (attempt, "candidate attempt"), (number, "acceptance PR")]:
        checked(value, r"[1-9][0-9]*", name)
    require(confirmation == "runtime-qualified:" + digest, "Explicit runtime qualification confirmation required")
    command("git", "-C", str(ROOT), "merge-base", "--is-ancestor", sha, "origin/main")
    environment = api("environments/ghcr-production")
    require(any(rule["type"] == "required_reviewers" and rule.get("reviewers")
                for rule in environment["protection_rules"]), "ghcr-production must have required reviewers")
    run = api(f"actions/runs/{run_id}/attempts/{attempt}")
    require(run["conclusion"] == "success" and run["event"] == "workflow_dispatch"
            and run["head_branch"] == "main" and run["path"] == ".github/workflows/ghcr-publish.yml",
            "Candidate must come from a successful main packaging workflow")
    pr = api(f"pulls/{number}")
    require(pr["base"]["repo"]["full_name"] == REPO and pr["merged_at"] is not None,
            "Acceptance PR must be merged in this repository")
    require(IMAGE + "@" + digest in (pr.get("body") or "") and sha in (pr.get("body") or ""),
            "Acceptance description must name the exact source and pullable digest")
    for tag in [f"candidate-{run_id}-{attempt}", f"sha-{sha}-{run_id}-{attempt}"]:
        descriptor = json.loads(command("docker", "buildx", "imagetools", "inspect",
                                        IMAGE + ":" + tag, "--format", "{{json .Manifest}}"))
        require(descriptor["digest"] == digest, "Candidate/source tag differs from accepted digest")
    subprocess.run(["docker", "buildx", "imagetools", "create", "--prefer-index=false",
                    "--tag", IMAGE + ":latest", IMAGE + "@" + digest], check=True)
    latest = json.loads(command("docker", "buildx", "imagetools", "inspect", IMAGE + ":latest",
                                "--format", "{{json .Manifest}}"))
    require(latest["digest"] == digest, "Published latest differs from accepted digest")
    print(json.dumps({"image": IMAGE + ":latest", "digest": digest, "acceptance_pr": number,
                      "legacyImageUnchanged": "ghcr.io/long2know/mahjong-unity:latest"}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("prepare", "promote"))
    args = parser.parse_args()
    if args.operation == "prepare":
        prepare(Path(os.environ["SOURCE_ROOT"]).resolve(), os.environ["SOURCE_SHA"], os.environ["SOURCE_PR"])
    else:
        promote(os.environ["IMAGE_DIGEST"], os.environ["SOURCE_SHA"], os.environ["CANDIDATE_RUN_ID"],
                os.environ["CANDIDATE_RUN_ATTEMPT"], os.environ["ACCEPTANCE_PR"], os.environ["CONFIRMATION"])


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        raise SystemExit("GHCR authorization rejected: " + str(error))
