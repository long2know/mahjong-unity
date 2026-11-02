"""Explicit, recorded focus-tool derivative; never edit the frozen application."""

import hashlib
import json
from pathlib import Path
import subprocess


ROOT = Path(__file__).resolve().parents[1]
APPLICATION_SOURCE = "ee58678676bc3f99ac141a27111922dc3110ab2e"
INPUTS = {
    "run_runtime_player_integration.py": "1c80fb9a01df1bbb7468ec51cd5818a1d97231dd3953b9488f70a9116744a815",
    "run_player_integration.py": "ee4768dd6d6511ee7d94471dbbe75c2b9049e4436a74b0744432e3171ed20911",
    "browser-gameplay.mjs": "f16cf73fe4d3844914d19dcd02ee463346d213ae911920f5d83b0951753b3043",
    "player-headful.mjs": "5ead0efdeffa0a5e1f7bd0e9d1affaaed76a03d398c6681f5cb07d645b885d5f",
}
OLD_DISPLAY_GUARD = r"assert.match(process.env.DISPLAY ?? '', /^:\d+$/);"
# Unlike JavaScript $, the final lookahead rejects trailing newlines too.
DISPLAY_GUARD = r"assert.match(process.env.DISPLAY ?? '', /^(?::|127\.0\.0\.1:)\d+(?![\s\S])/);"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def replace_once(text, before, after):
    if text.count(before) != 1:
        raise ValueError("Frozen focus-tool anchor differs: " + before)
    return text.replace(before, after)


def stage(source, destination):
    application = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if application != APPLICATION_SOURCE or subprocess.check_output(
            ["git", "-C", str(source), "status", "--porcelain"]):
        raise ValueError("Focus tools require the exact clean frozen application")
    probe_revision = subprocess.check_output(["git", "-C", str(ROOT), "rev-parse", "HEAD"], text=True).strip()
    if subprocess.check_output(["git", "-C", str(ROOT), "status", "--porcelain"]):
        raise ValueError("Focus probe tooling checkout must be clean")
    payloads = {}
    for name, expected in INPUTS.items():
        path = source / "tests/migration" / name
        if sha(path) != expected:
            raise ValueError("Frozen focus-tool input differs: " + name)
        payloads[name] = path.read_text()
    for name in ("run_runtime_player_integration.py", "run_player_integration.py"):
        payloads[name] = replace_once(
            payloads[name], "ROOT = Path(__file__).resolve().parents[2]",
            'ROOT = Path(os.environ["MAHJONG_APPLICATION_SOURCE_ROOT"]).resolve(strict=True)\n'
            'TOOL_ROOT = Path(os.environ["MAHJONG_PROBE_TOOL_ROOT"]).resolve(strict=True)')
        payloads[name] = replace_once(
            payloads[name], "    receipt = {\n",
            '    receipt = {\n        **json.loads(Path(os.environ["MAHJONG_TEST_TOOLING_RECEIPT"]).read_text()),\n')
    payloads["run_runtime_player_integration.py"] = replace_once(
        payloads["run_runtime_player_integration.py"],
        '"python3", "tests/migration/run_player_integration.py"',
        '"python3", str(TOOL_ROOT / "run_player_integration.py")')
    payloads["run_player_integration.py"] = replace_once(
        payloads["run_player_integration.py"],
        '(ROOT / "tests/migration" / name, "/workspace/tests/migration/" + name, True)',
        '(TOOL_ROOT / name if name in ("browser-gameplay.mjs", "player-headful.mjs") '
        'else ROOT / "tests/migration" / name,\n'
        '              "/workspace/tests/migration/" + name, True)')
    payloads["run_player_integration.py"] = replace_once(
        payloads["run_player_integration.py"],
        "return {path: hashlib.sha256((ROOT / path).read_bytes()).hexdigest() for path in paths}",
        "return {path: hashlib.sha256((TOOL_ROOT / Path(path).name if Path(path).name in "
        "('run_runtime_player_integration.py', 'run_player_integration.py', 'browser-gameplay.mjs', 'player-headful.mjs') "
        "else ROOT / path).read_bytes()).hexdigest() for path in paths}")
    payloads["browser-gameplay.mjs"] = replace_once(
        payloads["browser-gameplay.mjs"], OLD_DISPLAY_GUARD, DISPLAY_GUARD)
    if not destination.is_relative_to(ROOT / ".work") or any(
            p.is_symlink() for p in (destination, *destination.parents)):
        raise ValueError("Focus tooling destination must be a fresh owned unlinked .work path")
    destination.mkdir(parents=True)
    for name, text in payloads.items():
        (destination / name).write_text(text)
    record = {
        "applicationSourceSha": application, "probeSourceSha": probe_revision,
        "probeTooling": {
            "kind": "explicit-pinned-focus-tool-derivative",
            "transformSha256": sha(Path(__file__)), "root": str(destination),
            "originalFileSha256": INPUTS,
            "executedFileSha256": {name: sha(destination / name) for name in payloads},
            "changes": ["explicit frozen application root and derived child/probe paths",
                        "record separate application/probe provenance and actual executed file hashes",
                        "accept only Unix-local or exact IPv4-loopback DISPLAY syntax"],
        },
    }
    receipt = destination / "tooling-provenance.json"
    receipt.write_text(json.dumps(record, indent=2) + "\n")
    environment = {
        "MAHJONG_APPLICATION_SOURCE_ROOT": str(source),
        "MAHJONG_PROBE_TOOL_ROOT": str(destination),
        "MAHJONG_TEST_TOOLING_RECEIPT": str(receipt),
    }
    return record, environment


def validate(record):
    root = Path(record["probeTooling"]["root"])
    for name, expected in record["probeTooling"]["executedFileSha256"].items():
        if sha(root / name) != expected:
            raise ValueError("Executed focus-tool bytes changed: " + name)
