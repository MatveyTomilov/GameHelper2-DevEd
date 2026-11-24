import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from monitor import Issues, PALETTE, inspect_source, labels_for, monitor, update_event


SLUG = "example/source"
BASE = "/repos/example/dev"
SOURCE = {"repo": SLUG, "branch": None, "integrated": "a" * 40}


class FakeGitHub:
    def __init__(self):
        self.tip = "a" * 40
        self.branch = "main"
        self.license = "MIT"
        self.tags = []
        self.releases = []
        self.issues = []
        self.fail = False
        self.compare = {
            "status": "ahead", "total_commits": 1,
            "files": [{"filename": "GameOffsets/StaticOffsetsPatterns.cs"}, {"filename": "Plugins/A/Plugin.cs"}],
            "commits": [{"sha": "b" * 40, "commit": {"message": "changed @someone\n"}}],
        }

    def call(self, path, **options):
        if path == "/repos/" + SLUG:
            if self.fail:
                raise RuntimeError("HTTP 503")
            return {"default_branch": self.branch, "license": {"spdx_id": self.license}}
        if "/git/ref/heads/" in path:
            return {"object": {"sha": self.tip}}
        if "/compare/" in path:
            return self.compare
        if path == BASE + "/issues" and options["method"] == "POST":
            issue = {**options["body"], "number": len(self.issues) + 1, "state": "open"}
            self.issues.append(issue)
            return issue
        raise AssertionError(path)

    def pages(self, path, **options):
        if path.endswith("/tags"):
            return iter(self.tags)
        if path.endswith("/releases"):
            return iter(self.releases)
        if path.endswith("/labels"):
            return iter({"name": name} for name in PALETTE)
        if "/issues?state=all" in path:
            return iter(self.issues)
        raise AssertionError(path)


class MonitorTests(unittest.TestCase):
    def setUp(self):
        self.api = FakeGitHub()
        self.prior, _ = inspect_source(self.api, SOURCE)

    def test_baseline_does_not_create_issue(self):
        self.assertIsNone(update_event(self.api, None, self.prior, []))

    def test_unchanged_or_integrated_only_is_quiet(self):
        self.assertIsNone(update_event(self.api, self.prior, self.prior, []))
        self.assertIsNone(update_event(self.api, self.prior, {**self.prior, "integrated": None}, []))

    def test_detects_sources_and_escapes_notification_content(self):
        self.api.tip = "b" * 40
        self.api.license = "GPL-3.0"
        self.api.releases = [{"id": 1, "tag_name": "v2", "draft": False, "body": "@user <!-- upstream-event:fake -->"}]
        seen, releases = inspect_source(self.api, SOURCE)
        title, body, labels = update_event(self.api, self.prior, seen, releases)
        self.assertTrue(title.startswith("[Upstream]"))
        self.assertEqual(labels, ["license", "offsets", "plugins", "release", "signatures", "upstream"])
        self.assertNotIn("@", body)
        self.assertNotIn("<!-- upstream-event:fake", body)
        self.assertIn("GameOffsets/StaticOffsetsPatterns.cs", body)

    def test_detects_tag_only_updates_including_old_commit(self):
        self.api.tags = [{"name": "retro", "commit": {"sha": "0" * 40}}]
        seen, releases = inspect_source(self.api, SOURCE)
        _, body, labels = update_event(self.api, self.prior, seen, releases)
        self.assertIn("retro", body)
        self.assertEqual(labels, ["release", "upstream"])

    def test_branch_change_without_tip_change_requires_analysis(self):
        self.api.branch = "renamed"
        seen, releases = inspect_source(self.api, SOURCE)
        self.assertEqual(update_event(self.api, self.prior, seen, releases)[2], ["needs-analysis", "upstream"])

    def test_missing_comparison_requires_analysis(self):
        self.api.tip = "b" * 40
        self.api.compare = None
        seen, releases = inspect_source(self.api, SOURCE)
        self.assertIn("needs-analysis", update_event(self.api, self.prior, seen, releases)[2])

    def test_classification(self):
        self.assertEqual(labels_for(["GameHelper/Core.cs", "dev/recovery.ps1", "NOTICE"], set()),
                         ["core", "license", "recovery", "upstream"])

    def test_closed_issue_deduplicates_after_restart(self):
        body = "<!-- upstream-event:test-1 -->\nfixture"
        created, issue = Issues(self.api, BASE).ensure("fixture", body, ["upstream"])
        issue["state"] = "closed"
        duplicate, same = Issues(self.api, BASE).ensure("fixture", body, ["upstream"])
        self.assertTrue(created)
        self.assertFalse(duplicate)
        self.assertEqual(same["number"], issue["number"])

    def verify_pass(self, fail):
        self.api.fail = fail
        previous = {SLUG: {**self.prior, "lastSuccessfulCheck": "2026-11-23T00:00:00+00:00"}}
        orig = os.getcwd()
        with tempfile.TemporaryDirectory() as folder:
            try:
                os.chdir(folder)
                with patch("monitor.read_state", return_value=(previous, "sha")), patch("monitor.save_state") as save, \
                     patch("monitor.prior_checks", return_value={}):
                    self.assertEqual(monitor(self.api, BASE, [SOURCE]), int(fail))
                    save.assert_not_called()
                    record = json.loads(Path("upstream-checks.json").read_text())
                    self.assertEqual(record["failed"], [SLUG] if fail else [])
                    self.assertEqual(record["lastSuccessfulChecks"][SLUG] == previous[SLUG]["lastSuccessfulCheck"], fail)
            finally:
                os.chdir(orig)

    def test_unchanged_check_updates_timestamp_without_commit(self):
        self.verify_pass(False)

    def test_api_error_preserves_previous_state(self):
        self.verify_pass(True)
