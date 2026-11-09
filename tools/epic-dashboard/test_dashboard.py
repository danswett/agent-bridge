import json
import unittest
from datetime import datetime
from pathlib import Path
from unittest.mock import patch

import dashboard


class DashboardTests(unittest.TestCase):
    def setUp(self):
        self.fixture = json.loads(Path(__file__).with_name("fixture.json").read_text(encoding="utf-8"))
        self.issues = {issue["number"]: issue for issue in self.fixture["issues"]}

    def render(self, at="2026-11-04T23:55:00+00:00"):
        return dashboard.render(self.fixture["items"], self.issues, self.fixture["release"],
                                datetime.fromisoformat(at))

    def test_closed_and_renamed_trackers_keep_their_progress(self):
        """A closed and renamed tracker keeps its stable epic name and work counts."""
        self.assertIn("| [Trust](https://github.com/danswett/agent-bridge/issues/749) | Building (tracker closed) | "
                      "`▓▓░░░░░░░░` 1/5 · **1 P0** | 1 | 1 | #209, #690 |", self.render())

    def test_missing_tracker_and_missing_fields_are_visible(self):
        """A missing tracker or field does not prevent rendering the other epics."""
        body = self.render()
        self.assertIn("/issues/728) | Missing tracker |", body)
        self.assertIn("/issues/750) | Planned |", body)
        self.issues[747] = {"number": 747, "state": "open"}
        self.assertIn("/issues/747) | Missing from Project |", self.render())

    def test_pacific_time_follows_daylight_saving_and_release_dates(self):
        """Dashboard timestamps and release dates use Pacific time across DST."""
        for utc, pacific in [("2026-07-04T23:55:00+00:00", "2026-07-04 16:55 PT"),
                             ("2026-11-04T23:55:00+00:00", "2026-11-04 15:55 PT")]:
            with self.subTest(utc=utc):
                self.assertIn(f"_Updated {pacific},", self.render(utc))
        self.fixture["release"]["publishedAt"] = "2026-11-04T01:00:00Z"
        self.assertIn("`v0.1.2210` (2026-11-03)", self.render())

    def test_only_material_changes_request_a_body_edit(self):
        """An hourly timestamp alone never requests an edit, but a work change does."""
        current = self.render()
        later = self.render("2026-11-05T00:55:00+00:00")
        self.assertFalse(dashboard.body_changed(current.rstrip(), later))
        self.issues[209]["state"] = "closed"
        self.assertTrue(dashboard.body_changed(current, self.render()))

    def test_project_read_combines_pages_and_ignores_foreign_issues(self):
        """Project pages are combined without confusing equal issue numbers in other repos."""
        def page(repository, fields):
            return {"data": {"user": {"projectV2": {"items": {"nodes": [{
                "content": {"number": 209, "repository": {"nameWithOwner": repository}},
                "fieldValues": {"pageInfo": {"hasNextPage": False}, "nodes": fields},
            }]}}}}}
        pages = [page(dashboard.R, [{"field": {"name": "Epic"}, "name": "1 Trust"}, {}]),
                 page("other/repo", []), page(dashboard.R, [])]
        with patch.object(dashboard, "run", return_value=json.dumps(pages)) as run:
            self.assertEqual([{"number": 209, "epic": "1 Trust"}, {"number": 209}], dashboard.read_project())
            self.assertIn("--paginate", run.call_args.args)

    def test_unavailable_project_never_becomes_an_empty_dashboard(self):
        """An unavailable Project fails rather than publishing an empty dashboard."""
        with patch.object(dashboard, "run", return_value='[{"data":{"user":{"projectV2":null}}}]'):
            with self.assertRaisesRegex(ValueError, "Project 1 is unavailable"):
                dashboard.read_project()


if __name__ == "__main__":
    unittest.main(verbosity=2)
