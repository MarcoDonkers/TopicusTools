# Programs / Confluence

Browser-based Confluence access using **Python + Playwright**.

## How it works

- A persistent browser profile is stored in `session/` (gitignored).
- **First run:** a visible browser window opens. Log in normally (SSO, MFA, etc.). The session is saved automatically.
- **Subsequent runs:** the saved session is reused — no login required unless it expires.
- Once authenticated, Confluence's REST API is called *through* the browser context, so the session cookies are sent automatically. No separate API token is needed.

> Note: Confluence is on `topicus.atlassian.net`, which is a different Atlassian instance than Jira (`topicusfinance.atlassian.net`). Each has its own session folder.

## Setup

```bash
pip install -r requirements.txt
playwright install chromium
```

## Scripts

| Script | Purpose |
|--------|---------|
| `fetch_page.py <page-id>` | Fetch a Confluence page by ID and display its content |
| `search_pages.py <space-key> [term]` | List pages in a space, or search by title |

## Running

```bash
# Fetch the Work In Progress FPS page
python fetch_page.py 706863689

# List all pages in the QUION space
python search_pages.py QUION

# Search for a specific page
python search_pages.py QW "work in progress"
```

## Known spaces

| Space key | Description |
|-----------|-------------|
| `QUION` | Quion / FPS team space |
| `QW` | QW space |

## Files

| File | Description |
|------|-------------|
| `confluence_client.py` | `ConfluenceClient` class — session management, login detection, REST API wrapper |
| `fetch_page.py` | Fetch a page by ID and display its body content |
| `search_pages.py` | List or search pages in a Confluence space |
| `session/` | Persistent browser profile (gitignored — contains personal login data) |
