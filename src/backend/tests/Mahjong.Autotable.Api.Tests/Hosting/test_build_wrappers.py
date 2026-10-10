"""Execute real wrapper sources against an explicitly fabricated command harness."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import unittest
import uuid


ROOT = Path(__file__).resolve().parents[5]
FIXTURES = Path(__file__).resolve().parent / ".test-fixtures"
sys.dont_write_bytecode = True


class WrapperTests(unittest.TestCase):
    def setUp(self):
        self.fixture = FIXTURES / uuid.uuid4().hex
        self.project = self.fixture / "project"
        self.bin = self.fixture / "bin"
        (self.project / "scripts").mkdir(parents=True)
        (self.project / ".git").mkdir()
        self.bin.mkdir()
        for relative in ("build.sh", "build.ps1", "scripts/build-unity-docker.sh"):
            shutil.copyfile(ROOT / relative, self.project / relative)
        self.docker_log = self.fixture / "docker-arguments"
        fake_docker = self.bin / "docker"
        fake_docker.write_text("""#!/usr/bin/env bash
printf '%s\\n' "$*" >> "$DOCKER_TEST_LOG"
case "$1 ${2:-}" in
    'buildx version'|'compose version'|'info ') exit 0 ;;
    *) exit "${DOCKER_TEST_EXIT:-17}" ;;
esac
""")
        fake_docker.chmod(0o755)
        private = self.fixture / "private"
        private.mkdir()
        self.license = private / "TEST_ONLY.ulf"
        self.license.write_text("FABRICATED TEST ONLY; NOT AN ACTIVATED UNITY LICENSE\n")
        self.environment = dict(os.environ)
        for key in ("UNITY_LICENSE_FILE", "UNITY_LICENSE_DIR", "UNITY_LICENSE_MOUNT_SOURCE",
                    "UNITY_LICENSE_MOUNT_TARGET", "ALLOW_SOURCE_DIRTY"):
            self.environment.pop(key, None)
        self.environment.update({
            "PATH": str(self.bin) + os.pathsep + os.environ["PATH"],
            "DOCKER_TEST_LOG": str(self.docker_log),
            "DOCKER_TEST_EXIT": "17",
            "BUILD_SHA": "FABRICATED-WRAPPER-TEST-IDENTITY",
        })

    def tearDown(self):
        shutil.rmtree(self.fixture)

    def bash(self, relative, *arguments, licensed=False, **environment):
        env = dict(self.environment, **environment)
        if licensed:
            env["UNITY_LICENSE_FILE"] = str(self.license)
        return subprocess.run(["bash", str(self.project / relative), *arguments],
                              capture_output=True, text=True, env=env, cwd=self.fixture)

    def arguments(self):
        return self.docker_log.read_text() if self.docker_log.exists() else ""

    def test_container_wrapper_blocks_without_private_licensing_before_any_docker_build(self):
        result = self.bash("scripts/build-unity-docker.sh")
        self.assertEqual(result.returncode, 1)
        self.assertIn("Docker supplies build tools, not a Unity license", result.stderr)
        self.assertEqual(self.arguments(), "")
        self.assertFalse((self.project / "artifacts/unity-webgl").exists())

    def test_default_wrapper_calls_actual_test_and_build_mode_and_propagates_container_failure(self):
        result = self.bash("scripts/build-unity-docker.sh", licensed=True)
        self.assertEqual(result.returncode, 17)
        arguments = self.arguments()
        self.assertIn("--profile unity-build", arguments)
        self.assertIn("--env-file", arguments)
        self.assertIn("unity-compose-empty.env", arguments)
        self.assertIn("run --build --rm --no-deps unity-builder --test-and-build", arguments)
        self.assertNotIn("UNITY_EMAIL", arguments)
        self.assertNotIn("UNITY_PASSWORD", arguments)
        self.assertNotIn("FABRICATED TEST ONLY; NOT AN ACTIVATED UNITY LICENSE", result.stdout + result.stderr + arguments)

    def test_all_editor_modes_and_local_dirty_qualification_are_explicit(self):
        for mode in ("--build", "--test", "--test-and-build"):
            with self.subTest(mode=mode):
                result = self.bash("scripts/build-unity-docker.sh", mode, "--allow-source-dirty", licensed=True)
                self.assertEqual(result.returncode, 17)
                self.assertIn(f"unity-builder {mode}", self.arguments())
        invalid = self.bash("scripts/build-unity-docker.sh", licensed=True, ALLOW_SOURCE_DIRTY="true")
        self.assertEqual(invalid.returncode, 2)
        self.assertIn("exactly 0 or 1", invalid.stderr)

    def test_checkout_license_and_symlink_mounts_fail_closed(self):
        inside = self.project / "PRIVATE_TEST.ulf"
        inside.write_text("FABRICATED TEST ONLY")
        result = self.bash("scripts/build-unity-docker.sh", UNITY_LICENSE_FILE=str(inside))
        self.assertEqual(result.returncode, 1)
        self.assertIn("outside the checkout", result.stderr)
        link = self.fixture / "symlink.ulf"
        link.symlink_to(self.license)
        result = self.bash("scripts/build-unity-docker.sh", UNITY_LICENSE_FILE=str(link))
        self.assertEqual(result.returncode, 1)
        self.assertIn("not a symlink", result.stderr)
        self.assertEqual(self.arguments(), "")

    def test_private_directory_mode_does_not_require_account_credentials(self):
        result = self.bash("scripts/build-unity-docker.sh", UNITY_LICENSE_DIR=str(self.license.parent))
        self.assertEqual(result.returncode, 17)
        self.assertIn("unity-builder --test-and-build", self.arguments())

    def test_default_runtime_wrapper_builds_unity_first_and_stops_on_failure(self):
        result = self.bash("build.sh", licensed=True)
        self.assertEqual(result.returncode, 17)
        self.assertIn("unity-builder --test-and-build", self.arguments())
        self.assertNotIn("buildx build", self.arguments())

    def test_use_existing_real_artifacts_still_passes_strict_or_local_flag_to_docker_gate(self):
        for arguments, flag in ((("--skip-unity-build",), "0"),
                                (("--skip-unity-build", "--allow-source-dirty"), "1")):
            with self.subTest(flag=flag):
                result = self.bash("build.sh", *arguments)
                self.assertEqual(result.returncode, 17)
                self.assertIn(f"--build-arg ALLOW_SOURCE_DIRTY={flag}", self.arguments())
                self.assertIn("--tag mahjong-unity:local", self.arguments())
                self.assertNotIn("unity-builder --test-and-build", self.arguments())

    def test_entrypoint_propagates_editor_failure_and_rejects_stale_zero_exit(self):
        (self.project / "scripts/build-unity-webgl.sh").write_text(
            "#!/usr/bin/env bash\nprintf '%s\\n' 'FABRICATED TEST producer failure' >&2\nexit 31\n")
        (self.project / "scripts/verify-unity-build.py").write_text("# FABRICATED TEST marker; never invoked\n")
        env = dict(self.environment, MAHJONG_BUILD_WORKSPACE=str(self.project))
        command = ["bash", str(ROOT / "docker/unity-build-entrypoint.sh"), "--build"]
        failed = subprocess.run(command, text=True, capture_output=True, env=env)
        self.assertEqual(failed.returncode, 31)
        (self.project / "scripts/build-unity-webgl.sh").write_text("#!/usr/bin/env bash\nexit 0\n")
        stale = self.project / "artifacts/unity-webgl/renderer-build.json"
        stale.parent.mkdir(parents=True)
        stale.write_text('{"fixture":"FABRICATED TEST ONLY -- STALE -- NOT UNITY"}')
        os.utime(stale, (1, 1))
        rejected = subprocess.run(command, text=True, capture_output=True, env=env)
        self.assertNotEqual(rejected.returncode, 0)
        self.assertIn("fresh real-build manifest", rejected.stderr)

    @unittest.skipUnless(shutil.which("pwsh"), "Native PowerShell is not installed on this test runner")
    def test_powershell_actual_help_parser_and_existing_artifact_failure_path(self):
        executable = shutil.which("pwsh")
        help_result = subprocess.run([executable, "-NoLogo", "-NoProfile", "-File", str(self.project / "build.ps1"), "-Help"],
                                     text=True, capture_output=True, env=self.environment)
        self.assertEqual(help_result.returncode, 0, help_result.stderr)
        self.assertIn("-SkipUnityBuild", help_result.stdout)
        self.assertIn("mahjong-unity:local", help_result.stdout)
        result = subprocess.run([executable, "-NoLogo", "-NoProfile", "-File", str(self.project / "build.ps1"),
                                 "-SkipUnityBuild", "-AllowSourceDirty"], text=True, capture_output=True,
                                env=self.environment, cwd=self.fixture)
        self.assertEqual(result.returncode, 17, result.stderr)
        self.assertIn("--build-arg ALLOW_SOURCE_DIRTY=1", self.arguments())
        self.assertNotIn("unity-builder --test-and-build", self.arguments())

    @unittest.skipUnless(shutil.which("pwsh"), "Native PowerShell is not installed on this test runner")
    def test_powershell_default_container_failure_propagates_without_runtime_build(self):
        env = dict(self.environment, UNITY_LICENSE_FILE=str(self.license))
        result = subprocess.run([shutil.which("pwsh"), "-NoLogo", "-NoProfile", "-File", str(self.project / "build.ps1")],
                                text=True, capture_output=True, env=env, cwd=self.fixture)
        self.assertEqual(result.returncode, 17, result.stderr)
        self.assertIn("unity-builder --test-and-build", self.arguments())
        self.assertIn("--env-file", self.arguments())
        self.assertNotIn("buildx build", self.arguments())


class PipelineSourceTests(unittest.TestCase):
    def test_unity_ignores_never_hide_nested_source_checker_projects_or_asset_meta(self):
        generated = (
            "unity/MahjongRenderer/Assembly-CSharp.csproj",
            "unity/MahjongRenderer/MahjongRenderer.sln",
            "unity/MahjongRenderer/Library/generated",
            "unity/MahjongRenderer/BuildCache/tool-work/generated",
        )
        source = (
            "unity/MahjongRenderer/Tools/CSharpSyntax/CSharpSyntax.csproj",
            "unity/MahjongRenderer/Assets/Example.cs.meta",
            "unity/MahjongRenderer/Assets/Scenes/Example.unity",
            "unity/MahjongRenderer/Assets/Library/Example.cs",
            "unity/MahjongRenderer/Packages/packages-lock.json",
            "contracts/renderer-v1.schema.json",
            "docker/unity-builder-image.json",
        )
        for path in generated:
            self.assertEqual(subprocess.run(["git", "check-ignore", "--no-index", "--quiet", path], cwd=ROOT).returncode, 0, path)
        for path in source:
            self.assertEqual(subprocess.run(["git", "check-ignore", "--no-index", "--quiet", path], cwd=ROOT).returncode, 1, path)

    @unittest.skipUnless(shutil.which("docker"), "Docker is not installed on this source test runner")
    def test_compose_interfaces_parse_using_explicit_empty_env_not_operator_config(self):
        fixture = FIXTURES / uuid.uuid4().hex
        try:
            fixture.mkdir(parents=True)
            empty_env = fixture / ".env.example"
            empty_env.write_text("")
            docker_config = fixture / "docker-config"
            docker_config.mkdir()
            environment = dict(os.environ)
            environment.update({
                "DOCKER_CONFIG": str(docker_config),
                "JWT_SIGNING_KEY": "FABRICATED-COMPOSE-CONFIG-CHECK-NOT-A-LIVE-KEY",
                "UNITY_BUILD_UID": "1000",
                "UNITY_BUILD_GID": "1000",
                "UNITY_LICENSE_MOUNT_SOURCE": str(fixture / "FABRICATED-NONEXISTENT-TEST.ulf"),
                "UNITY_LICENSE_MOUNT_TARGET": "/workspace/artifacts/unity-builder-home/.local/share/unity3d/Unity/Unity_lic.ulf",
                "ALLOW_SOURCE_DIRTY": "0",
            })
            filesets = (
                ("docker-compose.yml",),
                ("docker-compose.yml", "docker-compose.postgres.yml"),
                ("docker-compose.yml", "docker-compose.sqlserver.yml"),
                ("docker-compose.unity-build.yml",),
            )
            for files in filesets:
                with self.subTest(files=files):
                    command = ["docker", "compose", "--env-file", str(empty_env)]
                    if files == ("docker-compose.unity-build.yml",):
                        command.extend(("--profile", "unity-build"))
                    for name in files:
                        command.extend(("-f", str(ROOT / name)))
                    result = subprocess.run([*command, "config", "--quiet"],
                                            text=True, capture_output=True, env=environment)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(result.stdout, "")
                    if files == ("docker-compose.unity-build.yml",):
                        rendered = subprocess.run([*command, "config", "--format", "json"],
                                                  text=True, capture_output=True, env=environment)
                        self.assertEqual(rendered.returncode, 0, rendered.stderr)
                        builder = json.loads(rendered.stdout)["services"]["unity-builder"]
                        self.assertEqual(builder.get("tmpfs"),
                                         ["/tmp:rw,nosuid,nodev,mode=1777,size=512m"])
                        self.assertIs(builder["read_only"], True)
                        self.assertEqual(builder["user"], "1000:1000")
                        self.assertEqual(builder["security_opt"], ["no-new-privileges:true"])
                        self.assertEqual(builder["image"], "mahjong-unity-builder:6000.3.24f1-webgl-3.2.2")
                        self.assertEqual(builder["platform"], "linux/amd64")
                        self.assertFalse(builder.get("privileged", False))
                        self.assertFalse(builder.get("cap_add", []))
                        mounts = {mount["target"]: mount for mount in builder["volumes"]}
                        license_target = environment["UNITY_LICENSE_MOUNT_TARGET"]
                        self.assertEqual(set(mounts), {
                            "/workspace/.git", "/workspace/.gitignore", "/workspace/scripts",
                            "/workspace/contracts", "/workspace/unity/MahjongRenderer",
                            "/workspace/artifacts", license_target,
                        })
                        for relative in (".git", ".gitignore", "scripts", "contracts",
                                         "unity/MahjongRenderer", "artifacts"):
                            mount = mounts["/workspace/" + relative]
                            self.assertEqual(mount["type"], "bind")
                            self.assertEqual(mount["source"], str(ROOT / relative))
                            self.assertEqual(mount.get("read_only", False),
                                             relative not in ("unity/MahjongRenderer", "artifacts"))
                        self.assertEqual(mounts[license_target]["type"], "bind")
                        self.assertEqual(mounts[license_target]["source"],
                                         environment["UNITY_LICENSE_MOUNT_SOURCE"])
                        self.assertIs(mounts[license_target]["read_only"], True)
                        self.assertIs(mounts[license_target]["bind"]["create_host_path"], False)
        finally:
            if fixture.exists():
                shutil.rmtree(fixture)

    def test_gameci_platform_pin_matches_configuration_and_frozen_editor(self):
        image = json.loads((ROOT / "docker/unity-builder-image.json").read_text())
        dockerfile = (ROOT / "docker/unity-builder.Dockerfile").read_text()
        schema = json.loads((ROOT / "contracts/renderer-v1.schema.json").read_text())
        self.assertIn(image["image"] + "@" + image["platformManifestDigest"], dockerfile)
        self.assertEqual(image["unityVersion"], schema["definitions"]["BuildManifest"]["properties"]["unityVersion"]["const"])
        self.assertEqual(image["unityRevision"], schema["definitions"]["BuildManifest"]["properties"]["unityRevision"]["const"])
        self.assertEqual(image["platform"], "linux/amd64")
        self.assertNotIn("UNITY_LICENSE", dockerfile)
        self.assertNotIn("activate", dockerfile.lower())

    def test_runtime_keeps_gate_notices_nonroot_and_separate_editor(self):
        dockerfile = (ROOT / "Dockerfile").read_text()
        self.assertIn("FROM python:3.12-alpine AS unity-artifacts-check", dockerfile)
        self.assertIn("COPY --from=unity-artifacts-check /verified/renderer-build.json", dockerfile)
        self.assertIn("--chown=1000:1000 /verified/ /frontend/autotable/unity/", dockerfile)
        self.assertIn("USER 1000:1000", dockerfile)
        self.assertIn('ENTRYPOINT ["/usr/bin/tini", "--", "dotnet"', dockerfile)
        self.assertIn("THIRD_PARTY_NOTICES.txt", dockerfile)
        self.assertNotIn("unityci/editor", dockerfile)

    @unittest.skipUnless(shutil.which("node"), "Node is not installed on this source test runner")
    def test_npm_inventory_and_verbatim_notices_do_not_copy_other_package_data(self):
        fixture = FIXTURES / uuid.uuid4().hex
        try:
            project = fixture / "project"
            package = project / "node_modules/fabricated-package"
            package.mkdir(parents=True)
            (project / "package-lock.json").write_text(json.dumps({"packages": {
                "": {"name": "test"},
                "node_modules/fabricated-package": {"version": "1.0.0", "license": "TEST-ONLY-DECLARATION"}
            }}))
            (package / "package.json").write_text(json.dumps({
                "name": "fabricated-package", "version": "1.0.0", "license": "TEST-ONLY-DECLARATION",
                "privateExample": "DO-NOT-COPY-OTHER-PACKAGE-CONFIG"
            }))
            (package / "LICENSE").write_text("FABRICATED TEST NOTICE ONLY\n")
            (package / "credentials-should-not-copy.txt").write_text("DO-NOT-COPY-OTHER-PACKAGE-CONFIG")
            output = fixture / "notices"
            result = subprocess.run(["node", str(ROOT / "src/backend/tools/collect-npm-notices.mjs"), str(project), str(output)],
                                    text=True, capture_output=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            inventory = json.loads((output / "inventory.json").read_text())
            self.assertEqual(inventory["packages"][0]["declaredLicense"], "TEST-ONLY-DECLARATION")
            self.assertEqual((output / inventory["packages"][0]["notices"][0]).read_text(), "FABRICATED TEST NOTICE ONLY\n")
            bom = json.loads((output / "bom.cdx.json").read_text())
            self.assertEqual(bom["bomFormat"], "CycloneDX")
            self.assertEqual(bom["components"][0]["name"], "fabricated-package")
            self.assertNotIn("DO-NOT-COPY-OTHER-PACKAGE-CONFIG", (output / "inventory.json").read_text())
        finally:
            if fixture.exists():
                shutil.rmtree(fixture)


if __name__ == "__main__":
    unittest.main()
