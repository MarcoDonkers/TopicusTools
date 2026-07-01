"""
ConfluenceClient — browser-based Confluence access using Playwright.

Session strategy
----------------
A persistent Chromium profile is stored in session/ (gitignored).
On first run, or whenever the session has expired, a visible browser window
opens and the human completes the login (any SSO/MFA flow works).
On subsequent runs the saved profile is reused and the browser starts headlessly.

API strategy
------------
REST API calls are made by navigating a new browser tab directly to the API URL.
This is the only reliable way to carry HttpOnly session cookies set by Atlassian
SSO — scripted fetch() calls inside the page do not work because Atlassian's auth
cookies are SameSite/HttpOnly and are blocked for XHR.
"""

import asyncio
import json
import re
from pathlib import Path
from urllib.parse import urlencode

from playwright.async_api import async_playwright, BrowserContext, Playwright

CONFLUENCE_BASE = "https://topicus.atlassian.net"
SESSION_DIR = Path(__file__).parent / "session"

_LOGIN_POLL_INTERVAL_S = 5
_LOGIN_TIMEOUT_S = 300  # 5 minutes


class ConfluenceClient:
    def __init__(self):
        self._playwright: Playwright | None = None
        self.context: BrowserContext | None = None
        self.page = None

    async def __aenter__(self) -> "ConfluenceClient":
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
        """
        print("[*] Checking Confluence session...")
        await self.page.goto(
            f"{CONFLUENCE_BASE}/wiki", wait_until="domcontentloaded", timeout=30_000
        )

        if await self._is_authenticated():
            print("[+] Session valid.")
            return

        print()
        print("[!] Not logged in.")
        print("    A browser window will open. Please log in to Confluence")
        print("    (including any SSO or MFA steps).")
        print(f"    Waiting up to {_LOGIN_TIMEOUT_S // 60} minutes...")

        await self._shutdown()
        await self._launch(headless=False)
        await self.page.goto(
            f"{CONFLUENCE_BASE}/wiki", wait_until="domcontentloaded", timeout=30_000
        )

        elapsed = 0
        while elapsed < _LOGIN_TIMEOUT_S:
            await asyncio.sleep(_LOGIN_POLL_INTERVAL_S)
            elapsed += _LOGIN_POLL_INTERVAL_S
            if await self._is_authenticated():
                print("[+] Login confirmed — session saved for future runs.")
                return

        raise TimeoutError(
            f"Timed out waiting for Confluence login after {_LOGIN_TIMEOUT_S} seconds."
        )

    async def _is_authenticated(self) -> bool:
        """Return True if /wiki/rest/api/user/current responds with a valid account."""
        try:
            data = await self._page_fetch(
                f"{CONFLUENCE_BASE}/wiki/rest/api/user/current"
            )
            return bool(data.get("accountId"))
        except Exception:
            return False

    # ------------------------------------------------------------------
    # REST API — new-tab navigation approach (GET)
    # ------------------------------------------------------------------

    async def _page_fetch(self, url: str, params: dict | None = None) -> dict:
        """
        Open a new browser tab, navigate to a Confluence REST API URL, read
        the JSON body, close the tab and return the parsed data.
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
                    f"Confluence API {response.status} for {full_url}\n{body[:500]}"
                )
            content = await api_page.inner_text("body")
            return json.loads(content)
        finally:
            await api_page.close()

    def _extract_cursor(self, next_link: str) -> str | None:
        m = re.search(r"cursor=([^&]+)", next_link)
        return m.group(1) if m else None

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    async def get_page(self, page_id: str | int, body_format: str = "view") -> dict:
        """
        Fetch a page by ID including its body content.

        body_format options:
          "view"    — rendered HTML (most readable, recommended)
          "storage" — Confluence wiki storage XML
        """
        return await self._page_fetch(
            f"{CONFLUENCE_BASE}/wiki/api/v2/pages/{page_id}",
            params={"body-format": body_format},
        )

    async def get_page_children(self, page_id: str | int) -> list[dict]:
        """Return all direct child pages of a page."""
        all_children: list[dict] = []
        cursor: str | None = None

        while True:
            params: dict = {"limit": "50"}
            if cursor:
                params["cursor"] = cursor
            data = await self._page_fetch(
                f"{CONFLUENCE_BASE}/wiki/api/v2/pages/{page_id}/children",
                params=params,
            )
            results = data.get("results", [])
            all_children.extend(results)
            next_link = data.get("_links", {}).get("next")
            if not next_link or not results:
                break
            cursor = self._extract_cursor(next_link)
            if not cursor:
                break

        return all_children

    async def get_space_pages(
        self, space_key: str, max_total: int = 100
    ) -> list[dict]:
        """
        Return root-level pages in a space, identified by its key (e.g. "QUION").
        """
        # Resolve space key → space ID
        spaces_data = await self._page_fetch(
            f"{CONFLUENCE_BASE}/wiki/api/v2/spaces",
            params={"keys": space_key, "limit": "1"},
        )
        spaces = spaces_data.get("results", [])
        if not spaces:
            raise ValueError(f"Space with key '{space_key}' not found.")
        space_id = spaces[0]["id"]

        all_pages: list[dict] = []
        cursor: str | None = None

        while len(all_pages) < max_total:
            params: dict = {"limit": "50", "depth": "root"}
            if cursor:
                params["cursor"] = cursor
            data = await self._page_fetch(
                f"{CONFLUENCE_BASE}/wiki/api/v2/spaces/{space_id}/pages",
                params=params,
            )
            results = data.get("results", [])
            all_pages.extend(results)
            next_link = data.get("_links", {}).get("next")
            if not next_link or not results:
                break
            cursor = self._extract_cursor(next_link)
            if not cursor:
                break

        return all_pages[:max_total]

    async def search_pages(self, cql: str, limit: int = 25) -> list[dict]:
        """
        Search pages using CQL (Confluence Query Language).

        Example CQL queries:
          'space = QUION AND title ~ "Work In Progress"'
          'space = QW AND ancestor = 12345'
          'space = QUION AND label = "fps"'
        """
        data = await self._page_fetch(
            f"{CONFLUENCE_BASE}/wiki/rest/api/content/search",
            params={
                "cql": cql,
                "limit": str(limit),
                "expand": "space,version",
            },
        )
        return data.get("results", [])
