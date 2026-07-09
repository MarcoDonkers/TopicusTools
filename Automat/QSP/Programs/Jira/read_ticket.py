"""
read_ticket.py — Read-only view of a Jira ticket (no assignment, no transition).

Prints the full ticket summary, description, parent details, linked issues
and comments. Makes no writes to Jira.

Usage:
    python read_ticket.py <TICKET-KEY>
"""

import asyncio
import sys

sys.stdout.reconfigure(encoding="utf-8")

from jira_client import JiraClient, JIRA_BASE
from pickup_ticket import _adf_to_text


async def read(ticket_key: str) -> None:
    async with JiraClient() as client:
        await client.ensure_logged_in()

        print(f"[*] Fetching {ticket_key}...")
        issue = await client.get_issue(
            ticket_key,
            fields=["summary", "status", "assignee", "issuetype", "priority",
                    "parent", "description", "comment", "issuelinks", "attachment"],
        )
        fields = issue["fields"]

        parent_obj = fields.get("parent") or {}
        parent_key = parent_obj.get("key", "")
        parent_data = None
        if parent_key:
            print(f"[*] Fetching parent {parent_key}...")
            parent_data = await client.get_issue(
                parent_key,
                fields=["summary", "status", "description", "issuetype"],
            )

        width = 72
        line = "=" * width
        print()
        print(line)
        print(f"  TICKET: {ticket_key}")
        print(line)
        print(f"  Summary : {fields['summary']}")
        print(f"  Type    : {(fields.get('issuetype') or {}).get('name', '?')}")
        print(f"  Status  : {(fields.get('status') or {}).get('name', '?')}")
        assignee = (fields.get("assignee") or {}).get("displayName", "unassigned")
        print(f"  Assignee: {assignee}")
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

        attachments = fields.get("attachment") or []
        if attachments:
            print()
            print(f"  ATTACHMENTS ({len(attachments)}):")
            print("  " + "-" * (width - 2))
            for a in attachments:
                print(f"  {a.get('filename', '?')}  ({a.get('mimeType', '?')}, {a.get('size', 0)} bytes)")

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


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("Usage: python read_ticket.py <TICKET-KEY>")
        sys.exit(1)
    asyncio.run(read(sys.argv[1]))
