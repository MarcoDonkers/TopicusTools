"""
Search Confluence pages using CQL (Confluence Query Language).

Usage:
    python search_pages.py <space-key> [search-term]
    python search_pages.py QUION
    python search_pages.py QW "work in progress"

Without a search term, lists root-level pages in the space.
With a search term, searches page titles and content.
"""

import asyncio
import sys

from confluence_client import CONFLUENCE_BASE, ConfluenceClient


async def main(space_key: str, search_term: str | None) -> None:
    async with ConfluenceClient() as client:
        await client.ensure_logged_in()

        if search_term:
            cql = f'space = "{space_key}" AND title ~ "{search_term}" ORDER BY lastModified DESC'
            print(f'[*] Zoeken in {space_key} naar: "{search_term}"')
            results = await client.search_pages(cql, limit=25)

            print()
            if not results:
                print("  Geen resultaten gevonden.")
            else:
                print(f"  {len(results)} resultaten:\n")
                for page in results:
                    page_id = page.get("id", "")
                    title = page.get("title", "")
                    url = f"{CONFLUENCE_BASE}/wiki/spaces/{space_key}/pages/{page_id}"
                    print(f"  [{page_id}]  {title}")
                    print(f"           {url}")
                    print()
        else:
            print(f"[*] Pagina's ophalen uit space: {space_key}")
            pages = await client.get_space_pages(space_key, max_total=100)

            print()
            if not pages:
                print("  Geen pagina's gevonden.")
            else:
                print(f"  {len(pages)} pagina's in {space_key}:\n")
                for page in pages:
                    page_id = page.get("id", "")
                    title = page.get("title", "")
                    url = f"{CONFLUENCE_BASE}/wiki/spaces/{space_key}/pages/{page_id}"
                    print(f"  [{page_id}]  {title}")
                    print(f"           {url}")
                    print()


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Gebruik: python search_pages.py <space-key> [zoekterm]")
        print()
        print("Voorbeelden:")
        print("  python search_pages.py QUION")
        print('  python search_pages.py QW "work in progress"')
        sys.exit(1)

    space = sys.argv[1].upper()
    term = sys.argv[2] if len(sys.argv) > 2 else None
    asyncio.run(main(space, term))
