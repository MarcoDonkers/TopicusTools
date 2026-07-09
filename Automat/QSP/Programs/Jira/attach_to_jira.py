"""
attach_to_jira.py — Upload a local file as an attachment to a Jira issue.

Usage:
    python attach_to_jira.py <issue-key> <file-path>

Example:
    python attach_to_jira.py QSP-94452 path/to/script.sql
"""

import asyncio
import sys
from pathlib import Path
from jira_client import JiraClient


async def attach(issue_key: str, file_path: Path) -> None:
    content = file_path.read_text(encoding="utf-8")
    filename = file_path.name

    js = """async ([issueKey, filename, content]) => {
        const blob = new Blob([content], { type: "application/octet-stream" });
        const form = new FormData();
        form.append("file", blob, filename);
        const resp = await fetch(
            `https://topicusfinance.atlassian.net/rest/api/3/issue/${issueKey}/attachments`,
            {
                method: "POST",
                headers: { "X-Atlassian-Token": "no-check", "Accept": "application/json" },
                credentials: "include",
                body: form
            }
        );
        return { status: resp.status, body: await resp.text() };
    }"""

    async with JiraClient() as client:
        await client.ensure_logged_in()
        await client.page.goto(
            "https://topicusfinance.atlassian.net",
            wait_until="domcontentloaded",
            timeout=30_000,
        )
        result = await client.page.evaluate(js, [issue_key, filename, content])

    if result["status"] >= 400:
        print(f"[!] Fout {result['status']}: {result['body'][:500]}")
        sys.exit(1)
    else:
        print(f"[+] Bijlage toegevoegd aan {issue_key}: {filename}")


def main() -> None:
    if len(sys.argv) != 3:
        print("Gebruik: python attach_to_jira.py <issue-key> <file-path>")
        sys.exit(1)

    issue_key = sys.argv[1]
    file_path = Path(sys.argv[2])

    if not file_path.exists():
        print(f"[!] Bestand niet gevonden: {file_path}")
        sys.exit(1)

    asyncio.run(attach(issue_key, file_path))


if __name__ == "__main__":
    main()
