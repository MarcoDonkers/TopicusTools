"""
update_story_description.py — Replace the description of a Jira story.

Usage:
    python update_story_description.py <TICKET-KEY> <python-module>

The python module must expose a function:
    def build_description() -> dict   # returns an ADF doc() node

Example:
    python update_story_description.py QSP-99202 descriptions.qsp99202_technisch

The module is imported at runtime; it may import from adf_builder freely.
"""

import asyncio
import importlib
import sys

sys.path.insert(0, str(__file__).replace("update_story_description.py", ""))
from jira_client import JiraClient


async def main(ticket: str, module_path: str) -> None:
    mod = importlib.import_module(module_path)
    description = mod.build_description()

    async with JiraClient() as jira:
        await jira.ensure_logged_in()
        await jira._api_call(
            "PUT",
            f"/rest/api/3/issue/{ticket}",
            {"fields": {"description": description}},
        )
        print(f"[+] {ticket} description updated.")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print("Usage: python update_story_description.py <TICKET> <module>")
        sys.exit(1)
    asyncio.run(main(sys.argv[1], sys.argv[2]))
