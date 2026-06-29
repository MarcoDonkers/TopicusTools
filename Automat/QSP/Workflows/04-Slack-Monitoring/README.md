# Workflow 04 — Slack Monitoring

Monitor Slack messages and inform the human when a response is warranted, proposing what to send.

## Trigger

Scheduled poll or event-driven notification from Slack.

## Steps (to be defined)

1. Read relevant Slack messages (channels and DMs to be configured).
2. Classify messages: requires response / informational / no action needed.
3. For messages that require a response: draft a proposed reply.
4. **[Human checkpoint]** Present the message and proposed reply; wait for approval, edit, or rejection before sending.
5. Send the approved message.

## Human Checkpoints

| Step | Reason |
|------|--------|
| Every outbound Slack message | Write action on external system; tone and content must be verified by human |

## Notes

- The agent never sends a Slack message autonomously.
- If Slack API access is unavailable, automated browser login (human-assisted) will be used as a fallback.
