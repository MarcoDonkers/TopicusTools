"""
start_workday.py — Daily workday startup: Jira ticket overview for Artemis.

Displays two sections:
  1. MY TICKETS      — tickets assigned to the current user in the active sprint
                       (In Progress first, then Open/To Do, excludes Done)
  2. FREE TO PICK UP — unassigned tickets in the active sprint with status
                       Open or To Do, filtered to the Artemis team

Usage:
    python start_workday.py
"""

import asyncio
from dataclasses import dataclass, field

from jira_client import JiraClient, JIRA_BASE

BOARD_ID = 668        # QSP - Sprintbord (hosts all team sprints)
SPRINT_TEAM = "Artemis"  # matched case-insensitively against sprint name

# Statuses that mean "done, skip it"
DONE_STATUSES = {"done", "gesloten", "closed", "resolved", "cancelled", "geannuleerd"}

# Sort weight per status (lower = shown first)
_STATUS_WEIGHT = {
    "in progress": 0,
    "open": 1,
    "to do": 1,
    "in review": 2,
    "reopened": 3,
}


# ---------------------------------------------------------------------------
# JQL builders
# ---------------------------------------------------------------------------

def _my_tickets_jql(sprint_id: int) -> str:
    done = ", ".join(f'"{s}"' for s in DONE_STATUSES)
    return (
        f"sprint = {sprint_id} "
        f"AND assignee = currentUser() "
        f"AND status not in ({done}) "
        "ORDER BY status ASC, priority ASC, created ASC"
    )


def _free_tickets_jql(sprint_id: int) -> str:
    return (
        f"sprint = {sprint_id} "
        f"AND assignee is EMPTY "
        f'AND status in ("Open", "To Do") '
        "ORDER BY priority ASC, created ASC"
    )


# ---------------------------------------------------------------------------
# Data model
# ---------------------------------------------------------------------------

@dataclass
class Ticket:
    key: str
    summary: str
    issue_type: str
    status: str
    priority: str
    parent_key: str
    parent_summary: str
    assignee: str
    url: str = field(init=False)

    def __post_init__(self):
        self.url = f"{JIRA_BASE}/browse/{self.key}"

    @property
    def status_weight(self) -> int:
        return _STATUS_WEIGHT.get(self.status.lower(), 99)

    def display(self, indent: str = "  ") -> str:
        lines = [
            f"{indent}[{self.key}]  {self.issue_type}  |  {self.status}  |  {self.priority}",
            f"{indent}  {self.summary}",
        ]
        if self.parent_key:
            lines.append(f"{indent}  Parent : [{self.parent_key}] {self.parent_summary}")
        lines.append(f"{indent}  URL    : {self.url}")
        return "\n".join(lines)


def _parse_ticket(issue: dict) -> Ticket:
    f = issue.get("fields", {})
    assignee_obj = f.get("assignee")
    parent_obj = f.get("parent") or {}
    parent_fields = parent_obj.get("fields") or {}

    return Ticket(
        key=issue["key"],
        summary=f.get("summary", ""),
        issue_type=(f.get("issuetype") or {}).get("name", "?"),
        status=(f.get("status") or {}).get("name", "?"),
        priority=(f.get("priority") or {}).get("name", "?"),
        parent_key=parent_obj.get("key", ""),
        parent_summary=parent_fields.get("summary", ""),
        assignee=assignee_obj["displayName"] if assignee_obj else "Unassigned",
    )


# ---------------------------------------------------------------------------
# Jira helpers
# ---------------------------------------------------------------------------

async def _get_artemis_sprint(client: JiraClient) -> dict:
    """Find the active sprint for SPRINT_TEAM on board BOARD_ID."""
    data = await client._page_fetch(
        f"{JIRA_BASE}/rest/agile/1.0/board/{BOARD_ID}/sprint",
        params={"state": "active"},
    )
    for sprint in data.get("values", []):
        if SPRINT_TEAM.lower() in sprint["name"].lower():
            return sprint
    raise RuntimeError(
        f"No active sprint matching '{SPRINT_TEAM}' found on board {BOARD_ID}."
    )


# ---------------------------------------------------------------------------
# Fetch
# ---------------------------------------------------------------------------

async def fetch_workday_tickets() -> tuple[str, list[Ticket], list[Ticket]]:
    """
    Returns (sprint_name, my_tickets, free_tickets).
    my_tickets is sorted: In Progress first, then Open/To Do.
    """
    async with JiraClient() as client:
        await client.ensure_logged_in()

        print(f"[*] Finding active '{SPRINT_TEAM}' sprint...")
        sprint = await _get_artemis_sprint(client)
        sprint_id = sprint["id"]
        sprint_name = sprint["name"]
        print(f"[+] Sprint: {sprint_name} (id={sprint_id})")

        fields = ["summary", "status", "assignee", "issuetype", "priority", "parent"]

        print("[*] Fetching my tickets...")
        my_raw = await client.search_issues(
            _my_tickets_jql(sprint_id), fields=fields, max_total=100
        )
        my_tickets = sorted(
            [_parse_ticket(i) for i in my_raw],
            key=lambda t: (t.status_weight, t.key),
        )

        print("[*] Fetching free tickets...")
        free_raw = await client.search_issues(
            _free_tickets_jql(sprint_id), fields=fields, max_total=100
        )
        free_tickets = [_parse_ticket(i) for i in free_raw]

        return sprint_name, my_tickets, free_tickets


# ---------------------------------------------------------------------------
# Display
# ---------------------------------------------------------------------------

def print_workday_overview(
    sprint_name: str,
    my_tickets: list[Ticket],
    free_tickets: list[Ticket],
) -> None:
    width = 72
    line = "=" * width

    print()
    print(line)
    print(f"  WORKDAY START — {sprint_name}")
    print(line)

    # Section 1: My tickets
    print(f"\n  MY TICKETS ({len(my_tickets)})")
    print("  " + "-" * (width - 2))
    if my_tickets:
        for ticket in my_tickets:
            print()
            print(ticket.display())
    else:
        print("\n  (no tickets assigned to you in this sprint)")

    # Section 2: Free to pick up
    print()
    print(f"\n  FREE TO PICK UP ({len(free_tickets)})")
    print("  " + "-" * (width - 2))
    if free_tickets:
        for ticket in free_tickets:
            print()
            print(ticket.display())
    else:
        print("\n  (no unassigned open tickets in this sprint)")

    print()
    print(line)


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    sprint_name, my_tickets, free_tickets = asyncio.run(fetch_workday_tickets())
    print_workday_overview(sprint_name, my_tickets, free_tickets)
