"""
slack_client.py — Slack API client using browser-extracted session credentials.

Credentials are loaded from config.json (gitignored), which is written by
login.py on first run. The xoxc token + d cookie are the same credentials
the Slack web app uses internally, so no OAuth app or API key is needed.
"""

import asyncio
import json
import time
from pathlib import Path

import httpx

SLACK_BASE    = "https://slack.com/api"
SLACK_WS      = "https://topicusfinance.slack.com"
CONFIG_FILE   = Path(__file__).parent / "config.json"
CHANNEL_CACHE = Path(__file__).parent / "channel_cache.json"

# How many messages to fetch per channel by default
DEFAULT_MSG_LIMIT = 30

# Refresh channel cache if older than this (seconds)
CACHE_MAX_AGE_S = 86_400  # 24 hours


class SlackClient:
    def __init__(self):
        if not CONFIG_FILE.exists():
            raise FileNotFoundError(
                "config.json not found. Run login.py first to save your Slack session."
            )
        cfg = json.loads(CONFIG_FILE.read_text())
        self._token  = cfg["token"]   # xoxc-...
        self._cookie = cfg["cookie"]  # xoxd-...
        self._user_cache: dict[str, str] = {}

    # ------------------------------------------------------------------
    # Core HTTP
    # ------------------------------------------------------------------

    async def _get(self, method: str, params: dict | None = None, _retries: int = 3) -> dict:
        headers = {
            "Authorization": f"Bearer {self._token}",
            "Cookie": f"d={self._cookie}",
        }
        for attempt in range(_retries):
            async with httpx.AsyncClient(timeout=15) as client:
                r = await client.get(
                    f"{SLACK_BASE}/{method}",
                    headers=headers,
                    params=params or {},
                )
            if r.status_code == 429:
                retry_after = int(r.headers.get("Retry-After", 10))
                print(f"    [rate limit] {method} — waiting {retry_after}s...")
                await asyncio.sleep(retry_after)
                continue
            r.raise_for_status()
            data = r.json()
            if not data.get("ok"):
                raise RuntimeError(f"Slack API error [{method}]: {data.get('error')}")
            return data
        raise RuntimeError(f"Slack API {method} kept returning 429 after {_retries} retries.")

    # ------------------------------------------------------------------
    # Users
    # ------------------------------------------------------------------

    async def resolve_user(self, user_id: str) -> str:
        """Return display name for a user ID, cached."""
        if user_id in self._user_cache:
            return self._user_cache[user_id]
        try:
            data = await self._get("users.info", {"user": user_id})
            u    = data.get("user", {})
            name = (
                u.get("profile", {}).get("display_name")
                or u.get("real_name")
                or user_id
            )
        except Exception:
            name = user_id
        self._user_cache[user_id] = name
        return name

    async def get_my_user_id(self) -> str:
        data = await self._get("auth.test")
        return data["user_id"]

    # ------------------------------------------------------------------
    # Channels
    # ------------------------------------------------------------------

    async def find_channels(self, names: list[str]) -> dict[str, str]:
        """
        Return {channel_name: channel_id} for the given channel names.
        Results are cached to channel_cache.json for 24 hours so the
        expensive full-workspace scan only happens once.
        """
        targets = {n.lstrip("#").lower() for n in names}

        # Load cache if fresh enough
        cache = self._load_channel_cache()
        result = {n: cache[n] for n in targets if n in cache}
        missing = targets - result.keys()

        if not missing:
            return result

        # Cache miss — scan channels until all found or exhausted
        print(f"    [channels] Scanning workspace for: {', '.join(missing)} (caching result)...")
        cursor = ""
        while missing:
            params: dict = {
                "limit": 200,
                "types": "public_channel,private_channel",
                "exclude_archived": "true",
            }
            if cursor:
                params["cursor"] = cursor

            data = await self._get("conversations.list", params)
            for ch in data.get("channels", []):
                name = ch.get("name", "").lower()
                cache[name] = ch["id"]          # populate full cache
                if name in missing:
                    result[name] = ch["id"]
                    missing.discard(name)

            cursor = data.get("response_metadata", {}).get("next_cursor", "")
            if not cursor:
                break
            await asyncio.sleep(1)

        self._save_channel_cache(cache)
        if missing:
            print(f"    [channels] Not found (not a member?): {missing}")
        return result

    def _load_channel_cache(self) -> dict[str, str]:
        if CHANNEL_CACHE.exists():
            raw = json.loads(CHANNEL_CACHE.read_text())
            if time.time() - raw.get("_ts", 0) < CACHE_MAX_AGE_S:
                return {k: v for k, v in raw.items() if not k.startswith("_")}
        return {}

    def _save_channel_cache(self, cache: dict[str, str]) -> None:
        CHANNEL_CACHE.write_text(json.dumps({**cache, "_ts": time.time()}, indent=2))

    async def get_open_dms(self) -> list[dict]:
        """Return all open IM (direct message) conversations."""
        data = await self._get(
            "conversations.list",
            {"types": "im", "limit": 200, "exclude_archived": "true"},
        )
        return data.get("channels", [])

    # ------------------------------------------------------------------
    # Messages
    # ------------------------------------------------------------------

    async def get_history(
        self,
        channel_id: str,
        limit: int = DEFAULT_MSG_LIMIT,
    ) -> list[dict]:
        """Return recent messages for a channel or DM."""
        data = await self._get(
            "conversations.history",
            {"channel": channel_id, "limit": limit},
        )
        return data.get("messages", [])
