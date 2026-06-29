"""
JiraClient — browser-based Jira access using Playwright.

Session strategy
----------------
A persistent Chromium profile is stored in session/ (gitignored).
On first run, or whenever the session has expired, a visible browser window
opens and the human completes the login (any SSO/MFA flow works — we never
inspect the URL, we only confirm authentication by calling /myself).
On subsequent runs the saved profile is reused and the browser starts headlessly.

API strategy
------------
REST API calls are made by navigating a new browser tab directly to the
API URL. This is the only reliable way to carry HttpOnly session cookies set
by Atlassian SSO — scripted fetch() calls inside the page do not work because
Atlassian's auth cookies are SameSite/HttpOnly and are blocked for XHR.
"""

import asyncio
import json
from pathlib import Path
from urllib.parse import urlencode

from playwright.async_api import async_playwright, BrowserContext, Playwright

JIRA_BASE = "https://topicusfinance.atlassian.net"
SESSION_DIR = Path(__file__).parent / "session"

_LOGIN_POLL_INTERVAL_S = 5
_LOGIN_TIMEOUT_S = 300  # 5 minutes


class JiraClient:
    def __init__(self):
        self._playwright: Playwright | None = None
        self.context: BrowserContext | None = None
        self.page = None

    async def __aenter__(self) -> "JiraClient":
        SESSION_DIR.mkdir(parents=True, exist_ok=True)
        await self._launch(headless=True)
        return self

    async def __aexit__(self, *_args) -> None:
        await self._shutdown()

    # ------------------------------------------------------------------
    # Internal launch / shutdown
    # ------------------------------------------------------------------

    async def _launch(self, headless: bool) -> None:
        if self._playwright is None:
            self._playwright = await async_playwright().start()
        self.context = await self._playwright.chromium.launch_persistent_context(
            user_data_dir=str(SESSION_DIR),
            headless=headless,
            viewport={"width": 1280, "height": 900},
            args=["--disable-blink-features=AutomationControlled"],
        )
        self.page = (
            self.context.pages[0] if self.context.pages else await self.context.new_page()
        )

    async def _shutdown(self) -> None:
        if self.context:
            await self.context.close()
            self.context = None
        if self._playwright:
            await self._playwright.stop()
            self._playwright = None

    # ------------------------------------------------------------------
    # Auth
    # ------------------------------------------------------------------

    async def ensure_logged_in(self) -> None:
        """
        Confirm the session is valid. If not, re-launch visibly and wait
        for the human to complete the full login/SSO/MFA flow.
        Authentication is detected by polling /rest/api/3/myself — no URL
        pattern matching, so any SSO provider is supported.
        """
        print("[*] Checking Jira session...")

        # Navigate to Jira base so cookies are in scope for the domain
        await self.page.goto(JIRA_BASE, wait_until="domcontentloaded", timeout=30_000)

        if await self._is_authenticated():
            print("[+] Session valid.")
            return

        # Session missing or expired — re-launch with a visible window
        print()
        print("[!] Not logged in.")
        print("    A browser window will open. Please log in to Jira")
        print("    (including any SSO or MFA steps).")
        print(f"    Waiting up to {_LOGIN_TIMEOUT_S // 60} minutes...")

        await self._shutdown()
        await self._launch(headless=False)

        # Navigate to the login entry point
        await self.page.goto(
            f"{JIRA_BASE}/login", wait_until="domcontentloaded", timeout=30_000
        )

        # Poll /myself until authenticated or timeout
        elapsed = 0
        while elapsed < _LOGIN_TIMEOUT_S:
            await asyncio.sleep(_LOGIN_POLL_INTERVAL_S)
            elapsed += _LOGIN_POLL_INTERVAL_S
            if await self._is_authenticated():
                print("[+] Login confirmed — session saved for future runs.")
                return

        raise TimeoutError(
            f"Timed out waiting for Jira login after {_LOGIN_TIMEOUT_S} seconds."
        )

    async def _is_authenticated(self) -> bool:
        """Return True if /rest/api/3/myself responds with a valid account."""
        try:
            data = await self._page_fetch(f"{JIRA_BASE}/rest/api/3/myself")
            return bool(data.get("accountId"))
        except Exception:
            return False

    # ------------------------------------------------------------------
    # REST API — new-tab navigation approach
    # ------------------------------------------------------------------

    async def _page_fetch(self, url: str, params: dict | None = None) -> dict:
        """
        Open a new browser tab, navigate to a Jira REST API URL, read the
        JSON body, close the tab and return the parsed data.
        """
        full_url = f"{url}?{urlencode(params)}" if params else url
        api_page = await self.context.new_page()
        try:
            response = await api_page.goto(
                full_url, wait_until="domcontentloaded", timeout=20_000
            )
            if response and response.status >= 400:
                body = await api_page.inner_text("body")
                raise RuntimeError(
                    f"Jira API {response.status} for {full_url}\n{body[:500]}"
                )
            content = await api_page.inner_text("body")
            return json.loads(content)
        finally:
            await api_page.close()

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    async def search_issues(
        self,
        jql: str,
        fields: list[str] | None = None,
        max_total: int = 500,
    ) -> list[dict]:
        """
        Search for Jira issues using JQL, paginating automatically.

        Args:
            jql:       JQL query string.
            fields:    Issue fields to return.
            max_total: Safety cap on total issues fetched.
        """
        if fields is None:
            fields = ["summary", "status", "assignee", "priority", "issuetype", "parent"]

        all_issues: list[dict] = []
        next_page_token: str | None = None
        page_size = 50

        while len(all_issues) < max_total:
            params: dict = {
                "jql": jql,
                "fields": ",".join(fields),
                "maxResults": str(page_size),
            }
            if next_page_token:
                params["nextPageToken"] = next_page_token

            data = await self._page_fetch(
                f"{JIRA_BASE}/rest/api/3/search/jql",
                params=params,
            )

            batch = data.get("issues", [])
            all_issues.extend(batch)

            next_page_token = data.get("nextPageToken")
            if not next_page_token or not batch:
                break

        return all_issues

    async def get_issue(self, issue_key: str, fields: list[str] | None = None) -> dict:
        """Fetch a single issue by key."""
        params = {"fields": ",".join(fields)} if fields else None
        return await self._page_fetch(
            f"{JIRA_BASE}/rest/api/3/issue/{issue_key}",
            params=params,
        )
