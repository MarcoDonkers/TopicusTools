# Workflow 05 — Automated Tests on an Epic

Run the full automated test suite for a given Epic and report results.

## Trigger

An Epic in Jira is ready for automated test execution (e.g., sprint end, release gate).

## Steps (to be defined)

1. Identify the Epic and its associated stories/features.
2. Determine the relevant test suites and environments.
3. Trigger the automated test run.
4. Monitor execution and collect results.
5. Produce a test report summarising pass/fail per story.
6. **[Human checkpoint]** Present the report; flag failures that require human decision.

## Human Checkpoints

| Step | Reason |
|------|--------|
| Failures requiring action | Agent cannot decide whether to block or proceed |
| Environment selection | Incorrect environment could affect production |
