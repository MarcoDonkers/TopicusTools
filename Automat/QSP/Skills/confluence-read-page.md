# Skill: Read Confluence Page

## Purpose

Fetch and display the content of a Confluence page, or list/search pages in a
Confluence space, so Marco can reference documentation during development or analysis.

## When to invoke

- User shares a Confluence URL or mentions a page by name.
- User asks "wat staat er op [pagina]?", "fetch confluence page", "open confluence".
- A workflow needs context from a Confluence document (e.g. WIP overview, spec page).

---

## Steps

### 1. Determine what to fetch

**From a URL** — extract the page ID (the number between `/pages/` and the title slug):

```
https://topicus.atlassian.net/wiki/spaces/QUION/pages/706863689/Work+In+Progress+FPS
                                                       ^^^^^^^^^  ← page ID
```

**From a space + search term** — use `search_pages.py` to find the page first.

---

### 2. Fetch a specific page

```
cd QSP\Programs\Confluence
python fetch_page.py <PAGE-ID>
```

The script prints:
- Title, page ID, space ID, version, URL
- Full page body as readable text
- List of child pages (if any)

---

### 3. List or search pages in a space

```
cd QSP\Programs\Confluence
python search_pages.py <SPACE-KEY>
python search_pages.py <SPACE-KEY> "<zoekterm>"
```

Examples:
```
python search_pages.py QUION
python search_pages.py QW "work in progress"
```

---

### 4. Present the output

Re-present the content in a clean, readable format:
- Page title as a clickable Confluence link
- Body content, preserving the structure (headers, lists, tables)
- If child pages were listed, summarise them briefly

---

## Known pages & spaces

| Item | Details |
|------|---------|
| Work In Progress FPS | ID `706863689` — space QUION |
| QUION space | `python search_pages.py QUION` |
| QW space | `python search_pages.py QW` |

---

## Output contract

Scripts exit with code `0` on success.
On authentication timeout or API error they exit non-zero and print an error message.
If exit code is non-zero, report the error and tell the user to re-run to trigger a new browser login.

---

## Human checkpoints

This skill is **read-only**. No confirmation required.

---

## Related

- Client: `QSP/Programs/Confluence/confluence_client.py`
- Scripts: `QSP/Programs/Confluence/fetch_page.py`, `search_pages.py`
- Session: `QSP/Programs/Confluence/session/`
- Companion skill: `QSP/Skills/start-workday.md` (Jira)
