"""
login.py — one-time login helper.

Run this script once to establish and save a Jira session.
The browser stays open until you are fully logged in (or you close it).
On success the session is saved to session/ and all other scripts will
reuse it without prompting again.

Usage:
    python login.py
"""
import asyncio
import json
from pathlib import Path
from urllib.parse import urlencode

from playwright.async_api import async_playwright

JIRA_BASE = "https://topicusfinance.atlassian.net"
SESSION_DIR = Path(__file__).parent / "session"


async def check_auth(context) -> dict | None:
    """Open /myself in a new tab and return the user dict, or None if not authed."""
    page = await context.new_page()
    try:
        resp = await page.goto(
            f"{JIRA_BASE}/rest/api/3/myself",
            wait_until="domcontentloaded",
            timeout=10_000,
        )
        if resp and resp.status == 200:
            content = await page.inner_text("body")
            data = json.loads(content)
            if data.get("accountId"):
                return data
        return None
    except Exception:
        return None
    finally:
        await page.close()


async def main():
    SESSION_DIR.mkdir(parents=True, exist_ok=True)

    async with async_playwright() as p:
        print("[*] Launching browser...")
        context = await p.chromium.launch_persistent_context(
            user_data_dir=str(SESSION_DIR),
            headless=False,
            viewport={"width": 1280, "height": 900},
            args=["--disable-blink-features=AutomationControlled"],
        )
        page = context.pages[0] if context.pages else await context.new_page()

        # Check if already logged in
        print("[*] Checking existing session...")
        user = await check_auth(context)
        if user:
            print(f"[+] Already logged in as: {user.get('displayName')} ({user.get('emailAddress')})")
            await context.close()
            return

        # Not logged in — navigate to Jira and wait
        print("[!] Not logged in. Navigating to Jira login...")
        await page.goto(f"{JIRA_BASE}/login", wait_until="domcontentloaded", timeout=30_000)
        print()
        print("    Please log in to Jira in the browser (SSO, MFA, etc.).")
        print("    This script will detect login automatically.")
        print("    Press Ctrl+C to cancel.")
        print()

        attempt = 0
        while True:
            await asyncio.sleep(5)
            attempt += 1
            user = await check_auth(context)
            if user:
                print(f"[+] Login confirmed! Logged in as: {user.get('displayName')} ({user.get('emailAddress')})")
                print("[+] Session saved to session/ — other scripts will reuse this session.")
                break
            print(f"    [{attempt * 5}s] Still waiting for login...")

        await context.close()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\n[!] Cancelled.")
