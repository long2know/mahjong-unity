import contextlib
import copy
import importlib.util
import io
import json
from pathlib import Path
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
