"""Identity/output helpers shared only by the synthetic container probes."""
import hashlib
import subprocess

PROBE_FILES = (
    "tests/migration/browser-prefix.mjs",
    "tests/migration/browser-input-trace.mjs",
    "tests/migration/browser-input-trace.test.mjs",
    "tests/migration/container_proxy_smoke.py",
    "tests/migration/container_smoke.py",
    "tests/migration/runtime_probe.py",
    "tests/migration/test_ghcr_publish.py",
    ".github/workflows/migration-build.yml",
)


def artifact_output(root, value):
    output = (root / value).resolve()
    if not output.is_relative_to(root / "artifacts"):
        raise ValueError("Probe output must remain under this checkout's artifacts directory.")
    return output


def probe_provenance(root):
    source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    files = {}
    for name in PROBE_FILES:
        actual = hashlib.sha256((root / name).read_bytes()).hexdigest()
        committed = subprocess.check_output(["git", "show", f"{source}:{name}"], cwd=root)
        if actual != hashlib.sha256(committed).hexdigest():
            raise ValueError("Uncommitted probe file: " + name)
        files[name] = actual
    return {"probeSourceSha": source, "probeFiles": files}
