"""
board_overview.py — Sprint board overview for team Artemis.

Fetches all stories in the active sprint for board 668, filtered by
the Artemis quick filter, and shows each story with its subtasks and statuses.

Usage:
    python board_overview.py
"""
import asyncio
from jira_client import JiraClient, JIRA_BASE

BOARD_ID   = 668
QF_ID      = 7789   # Artemis quick filter
SPRINT_ID  = 35596  # current sprint (update each sprint or auto-detect)


async def get_active_sprint(client: JiraClient) -> dict:
    """Return the currently active sprint for the board."""
    data = await client._page_fetch(
        f"{JIRA_BASE}/rest/agile/1.0/board/{BOARD_ID}/sprint",
        params={"state": "active"},
    )
    sprints = data.get("values", [])
    if not sprints:
        raise RuntimeError("No active sprint found for board.")
    return sprints[0]


async def get_quickfilter_jql(client: JiraClient) -> str:
    """Return the JQL clause for the Artemis quick filter."""
    data = await client._page_fetch(
        f"{JIRA_BASE}/rest/agile/1.0/board/{BOARD_ID}/quickfilter",
    )
    for qf in data.get("values", []):
        if qf.get("id") == QF_ID:
            return qf.get("query", "")
    raise RuntimeError(f"Quick filter {QF_ID} not found on board {BOARD_ID}.")


async def get_sprint_issues(client: JiraClient, sprint_id: int, extra_jql: str) -> list[dict]:
    """Fetch all non-subtask issues in a sprint, optionally filtered by extra JQL."""
    base_jql = (
        f'project = QSP AND sprint = {sprint_id} '
        f'AND issuetype not in subTaskIssueTypes() '
    )
    jql = f"{base_jql} AND ({extra_jql})" if extra_jql else base_jql
    jql += " ORDER BY created ASC"
    return await client.search_issues(
        jql,
        fields=["summary", "status", "assignee", "issuetype", "subtasks", "priority"],
        max_total=200,
    )


async def get_subtask_details(client: JiraClient, subtask_keys: list[str]) -> list[dict]:
    """Fetch status + assignee for a list of subtask keys in one JQL call."""
    if not subtask_keys:
        return []
    keys_jql = ", ".join(subtask_keys)
    return await client.search_issues(
        f"issue in ({keys_jql}) ORDER BY created ASC",
        fields=["summary", "status", "assignee", "issuetype"],
        max_total=500,
    )


def fmt_assignee(issue: dict) -> str:
    a = (issue.get("fields") or {}).get("assignee")
    return a["displayName"] if a else "Unassigned"


def fmt_status(issue: dict) -> str:
    return ((issue.get("fields") or {}).get("status") or {}).get("name", "?")


def print_overview(stories: list[dict], subtask_map: dict) -> None:
    print()
    print("=" * 80)
    print(f"  Sprint board overview — Artemis ({len(stories)} stories)")
    print("=" * 80)

    for story in stories:
        f        = story.get("fields", {})
        key      = story["key"]
        summary  = f.get("summary", "")[:70]
        status   = fmt_status(story)
        assignee = fmt_assignee(story)
        itype    = (f.get("issuetype") or {}).get("name", "?")

        print(f"\n  [{key}]  {itype}  |  {status}  |  {assignee}")
        print(f"  {summary}")

        raw_subtasks = f.get("subtasks") or []
        if raw_subtasks:
            for st in raw_subtasks:
                st_key     = st["key"]
                detail     = subtask_map.get(st_key)
                if detail:
                    st_status   = fmt_status(detail)
                    st_assignee = fmt_assignee(detail)
                    st_summary  = (detail.get("fields") or {}).get("summary", "")[:60]
                    st_itype    = ((detail.get("fields") or {}).get("issuetype") or {}).get("name", "?")
                else:
                    st_status   = (st.get("fields") or {}).get("status", {}).get("name", "?")
                    st_assignee = "?"
                    st_summary  = (st.get("fields") or {}).get("summary", "")[:60]
                    st_itype    = "Sub-task"
                print(f"    -> [{st_key}]  {st_itype}  |  {st_status}  |  {st_assignee}  |  {st_summary}")
        else:
            print("    (no subtasks)")

    print()
    print("=" * 80)


async def main():
    async with JiraClient() as client:
        await client.ensure_logged_in()

        print("[*] Fetching active sprint...")
        sprint = await get_active_sprint(client)
        sprint_id   = sprint["id"]
        sprint_name = sprint["name"]
        print(f"[+] Sprint: {sprint_name} (id={sprint_id})")

        print("[*] Fetching Artemis quick filter JQL...")
        qf_jql = await get_quickfilter_jql(client)
        print(f"[+] Quick filter JQL: {qf_jql}")

        print("[*] Fetching sprint stories...")
        stories = await get_sprint_issues(client, sprint_id, qf_jql)
        print(f"[+] Found {len(stories)} stories.")

        # Collect all subtask keys, fetch their details in one batch
        all_subtask_keys = [
            st["key"]
            for story in stories
            for st in (story.get("fields", {}).get("subtasks") or [])
        ]
        subtask_map = {}
        if all_subtask_keys:
            print(f"[*] Fetching details for {len(all_subtask_keys)} subtask(s)...")
            subtask_issues = await get_subtask_details(client, all_subtask_keys)
            subtask_map = {i["key"]: i for i in subtask_issues}

        print_overview(stories, subtask_map)


if __name__ == "__main__":
    asyncio.run(main())
