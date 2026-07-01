"""
Fetch a Confluence page by ID and print its content as readable text.

Usage:
    python fetch_page.py <page-id>

The page ID is the number in the Confluence URL:
    https://topicus.atlassian.net/wiki/spaces/QUION/pages/706863689/...
                                                           ^^^^^^^^^
"""

import asyncio
import re
import sys
from html.parser import HTMLParser

from confluence_client import CONFLUENCE_BASE, ConfluenceClient


class _HtmlToText(HTMLParser):
    """Minimal HTML → plain-text converter for Confluence page bodies."""

    _BLOCK_TAGS = {"p", "br", "h1", "h2", "h3", "h4", "h5", "h6", "li", "tr", "div"}
    _SKIP_TAGS = {"script", "style"}

    def __init__(self):
        super().__init__()
        self._parts: list[str] = []
        self._skip_depth = 0

    def handle_starttag(self, tag, attrs):
        if tag in self._SKIP_TAGS:
            self._skip_depth += 1
        if tag in self._BLOCK_TAGS and not self._skip_depth:
            self._parts.append("\n")
        if tag == "th" and not self._skip_depth:
            self._parts.append("| ")

    def handle_endtag(self, tag):
        if tag in self._SKIP_TAGS:
            self._skip_depth = max(0, self._skip_depth - 1)
        if tag in {"h1", "h2", "h3"} and not self._skip_depth:
            self._parts.append("\n")

    def handle_data(self, data):
        if not self._skip_depth:
            self._parts.append(data)

    def get_text(self) -> str:
        raw = "".join(self._parts)
        # Collapse runs of blank lines to at most two
        return re.sub(r"\n{3,}", "\n\n", raw).strip()


def html_to_text(html: str) -> str:
    parser = _HtmlToText()
    parser.feed(html)
    return parser.get_text()


def page_url(page_id: str, space_key: str | None = None) -> str:
    if space_key:
        return f"{CONFLUENCE_BASE}/wiki/spaces/{space_key}/pages/{page_id}"
    return f"{CONFLUENCE_BASE}/wiki/pages/viewpage.action?pageId={page_id}"


async def main(page_id: str) -> None:
    async with ConfluenceClient() as client:
        await client.ensure_logged_in()

        print(f"[*] Fetching page {page_id}...")
        page = await client.get_page(page_id, body_format="view")

        title = page.get("title", "Unknown")
        space_id = page.get("spaceId", "")
        version_num = page.get("version", {}).get("number", "?")
        created_at = page.get("version", {}).get("createdAt", "")
        date_str = created_at[:10] if created_at else ""

        body_html = page.get("body", {}).get("view", {}).get("value", "")
        body_text = html_to_text(body_html) if body_html else "(geen inhoud)"

        url = page_url(page_id)

        print()
        print(f"{'=' * 60}")
        print(f"  {title}")
        print(f"{'=' * 60}")
        print(f"  Pagina-ID : {page_id}")
        print(f"  Space-ID  : {space_id}")
        print(f"  Versie    : {version_num}  ({date_str})")
        print(f"  URL       : {url}")
        print(f"{'=' * 60}")
        print()
        print(body_text)
        print()

        # Also list child pages if any
        children = await client.get_page_children(page_id)
        if children:
            print(f"--- Subpagina's ({len(children)}) ---")
            for child in children:
                child_id = child.get("id", "")
                child_title = child.get("title", "")
                print(f"  [{child_id}]  {child_title}")
            print()


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Gebruik: python fetch_page.py <page-id>")
        print()
        print("De page-id staat in de Confluence URL:")
        print("  https://topicus.atlassian.net/wiki/spaces/QUION/pages/706863689/...")
        print("                                                         ^^^^^^^^^")
        sys.exit(1)

    asyncio.run(main(sys.argv[1]))
