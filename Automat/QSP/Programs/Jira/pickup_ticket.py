"""
pickup_ticket.py — Assign a Jira ticket to yourself and set it to In Progress.

Also fetches and prints full ticket + parent details for context.

Usage:
    python pickup_ticket.py <TICKET-KEY>

Example:
    python pickup_ticket.py QSP-97069
"""

import asyncio
import sys

sys.stdout.reconfigure(encoding="utf-8")

from jira_client import JiraClient, JIRA_BASE

IN_PROGRESS_NAMES = {"in progress", "in behandeling", "in bewerking"}


async def pickup(ticket_key: str) -> None:
    async with JiraClient() as client:
        await client.ensure_logged_in()

        # Current user
        me = await client._page_fetch(f"{JIRA_BASE}/rest/api/3/myself")
        account_id = me["accountId"]
        print(f"[+] Logged in as: {me['displayName']}")

        # Fetch ticket
        print(f"[*] Fetching {ticket_key}...")
        issue = await client.get_issue(
            ticket_key,
            fields=["summary", "status", "assignee", "issuetype", "priority",
                    "parent", "description", "comment", "issuelinks"],
        )
        fields = issue["fields"]
        current_status = (fields.get("status") or {}).get("name", "?")
        print(f"[+] {ticket_key}: {fields['summary']} [{current_status}]")

        # Assign to self
        print(f"[*] Assigning {ticket_key} to {me['displayName']}...")
        await client.assign_issue(ticket_key, account_id)
        print(f"[+] Assigned.")

        # Transition to In Progress (skip if already there)
        if current_status.lower() not in IN_PROGRESS_NAMES:
            transitions = await client.get_transitions(ticket_key)
            in_progress_t = next(
                (t for t in transitions if t["name"].lower() in IN_PROGRESS_NAMES),
                None,
            )
            if in_progress_t:
                print(f"[*] Transitioning to '{in_progress_t['name']}'...")
                await client.transition_issue(ticket_key, in_progress_t["id"])
                print(f"[+] Status set to In Progress.")
            else:
                available = [t["name"] for t in transitions]
                print(f"[!] 'In Progress' transition not found. Available: {available}")
        else:
            print(f"[=] Already In Progress — skipping transition.")

        # Fetch parent if present
        parent_obj = fields.get("parent") or {}
        parent_key = parent_obj.get("key", "")
        parent_data = None
        if parent_key:
            print(f"[*] Fetching parent {parent_key}...")
            parent_data = await client.get_issue(
                parent_key,
                fields=["summary", "status", "description", "issuetype"],
            )

        # Print overview
        width = 72
        line = "=" * width
        print()
        print(line)
        print(f"  TICKET: {ticket_key}")
        print(line)
        print(f"  Summary : {fields['summary']}")
        print(f"  Type    : {(fields.get('issuetype') or {}).get('name', '?')}")
        print(f"  Status  : In Progress")
        print(f"  URL     : {JIRA_BASE}/browse/{ticket_key}")

        if parent_data:
            pf = parent_data["fields"]
            print()
            print(f"  PARENT: [{parent_key}] {pf['summary']}")
            print(f"  URL   : {JIRA_BASE}/browse/{parent_key}")
            desc_text = _adf_to_text(pf.get("description"))
            if desc_text:
                print()
                print("  PARENT DESCRIPTION:")
                print("  " + "-" * (width - 2))
                for ln in desc_text.strip().splitlines():
                    print(f"  {ln}")

        desc_text = _adf_to_text(fields.get("description"))
        if desc_text:
            print()
            print("  DESCRIPTION:")
            print("  " + "-" * (width - 2))
            for ln in desc_text.strip().splitlines():
                print(f"  {ln}")

        # Linked issues
        links = fields.get("issuelinks") or []
        if links:
            print()
            print("  LINKED ISSUES:")
            print("  " + "-" * (width - 2))
            for lnk in links:
                rel_type = lnk.get("type", {})
                if "outwardIssue" in lnk:
                    rel = rel_type.get("outward", "relates to")
                    other = lnk["outwardIssue"]
                else:
                    rel = rel_type.get("inward", "is related to")
                    other = lnk.get("inwardIssue", {})
                key = other.get("key", "?")
                summary = (other.get("fields") or {}).get("summary", "")
                status = ((other.get("fields") or {}).get("status") or {}).get("name", "")
                print(f"  [{key}]  {rel}  —  {summary}  [{status}]")
                print(f"    {JIRA_BASE}/browse/{key}")

        # Comments
        comment_obj = fields.get("comment") or {}
        comments = comment_obj.get("comments") or []
        if comments:
            print()
            print(f"  COMMENTS ({len(comments)}):")
            print("  " + "-" * (width - 2))
            for c in comments:
                author = (c.get("author") or {}).get("displayName", "?")
                created = c.get("created", "")[:10]
                body = _adf_to_text(c.get("body")).strip()
                print(f"  [{created}] {author}:")
                for ln in body.splitlines():
                    print(f"    {ln}")
                print()

        print()
        print(line)


def _adf_to_text(node) -> str:
    """Recursively extract plain text from Atlassian Document Format."""
    if not node:
        return ""
    if isinstance(node, str):
        return node
    if isinstance(node, list):
        return "".join(_adf_to_text(n) for n in node)
    if isinstance(node, dict):
        node_type = node.get("type", "")
        # Smart links / inline cards: render as their URL
        if node_type == "inlineCard":
            return node.get("attrs", {}).get("url", "") + " "
        text = node.get("text", "")
        children = "".join(_adf_to_text(c) for c in node.get("content", []))
        result = text + children
        if node_type in ("paragraph", "heading"):
            result += "\n"
        elif node_type == "listItem":
            result = "• " + result
        elif node_type in ("bulletList", "orderedList"):
            result += "\n"
        return result
    return ""


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(f"Usage: python pickup_ticket.py <TICKET-KEY>")
        sys.exit(1)
    asyncio.run(pickup(sys.argv[1]))
