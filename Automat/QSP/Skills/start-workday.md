# Skill: Start the Workday

## Purpose

Present a prioritised Jira ticket overview at the beginning of the workday so
Marco can immediately see what to work on and what is available to pick up.

## When to invoke

- User says "start my workday", "what's on my plate?", "begin workday", or similar.
- User asks for today's Jira tickets or today's work overview.

---

## Steps

### 1. Run the workday script

```
cd QSP\Programs\Jira
python start_workday.py
```

If the session has expired, a browser window will open automatically.
Wait for the user to complete the login, then the script continues.

### 2. Parse and present the output

The script prints two sections:

| Section | Contents |
|---------|----------|
| **MY TICKETS** | Tickets assigned to Marco in the active sprint, excluding Done. Sorted: In Progress first, then Open/To Do. |
| **FREE TO PICK UP** | Unassigned Artemis tickets with status Open or To Do in the active sprint. |

Re-present the output to the user in a clean, readable format.
Group by section, include the ticket key as a clickable link if the interface supports it.

**Important:** Always list **every** ticket in the FREE TO PICK UP section — never truncate, summarise, or omit entries regardless of how many there are. The user needs the full list to decide what to pick up.

### 3. Ask what to do next

After presenting the overview, offer one of the following natural continuations:

- **"Start working on [key]"** — triggers Workflow 02 (Feature Development) for that ticket.
- **"Pick up [key]"** — invoke the `pickup-ticket` skill (`pickup_ticket.py <key>`), then trigger Workflow 01 (Technical Analysis) if it is a TA task, or Workflow 02 otherwise.
- **"Show details for [key]"** — fetch full ticket details from Jira and summarise them.
- **"Nothing yet"** — end the skill; no further action.

---

## Output contract

The script exits with code `0` on success.
On authentication timeout or Jira API error it exits with a non-zero code and prints an error.
If exit code is non-zero, report the error to the user and suggest running `python login.py` to refresh the session.

---

## Human checkpoints

This skill is **read-only**. No write actions are performed.
No human confirmation is required unless the user selects a follow-up action.

---

## Related

- Implementation: `QSP/Programs/Jira/start_workday.py`
- Session management: `QSP/Programs/Jira/jira_client.py`
- Login refresh: `QSP/Programs/Jira/login.py`
- Follow-up workflows: `QSP/Workflows/02-Feature-Development/`, `QSP/Workflows/03-Feature-Testing/`
