# Workflow 02 — Feature Development / Bug Fix

Develop a feature or fix a bug based on a Jira story.

## Trigger

A story in Jira is assigned to Marco Donkers and is ready for development.

## Steps (to be defined)

1. Fetch the story details from Jira.
2. Identify the relevant local repository and codebase area.
3. **[Human checkpoint]** Propose branch name and base branch; wait for confirmation.
4. Create the branch (after confirmation).
5. Implement the changes.
6. **[Human checkpoint]** Present a diff/summary of changes; wait for confirmation before pushing.
7. Push branch and create pull request.
8. **[Human checkpoint]** Confirm PR description and linked Jira story before submitting.

## Guardrails

- Branch naming must follow the defined convention (to be specified).
- No force-push or direct push to protected branches.
- Every `git push` and Jira status transition requires explicit human confirmation.
