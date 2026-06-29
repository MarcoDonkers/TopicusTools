# Programs / Slack

Browser-based Slack access using **Python + Playwright + httpx**.

## How it works

1. `login.py` opens a visible browser and waits for you to log in to Slack.
2. After login it extracts the internal `xoxc-` token and `xoxd-` session cookie from the browser and saves them to `config.json` (gitignored).
3. All subsequent scripts use `slack_client.py` which calls the Slack Web API directly via `httpx` with those credentials — no OAuth app or API token required.

## Setup

```bash
pip install -r requirements.txt
playwright install chromium
```

## First-time login

```bash
python login.py
```

Re-run `login.py` whenever the session expires (typically weeks to months).

## Scripts

| Script | Purpose |
|--------|---------|
| `login.py` | Extract and save Slack session credentials |
| `slack_overview.py` | Show recent messages from team channels + active DMs |

## Watched channels

`artemis`, `artemis-devs`, `qsp-artemis`, `qsp`

## Files

| File | Description |
|------|-------------|
| `slack_client.py` | `SlackClient` — API wrapper using saved credentials |
| `login.py` | One-time credential extraction |
| `slack_overview.py` | Channel + DM overview |
| `config.json` | Saved credentials (gitignored) |
| `session/` | Persistent browser profile for login (gitignored) |
