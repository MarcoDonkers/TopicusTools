# Skill: Pick Up a Ticket

## Purpose

Assign a Jira ticket to Marco and set it to **In Progress**, then display the
full ticket and parent context so work can begin immediately.

## When to invoke

- User says "pick up [key]", "pak [key] op", or selects a ticket from the
  workday overview.

---

## Steps

### 1. Run the pickup script

```
cd QSP\Programs\Jira
python pickup_ticket.py <TICKET-KEY>
```

The script:
1. Assigns the ticket to the current user.
2. Transitions the ticket to **In Progress** (skips if already there).
3. Prints the full ticket summary, description, and parent details.

### 2. Present the output

Re-present the output to the user in a clean, readable format:
- Ticket key (as a clickable link), summary, type, status
- Parent ticket key + summary (as a clickable link) if present
- Parent description (for context on the broader story)
- Ticket description

### 3. Determine next workflow

Based on the ticket type, offer the appropriate follow-up:

| Ticket type / summary contains | Next workflow |
|---------------------------------|---------------|
| "Technische analyse" / "Impactanalyse" / "Risico en dreigingsanalyse" | Workflow 01 — Technical Analysis |
| Feature / Story / other Task | Workflow 02 — Feature Development |
| Test / ATF | Workflow 03 — Feature Testing |

Ask the user to confirm before starting the next workflow.

---

## Output contract

The script exits with code `0` on success.
On authentication timeout or Jira API error it exits with a non-zero code and
prints an error. If exit code is non-zero, report the error to the user and
suggest running `python login.py` to refresh the session.

---

## Human checkpoints

- Confirm ticket key before running (the assignment + transition cannot be undone
  without manually reverting in Jira).
- Ask the user which workflow to start after presenting the ticket details.

---

## Related

- Implementation: `QSP/Programs/Jira/pickup_ticket.py`
- Session management: `QSP/Programs/Jira/jira_client.py`
- Login refresh: `QSP/Programs/Jira/login.py`
- Triggered from: `QSP/Skills/start-workday.md`
- Follow-up: Workflow 01 / 02 / 03 (not yet created)
