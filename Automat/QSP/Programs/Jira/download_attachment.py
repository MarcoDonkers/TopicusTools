"""
download_attachment.py — Download one or more attachments from a Jira issue.

Usage:
    python download_attachment.py <issue-key> [--name <filename-filter>] [--out <output-dir>]

Options:
    --name   Case-insensitive substring filter on filename (default: all attachments)
    --out    Directory to save files to (default: current directory)

Examples:
    python download_attachment.py QSP-94452
    python download_attachment.py QSP-94452 --name .sql
    python download_attachment.py QSP-94452 --name script --out C:/Downloads
"""

import argparse
import asyncio
import sys
from pathlib import Path
from jira_client import JiraClient


async def download(issue_key: str, name_filter: str | None, out_dir: Path) -> None:
    async with JiraClient() as client:
        await client.ensure_logged_in()

        result = await client._page_fetch(
            f"https://topicusfinance.atlassian.net/rest/api/3/issue/{issue_key}",
            {"fields": "attachment"},
        )
        attachments = result["fields"].get("attachment", [])

        if not attachments:
            print(f"[!] Geen bijlagen gevonden op {issue_key}.")
            return

        if name_filter:
            attachments = [
                a for a in attachments
                if name_filter.lower() in a["filename"].lower()
            ]

        if not attachments:
            print(f"[!] Geen bijlagen gevonden die overeenkomen met '{name_filter}'.")
            return

        out_dir.mkdir(parents=True, exist_ok=True)

        for attachment in attachments:
            filename = attachment["filename"]
            url = attachment["content"]
            print(f"[*] Downloaden: {filename} ...")

            resp = await client.context.request.get(url)
            body = await resp.body()

            out_path = out_dir / filename
            out_path.write_bytes(body)
            print(f"[+] Opgeslagen: {out_path}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Download Jira attachments.")
    parser.add_argument("issue_key", help="Jira issue key, e.g. QSP-94452")
    parser.add_argument("--name", default=None, help="Substring filter op bestandsnaam")
    parser.add_argument("--out", default=".", help="Output directory (default: .)")
    args = parser.parse_args()

    asyncio.run(download(args.issue_key, args.name, Path(args.out)))


if __name__ == "__main__":
    main()
