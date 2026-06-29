# Programs / Jira

Browser-based Jira access using **Python + Playwright**.

## How it works

- A persistent browser profile is stored in `session/` (gitignored).  
- **First run:** a visible browser window opens. Log in normally (SSO, MFA, etc.). The session is saved automatically.  
- **Subsequent runs:** the saved session is reused — no login required unless it expires.  
- Once authenticated, Jira's REST API is called *through* the browser context, so the session cookies are sent automatically. No separate API token is needed.

## Setup

```bash
pip install -r requirements.txt
playwright install chromium
```

## Scripts

| Script | Purpose |
|--------|---------|
| `fetch_ta_tasks.py` | Fetch open Technical Analysis tasks (free to pick up or in-progress for current user) |

## Running

```bash
python fetch_ta_tasks.py
```

## Files

| File | Description |
|------|-------------|
| `jira_client.py` | `JiraClient` class — session management, login detection, REST API wrapper |
| `fetch_ta_tasks.py` | Fetch & display TA tasks |
| `session/` | Persistent browser profile (gitignored — contains personal login data) |
