# Workflow 01 — Technical Analysis

Automatically fetch and perform technical analysis for Jira stories.

## Jira Structure

- **Parent story:** `Technische analyse van QSP-XXXXX` (or similar analysis story)
- **Sub-task:** `Analyse` — this is the actual work item that gets assigned
- **Free to pick up:** sub-task status = `Open`, unassigned, in active sprint
- **In progress:** sub-task status = `In Progress`, assigned to current user

## Programs

| Script | Location |
|--------|----------|
| Fetch open TA tasks | `QSP/Programs/Jira/fetch_ta_tasks.py` |
| One-time login | `QSP/Programs/Jira/login.py` |

## Steps (to be defined)

1. Run `fetch_ta_tasks.py` to list open TA tasks in the active sprint.
2. Select a task to work on.
3. Traverse the relevant local repository to understand the affected code area.
4. Produce a technical analysis document.
5. **[Human checkpoint]** Review and confirm the analysis before writing it back to Jira.

## Human Checkpoints

| Step | Reason |
|------|--------|
| Write analysis to Jira | Write action on external system |
| Ambiguous requirements | Agent cannot determine intent with confidence |
