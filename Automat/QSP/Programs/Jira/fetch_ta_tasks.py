"""
fetch_ta_tasks.py — Fetch Technical Analysis tasks from Jira.

Picks up two kinds of TA work items for team Artemis in the active sprint:

  1. Standalone Tasks titled "Technische analyse van …"
  2. Sub-tasks titled "Technische analyse" within a story

Both cases:
  - Status "Open" and unassigned  → free to pick up
  - Status "In Progress" assigned to the current user → already in progress

Usage:
    python fetch_ta_tasks.py
"""

import asyncio
from dataclasses import dataclass, field

from jira_client import JiraClient, JIRA_BASE


# ---------------------------------------------------------------------------
# JQL — two separate queries unified in one OR block
# ---------------------------------------------------------------------------

TA_JQL = (
    'project = QSP '
    'AND sprint in openSprints() '
    'AND ('
    '  (issuetype = Task AND summary ~ "Technische analyse van") '
    '  OR (issuetype = "Sub-task" AND summary = "Technische analyse")'
    ') '
    'AND ('
    '  (status = "Open" AND assignee is EMPTY) '
    '  OR (status = "In Progress" AND assignee = currentUser())'
    ') '
    'ORDER BY created ASC'
)


# ---------------------------------------------------------------------------
# Data model
# ---------------------------------------------------------------------------

@dataclass
class TATask:
    key: str
    summary: str
    status: str
    assignee: str
    priority: str
    parent_key: str
    parent_summary: str
    url: str = field(init=False)

    def __post_init__(self):
        self.url = f"{JIRA_BASE}/browse/{self.key}"

    @property
    def is_free(self) -> bool:
        return self.assignee == "Unassigned"

    def display(self) -> str:
        icon = "[FREE]" if self.is_free else "[WIP] "
        lines = [
            f"  {icon}  [{self.key}]  {self.summary}",
            f"       Status   : {self.status}",
            f"       Assignee : {self.assignee}",
            f"       Priority : {self.priority}",
        ]
        if self.parent_key:
            lines.append(f"       Parent   : [{self.parent_key}] {self.parent_summary}")
        lines.append(f"       URL      : {self.url}")
        return "\n".join(lines)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _parse_task(issue: dict) -> TATask:
    f = issue.get("fields", {})
    assignee_obj = f.get("assignee")
    parent_obj = f.get("parent", {}) or {}
    parent_fields = parent_obj.get("fields", {}) or {}

    return TATask(
        key=issue["key"],
        summary=f.get("summary", ""),
        status=(f.get("status") or {}).get("name", ""),
        assignee=assignee_obj["displayName"] if assignee_obj else "Unassigned",
        priority=(f.get("priority") or {}).get("name", ""),
        parent_key=parent_obj.get("key", ""),
        parent_summary=parent_fields.get("summary", ""),
    )


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

async def fetch_ta_tasks() -> list[TATask]:
    async with JiraClient() as client:
        await client.ensure_logged_in()

        print("\n[*] Searching for Technical Analysis tasks...")
        issues = await client.search_issues(
            TA_JQL,
            fields=["summary", "status", "assignee", "priority", "parent"],
        )

        tasks = [_parse_task(i) for i in issues]
        return tasks


def print_results(tasks: list[TATask]) -> None:
    if not tasks:
        print("\n[+] No Technical Analysis tasks to pick up right now.")
        return

    free = [t for t in tasks if t.is_free]
    mine = [t for t in tasks if not t.is_free]

    print(f"\n[=] Found {len(tasks)} Technical Analysis task(s):\n")

    if free:
        print(f"  -- Free to pick up ({len(free)}) --")
        for t in free:
            print(t.display())
            print()

    if mine:
        print(f"  -- Already in progress / assigned to you ({len(mine)}) --")
        for t in mine:
            print(t.display())
            print()


if __name__ == "__main__":
    tasks = asyncio.run(fetch_ta_tasks())
    print_results(tasks)
