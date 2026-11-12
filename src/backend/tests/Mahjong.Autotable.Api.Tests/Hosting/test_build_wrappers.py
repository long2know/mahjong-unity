"""Execute the default source-build wrapper against a labelled command harness."""
import os
from pathlib import Path
import shutil
import subprocess
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[5]
FIXTURES = ROOT / ".work/wrapper-tests"


class WrapperTests(unittest.TestCase):
    def setUp(self):
        self.fixture = FIXTURES / uuid.uuid4().hex
        self.project = self.fixture / "project"
        self.bin = self.fixture / "bin"
        self.project.mkdir(parents=True)
        self.bin.mkdir()
        for name in ("build.sh", "build.ps1"):
            shutil.copyfile(ROOT / name, self.project / name)
        self.log = self.fixture / "docker-arguments"
        docker = self.bin / "docker"
        docker.write_text("""#!/usr/bin/env bash
printf '%s\\n' "$*" >> "$DOCKER_TEST_LOG"
case "$1 ${2:-}" in
  'buildx version') exit 0 ;;
  'buildx build') exit "${DOCKER_TEST_EXIT:-0}" ;;
  'image inspect') printf 'linux\\n'; exit 0 ;;
  'image save') exit 0 ;;
  *) exit 99 ;;
esac
""")
        docker.chmod(0o755)
        self.environment = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                                DOCKER_TEST_LOG=str(self.log), BUILD_SHA="a" * 40)

    def tearDown(self):
        shutil.rmtree(self.fixture)

    def run_wrapper(self, *args, **env):
        return subprocess.run(["bash", str(self.project / "build.sh"), *args],
                              env=dict(self.environment, **env), text=True, capture_output=True)

    def test_default_build_requires_only_docker_and_source(self):
        result = self.run_wrapper()
        self.assertEqual(result.returncode, 0, result.stderr)
        args = self.log.read_text()
        self.assertIn("buildx build --load --platform linux/amd64 --tag mahjong-web:local", args)
        self.assertIn("--build-arg BUILD_SHA=" + "a" * 40, args)
        self.assertNotIn("unity", args.lower())
        self.assertNotIn("artifacts", args)
        self.assertFalse((self.project / "artifacts").exists())

    def test_build_failure_propagates_without_inspecting_or_saving_an_image(self):
        result = self.run_wrapper(DOCKER_TEST_EXIT="17")
        self.assertEqual(result.returncode, 17)
        self.assertNotIn("image inspect", self.log.read_text())
        self.assertNotIn("image save", self.log.read_text())

    def test_explicit_tag_platform_archive_and_no_cache_reach_docker(self):
        archive = self.fixture / "owned.tar"
        result = self.run_wrapper("--tag", "mahjong-web:test", "--platform", "linux/arm64",
                                  "--archive", str(archive), "--no-cache")
        self.assertEqual(result.returncode, 0, result.stderr)
        args = self.log.read_text()
        for value in ("--tag mahjong-web:test", "--platform linux/arm64", "--no-cache", "image save --output " + str(archive)):
            self.assertIn(value, args)

    def test_invalid_or_retired_options_fail_before_docker(self):
        for args in [("--platform", "windows/amd64"), ("--tag",), ("--skip-unity-build",), ("--allow-source-dirty",)]:
            with self.subTest(args=args):
                result = self.run_wrapper(*args)
                self.assertEqual(result.returncode, 2)
                self.assertFalse(self.log.exists())


if __name__ == "__main__":
    unittest.main()
