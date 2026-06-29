"""
slack_overview.py — Overview of active Slack conversations for QSP/Artemis.

Shows recent messages from:
  - Configured team channels (artemis, artemis-devs, qsp-artemis, qsp)
  - All active DMs (private messages with recent activity)

Usage:
    python slack_overview.py
"""
import asyncio
from datetime import datetime, timezone

from slack_client import SlackClient

# Channels to always include
WATCH_CHANNELS = ["artemis", "artemis-devs", "qsp-artemis", "qsp"]

# Only show DMs with a message in the last N days
DM_ACTIVE_DAYS = 7

# Messages per channel/DM to fetch
MSG_LIMIT = 20


def ts_to_str(ts: str) -> str:
    """Convert Slack timestamp to human-readable string."""
    try:
        dt = datetime.fromtimestamp(float(ts), tz=timezone.utc).astimezone()
        return dt.strftime("%d-%m %H:%M")
    except Exception:
        return ts


def is_recent(ts: str, days: int) -> bool:
    try:
        import time
        age = time.time() - float(ts)
        return age < days * 86400
    except Exception:
        return False


async def print_channel_section(
    client: SlackClient,
    label: str,
    channel_id: str,
    limit: int = MSG_LIMIT,
) -> None:
    messages = await client.get_history(channel_id, limit=limit)
    # Filter out subtypes (joins, topic changes, etc.)
    messages = [m for m in messages if not m.get("subtype")]

    print(f"\n  {'=' * 70}")
    print(f"  # {label}  ({len(messages)} recent messages)")
    print(f"  {'=' * 70}")

    if not messages:
        print("  (no recent messages)")
        return

    for msg in reversed(messages):  # oldest first
        user = await client.resolve_user(msg.get("user", "?"))
        ts   = ts_to_str(msg.get("ts", ""))
        text = msg.get("text", "").replace("\n", " ")[:120]
        print(f"  [{ts}] {user}: {text}")


async def main():
    client = SlackClient()
    my_user_id = await client.get_my_user_id()
    print(f"[*] Logged in as user: {my_user_id}")

    # ------------------------------------------------------------------
    # Team channels
    # ------------------------------------------------------------------
    print("\n[*] Looking up channel IDs...")
    channel_map = await client.find_channels(WATCH_CHANNELS)
    missing = [n for n in WATCH_CHANNELS if n not in channel_map]
    if missing:
        print(f"[!] Channels not found (not a member or wrong name): {missing}")

    print("\n" + "=" * 72)
    print("  TEAM CHANNELS")
    print("=" * 72)

    for name in WATCH_CHANNELS:
        ch_id = channel_map.get(name)
        if ch_id:
            await print_channel_section(client, f"#{name}", ch_id)
        else:
            print(f"\n  [SKIP] #{name} — channel not found")

    # ------------------------------------------------------------------
    # Active DMs
    # ------------------------------------------------------------------
    print("\n\n" + "=" * 72)
    print("  DIRECT MESSAGES  (active in last 7 days)")
    print("=" * 72)

    dms = await client.get_open_dms()
    active_dms = []
    for dm in dms:
        dm_id = dm["id"]
        # latest message timestamp is in the channel object
        latest_ts = dm.get("latest", {}).get("ts") if isinstance(dm.get("latest"), dict) else dm.get("latest")
        if latest_ts and is_recent(str(latest_ts), DM_ACTIVE_DAYS):
            active_dms.append(dm)

    if not active_dms:
        print("\n  (no active DMs in the last 7 days)")
    else:
        for dm in active_dms:
            other_user_id = dm.get("user", "?")
            if other_user_id == my_user_id:
                label = "You (self)"
            else:
                label = await client.resolve_user(other_user_id)
            await print_channel_section(client, f"DM: {label}", dm["id"])

    print("\n")


if __name__ == "__main__":
    asyncio.run(main())
