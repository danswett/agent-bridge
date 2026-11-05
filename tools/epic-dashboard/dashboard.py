"""Read Project 1 and public repository data; print the #755 dashboard, never write it."""

import argparse
import json
import re
import subprocess
from datetime import datetime
from pathlib import Path
from zoneinfo import ZoneInfo

R = "danswett/agent-bridge"
TRACKERS = {
    "1": 749, "2": 750, "14": 728, "4": 747, "3": 751, "5": 535, "6": 568, "7": 597, "12": 357,
}
NAMES = {
    "1": "Trust", "2": "Daily-use reliability", "14": "Agent messaging", "4": "Dogfood readiness",
    "3": "Migration", "5": "Rebrand", "6": "Ops", "7": "Performance", "12": "Quality",
}
BETA1_GATE = [209, 212, 220, 662, 690, 516, 530, 499, 731, 732, 733, 734, 735, 736, 384, 54, 231,
              678, 451, 737, 738, 739, 740, 742, 744, 746]
STAGES = ["Planned", "Designing", "Building", "In Beta", "In production", "Done"]
PACIFIC = ZoneInfo("America/Los_Angeles")

# Only this query runs with the read:project credential. Repository reads use GITHUB_TOKEN.
PROJECT_QUERY = """
query($endCursor: String) {
  user(login: "danswett") {
    projectV2(number: 1) {
      items(first: 100, after: $endCursor) {
        pageInfo { hasNextPage endCursor }
        nodes {
          content {
            ... on Issue { number repository { nameWithOwner } }
          }
          fieldValues(first: 100) {
            pageInfo { hasNextPage }
            nodes {
              ... on ProjectV2ItemFieldSingleSelectValue {
                name field { ... on ProjectV2FieldCommon { name } }
              }
              ... on ProjectV2ItemFieldNumberValue {
                number field { ... on ProjectV2FieldCommon { name } }
              }
              ... on ProjectV2ItemFieldTextValue {
                text field { ... on ProjectV2FieldCommon { name } }
              }
            }
          }
        }
      }
    }
  }
}
"""


def run(*args):
    return subprocess.run(args, capture_output=True, text=True, encoding="utf-8", check=True).stdout


def read_project():
    pages = json.loads(run("gh", "api", "graphql", "--paginate", "--slurp", "-f",
                          f"query={PROJECT_QUERY}"))
    items = []
    for page in pages:
        if page.get("errors"):
            raise ValueError("Project query failed; leaving #755 unchanged.")
        project = (page.get("data", {}).get("user") or {}).get("projectV2")
        if project is None:
            raise ValueError("Project 1 is unavailable; check EPIC_DASHBOARD_PROJECT_READ.")
        for node in project["items"]["nodes"]:
            content = node.get("content") or {}
            if content.get("repository", {}).get("nameWithOwner") != R:
                continue
            fields = node["fieldValues"]
            if fields["pageInfo"]["hasNextPage"]:
                raise ValueError("Project item has more than 100 fields; refusing partial data.")
            item = {"number": content["number"]}
            for value in fields["nodes"]:
                name = (value.get("field") or {}).get("name", "").lower()
                if name in {"epic", "stage", "rank", "status"}:
                    item[name] = next((value[k] for k in ("name", "number", "text") if k in value), None)
            items.append(item)
    return items


def read_repository():
    pages = json.loads(run("gh", "api", "--paginate", "--slurp",
                          f"repos/{R}/issues?state=all&per_page=100"))
    issues = {i["number"]: i for page in pages for i in page if "pull_request" not in i}
    releases = json.loads(run("gh", "release", "list", "-R", R, "--limit", "1",
                             "--exclude-drafts", "--json", "tagName,publishedAt"))
    return issues, releases[0] if releases else None


def bar(done, total, width=10):
    filled = round(width * done / total) if total else 0
    return "▓" * filled + "░" * (width - filled)


def rank(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def render(items, issues, release, now=None):
    open_issues = {n: i for n, i in issues.items() if i.get("state", "").lower() == "open"}
    by_epic, stage_of = {}, {}
    trackers = set(TRACKERS.values())
    for item in items:
        n = item["number"]
        if n in trackers:
            stage_of[n] = item.get("stage") if item.get("stage") in STAGES else "Planned"
            continue
        epic = str(item.get("epic") or "").split(" ")[0]
        if not epic or n not in issues:
            continue
        labels = {label["name"] for label in issues[n].get("labels", [])}
        by_epic.setdefault(epic, []).append({
            "n": n, "open": n in open_issues, "rank": rank(item.get("rank")),
            "blocked": "status:blocked" in labels,
            "flight": item.get("status") == "In Progress" or "status:in-progress" in labels,
            "p0": "priority:P0" in labels,
        })

    updated = (now or datetime.now(PACIFIC)).astimezone(PACIFIC).strftime("%Y-%m-%d %H:%M PT")
    out = ["# Slipgates: epic dashboard", "",
           f"_Updated {updated}, generated from [Project 1](https://github.com/users/danswett/projects/1) and #696. "
           "Do not edit by hand; this issue's body is rewritten on each run._", ""]
    if release:
        published = datetime.fromisoformat(release["publishedAt"].replace("Z", "+00:00"))
        date = published.astimezone(PACIFIC).strftime("%Y-%m-%d")
        out.append(f"**Latest release:** `{release['tagName']}` ({date})")
    gate_open = [n for n in BETA1_GATE if n in open_issues]
    gdone = len(BETA1_GATE) - len(gate_open)
    out += [f"**Beta 1 gate:** {bar(gdone, len(BETA1_GATE))} {gdone}/{len(BETA1_GATE)} · open: "
            + ", ".join(f"#{n}" for n in gate_open[:12]) + (" …" if len(gate_open) > 12 else ""), ""]
    out += ["| Epic | Stage | Progress | In flight | Blocked | Next up |", "|---|---|---|---|---|---|"]
    for epic, tracker in TRACKERS.items():
        rows = by_epic.get(epic, [])
        total, done = len(rows), sum(1 for row in rows if not row["open"])
        opens = [row for row in rows if row["open"]]
        p0 = sum(1 for row in opens if row["p0"])
        flight = sum(1 for row in opens if row["flight"])
        blocked = sum(1 for row in opens if row["blocked"])
        ready = sorted((row for row in opens
                        if not row["blocked"] and not row["flight"] and row["rank"] is not None),
                       key=lambda row: row["rank"])
        nxt = ", ".join(f"#{row['n']}" for row in ready[:2]) or "—"
        stage = stage_of.get(tracker, "Missing from Project")
        if tracker not in issues:
            stage = "Missing tracker"
        elif tracker not in open_issues:
            stage += " (tracker closed)"
        p0s = f" · **{p0} P0**" if p0 else ""
        out.append(f"| [{NAMES[epic]}](https://github.com/{R}/issues/{tracker}) | {stage} | "
                   f"`{bar(done, total)}` {done}/{total}{p0s} | {flight} | {blocked} | {nxt} |")
    out += ["", "**Stages:** " + " → ".join(STAGES) + ". *In Beta* means in a published pre-release; "
            "*In production* means in the release the fleet runs, or deployed to the production relay, and "
            "verified live.",
            "Progress counts each epic's items from the 2026-11-04 baseline; items closed earlier aren't counted. "
            "Parked epics aren't shown."]
    return "\n".join(out) + "\n"


def body_changed(current, generated):
    def without_timestamp(body):
        return re.sub(r"^_Updated \d{4}-\d{2}-\d{2} \d{2}:\d{2} PT,",
                      "_Updated TIMESTAMP,", body, count=1, flags=re.MULTILINE).rstrip("\r\n")
    return without_timestamp(current) != without_timestamp(generated)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("project")
    generate = commands.add_parser("render")
    generate.add_argument("--project-file", type=Path, required=True)
    compare = commands.add_parser("compare")
    compare.add_argument("--current", type=Path, required=True)
    compare.add_argument("--generated", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "project":
        print(json.dumps(read_project()))
    elif args.command == "render":
        issues, release = read_repository()
        print(render(json.loads(args.project_file.read_text(encoding="utf-8")), issues, release), end="")
    else:
        changed = body_changed(args.current.read_text(encoding="utf-8"), args.generated.read_text(encoding="utf-8"))
        print(f"changed={str(changed).lower()}")


if __name__ == "__main__":
    main()
