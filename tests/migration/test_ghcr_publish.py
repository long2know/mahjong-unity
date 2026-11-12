import contextlib
import copy
import importlib.util
import io
import json
from pathlib import Path
import re
import shlex
import textwrap
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("publish", ROOT / "scripts/ghcr_publish.py")
publish = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publish)
SHA = "a" * 40
DIGEST = "sha256:" + "b" * 64


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.pr = {"head": {"repo": {"full_name": publish.REPO}, "sha": SHA},
                   "base": {"repo": {"full_name": publish.REPO}, "ref": "dev"},
                   "state": "open", "merged_at": None, "body": ""}
        self.calls = []

    def command(self, *args):
        self.calls.append(args)
        if args[0] == "git":
            return "" if "status" in args else SHA
        return json.dumps({"digest": DIGEST})

    def test_prepare_checks_clean_exact_same_repo_dev_head_without_any_bundle(self):
        with patch.object(publish, "command", self.command), patch.object(publish, "api", return_value=self.pr), contextlib.redirect_stdout(io.StringIO()):
            publish.prepare(ROOT, SHA, "36")
        self.assertEqual(publish.REPO, "long2know/mahjong-web")
        self.assertTrue(any("status" in call for call in self.calls))
        self.assertFalse(any(call[0] == "docker" for call in self.calls))

    def test_prepare_rejects_dirty_source_forks_wrong_head_or_base(self):
        cases = []
        for path, value in [(("head", "sha"), "c" * 40), (("base", "ref"), "main"),
                            (("head", "repo", "full_name"), "outside/fork")]:
            pr = copy.deepcopy(self.pr)
            target = pr
            for key in path[:-1]:
                target = target[key]
            target[path[-1]] = value
            cases.append(pr)
        for pr in cases:
            with patch.object(publish, "command", self.command), patch.object(publish, "api", return_value=pr), self.assertRaises(ValueError):
                publish.prepare(ROOT, SHA, "36")
        with patch.object(publish, "command", side_effect=[SHA, " M source.ts"]), self.assertRaises(ValueError):
            publish.prepare(ROOT, SHA, "36")

    def test_published_digest_runtime_gate_uses_approved_source_and_cannot_be_bypassed(self):
        workflow = (ROOT / ".github/workflows/ghcr-publish.yml").read_text()
        canonical = (ROOT / ".github/workflows/migration-build.yml").read_text().split("  image-runtime:\n", 1)[1]

        def verify(text):
            starts = [match.start() for match in re.finditer(r"(?m)^      - ", text)]
            steps = [text[start:end] for start, end in zip(starts, starts[1:] + [len(text)])]
            def step(pattern):
                matches = [index for index, value in enumerate(steps) if re.search(pattern, value, re.M)]
                self.assertEqual(len(matches), 1, pattern)
                return matches[0]
            build = step(r"^        id: build$")
            source = step(r"^          path: source$")
            runtime = step(r"^        id: runtime$")
            upload = step(r"uses: actions/upload-artifact@")
            record = step(r"name: Record runtime-checked candidate identity")
            self.assertLess(source, build)
            self.assertLess(build, runtime)
            self.assertLess(runtime, upload)
            self.assertLess(upload, record)
            self.assertIn("          ref: ${{ inputs.source_sha }}", steps[source])
            self.assertIn("          push: true", steps[build])
            self.assertIn("docker/build-push-action@", steps[build])
            self.assertNotIn("continue-on-error", text)
            for index in (runtime, record):
                self.assertNotRegex(steps[index], r"(?m)^        if:")
            self.assertIn("        working-directory: source\n", steps[runtime])
            self.assertIn("          DIGEST: ${{ steps.build.outputs.digest }}\n", steps[runtime])
            self.assertIn("          SOURCE_SHA: ${{ inputs.source_sha }}\n", steps[runtime])
            lines = textwrap.dedent(steps[runtime].split("        run: |\n", 1)[1]).strip().splitlines()
            commands = [shlex.split(line) for line in lines]
            self.assertEqual(len(commands), 9)
            self.assertEqual(commands[0], ["set", "-euo", "pipefail"])
            self.assertEqual(commands[1:3], [
                ["[[", "$DIGEST", "=~", "^sha256:[0-9a-f]{64}$", "]]"],
                ["[[", "$SOURCE_SHA", "=~", "^[0-9a-f]{40}$", "]]"],
            ])
            self.assertEqual(commands[3], ["IMAGE=" + publish.IMAGE + "@${DIGEST}"])
            self.assertEqual(commands[4], ["docker", "pull", "--platform", "linux/amd64", "$IMAGE"])
            self.assertEqual(commands[5:7], [
                ["python3", "-B", "tests/migration/" + runner, "--image", "$IMAGE", "--source-sha", "$SOURCE_SHA"]
                for runner in ("container_smoke.py", "container_proxy_smoke.py")
            ])
            self.assertEqual(lines[7], 'IMAGE_ID="$(docker image inspect --format \'{{.Id}}\' "$IMAGE")"')
            self.assertEqual(commands[8], ["printf", "image=%s\\nimage_id=%s\\n", "$IMAGE", "$IMAGE_ID", ">>", "$GITHUB_OUTPUT"])
            self.assertIn("          IMAGE: ${{ steps.runtime.outputs.image }}\n", steps[record])
            self.assertIn("          IMAGE_ID: ${{ steps.runtime.outputs.image_id }}\n", steps[record])
            self.assertIn("          SOURCE_SHA: ${{ inputs.source_sha }}\n", steps[record])
            self.assertIn("        if: always()\n", steps[upload])
            self.assertIn("          name: candidate-runtime-${{ github.run_id }}-${{ github.run_attempt }}\n", steps[upload])
            self.assertIn("          if-no-files-found: error\n", steps[upload])
            paths = textwrap.dedent(steps[upload].split("          path: |\n", 1)[1]).strip().splitlines()
            expected = textwrap.dedent(canonical.split("          path: |\n", 1)[1]).strip().splitlines()
            self.assertEqual(paths, ["source/" + path for path in expected])
            self.assertIn(re.search(r"actions/setup-node@[0-9a-f]{40}", canonical).group(), "".join(steps[build + 1:runtime]))

        verify(workflow)
        mutations = [
            ("steps.build.outputs.digest", "inputs.source_sha"),
            ('IMAGE="ghcr.io/long2know/mahjong-web@${DIGEST}"', 'IMAGE="mahjong-web:ci"'),
            ('docker pull --platform linux/amd64 "$IMAGE"', 'docker pull ghcr.io/long2know/mahjong-web:latest'),
            ('--image "$IMAGE"', '--image mahjong-web:ci'),
            ('--source-sha "$SOURCE_SHA"', '--source-sha "$(git rev-parse HEAD)"'),
            ("SOURCE_SHA: ${{ inputs.source_sha }}", "SOURCE_SHA: ${{ github.sha }}"),
            ("id: runtime\n", "id: runtime\n        continue-on-error: true\n"),
            ("id: runtime\n", "id: runtime\n        if: always()\n"),
            ("id: runtime\n        working-directory: source", "id: runtime\n        working-directory: publisher"),
            ('--source-sha "$SOURCE_SHA"\n', '--source-sha "$SOURCE_SHA" || true\n'),
            ("set -euo pipefail", "set +e"),
            ('          python3 -B tests/migration/container_proxy_smoke.py --image "$IMAGE" --source-sha "$SOURCE_SHA"\n', ""),
            ("source/artifacts/base-url/hosted-nginx/receipt.json", "source/artifacts/**"),
            ("          IMAGE: ${{ steps.runtime.outputs.image }}", "          IMAGE: mahjong-web:ci"),
        ]
        for before, after in mutations:
            with self.subTest(mutation=after):
                self.assertIn(before, workflow)
                with self.assertRaises(AssertionError):
                    verify(workflow.replace(before, after))

    def promotion_api(self, endpoint):
        if endpoint.startswith("environments/"):
            return {"protection_rules": [{"type": "required_reviewers", "reviewers": [{"id": 1}]}]}
        if endpoint.startswith("actions/"):
            return {"conclusion": "success", "event": "workflow_dispatch", "head_branch": "main",
                    "path": ".github/workflows/ghcr-publish.yml"}
        return {**self.pr, "merged_at": "2026-11-11T00:00:00Z", "body": publish.IMAGE + "@" + DIGEST + "\n" + SHA}

    def test_promotion_requires_protected_approval_and_only_moves_new_digest_not_legacy_alias(self):
        with patch.object(publish, "command", self.command), patch.object(publish, "api", self.promotion_api), \
                patch.object(publish.subprocess, "run") as run, contextlib.redirect_stdout(io.StringIO()):
            publish.promote(DIGEST, SHA, "123", "1", "36", "runtime-qualified:" + DIGEST)
            args = run.call_args.args[0]
            self.assertIn("ghcr.io/long2know/mahjong-web:latest", args)
            self.assertFalse(any("mahjong-unity" in value for value in args))

    def test_wrong_digest_confirmation_or_missing_environment_reviewers_prevents_mutation(self):
        with patch.object(publish, "command", self.command), patch.object(publish.subprocess, "run") as run:
            with self.assertRaises(ValueError):
                publish.promote(DIGEST, SHA, "123", "1", "36", "yes")
            with patch.object(publish, "api", return_value={"protection_rules": []}), self.assertRaises(ValueError):
                publish.promote(DIGEST, SHA, "123", "1", "36", "runtime-qualified:" + DIGEST)
            run.assert_not_called()

    def test_source_tag_drift_or_unmerged_acceptance_prevents_latest(self):
        def wrong(*args):
            if args[0] == "docker":
                return json.dumps({"digest": "sha256:" + "c" * 64})
            return self.command(*args)
        with patch.object(publish, "command", wrong), patch.object(publish, "api", self.promotion_api), \
                patch.object(publish.subprocess, "run") as run, self.assertRaises(ValueError):
            publish.promote(DIGEST, SHA, "123", "1", "36", "runtime-qualified:" + DIGEST)
        run.assert_not_called()
        def unmerged(endpoint):
            result = self.promotion_api(endpoint)
            if endpoint.startswith("pulls/"):
                result["merged_at"] = None
            return result
        with patch.object(publish, "command", self.command), patch.object(publish, "api", unmerged), \
                patch.object(publish.subprocess, "run") as run, self.assertRaises(ValueError):
            publish.promote(DIGEST, SHA, "123", "1", "36", "runtime-qualified:" + DIGEST)
        run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
