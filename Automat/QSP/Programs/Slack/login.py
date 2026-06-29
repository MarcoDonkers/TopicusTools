"""
login.py — one-time Slack login and credential extraction.

Opens a visible browser, waits for you to log in to Slack, then extracts
the internal session token (xoxc-) and d cookie (xoxd-) from the browser
and saves them to config.json (gitignored).

Run this once. Re-run whenever the session expires (usually weeks/months).

Usage:
    python login.py
"""
import asyncio
import json
from pathlib import Path

from playwright.async_api import async_playwright

SLACK_WS     = "https://topicusfinance.slack.com"
SESSION_DIR  = Path(__file__).parent / "session"
CONFIG_FILE  = Path(__file__).parent / "config.json"


async def extract_credentials(context) -> tuple[str | None, str | None]:
    """Extract xoxc token and xoxd cookie from a logged-in Slack browser context.
    Does NOT navigate — reads from whatever page is currently loaded.
    """
    page = context.pages[0] if context.pages else await context.new_page()

    token = await page.evaluate("""
        () => {
            try {
                const raw = localStorage.getItem('localConfig_v2');
                if (!raw) return null;
                const cfg  = JSON.parse(raw);
                const teams = Object.values(cfg.teams || {});
                return teams.length > 0 ? (teams[0].token || null) : null;
            } catch(e) { return null; }
        }
    """)

    cookies = await context.cookies(["https://slack.com", SLACK_WS])
    d_cookie = next((c["value"] for c in cookies if c["name"] == "d"), None)

    return token, d_cookie


async def main():
    SESSION_DIR.mkdir(parents=True, exist_ok=True)

    async with async_playwright() as p:
        print("[*] Launching browser...")
        context = await p.chromium.launch_persistent_context(
            user_data_dir=str(SESSION_DIR),
            headless=False,
            viewport={"width": 1280, "height": 900},
        )
        page = context.pages[0] if context.pages else await context.new_page()

        # Check if already logged in
        print("[*] Checking existing Slack session...")
        token, d_cookie = await extract_credentials(context)

        if token and d_cookie:
            _save(token, d_cookie)
            print("[+] Already logged in — credentials saved.")
            await context.close()
            return

        # Not logged in — ask human to log in
        print("[!] Not logged in. Please log in to Slack in the browser.")
        print("    (SSO, MFA, etc. all work — just complete the flow normally.)")
        print("    Waiting for login to complete...")
        print()

        await page.goto(SLACK_WS, wait_until="domcontentloaded", timeout=30_000)

        attempt = 0
        while True:
            await asyncio.sleep(10)
            attempt += 1
            token, d_cookie = await extract_credentials(context)
            if token and d_cookie:
                _save(token, d_cookie)
                print(f"[+] Login confirmed! Credentials extracted and saved to config.json.")
                break
            print(f"    [{attempt * 10}s] Still waiting for login...")

        await context.close()


def _save(token: str, cookie: str) -> None:
    CONFIG_FILE.write_text(json.dumps({"token": token, "cookie": cookie}, indent=2))


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\n[!] Cancelled.")
