# Workflow 03 — Feature Testing (Developer Smoke Test)

After development, reset an environment, deploy the feature, and run smoke tests to verify the changes work as expected.

> **Scope:** This is a developer self-check, not a QA acceptance test. A separate tester will perform formal testing. The goal here is to catch obvious regressions or integration issues before handoff.

## Trigger

Feature development (Workflow 02) is complete and a deployable build is available.

## Steps (to be defined)

1. Reset the target test environment to a clean state.
2. Deploy the build containing the feature.
3. Run automated smoke tests relevant to the changed functionality.
4. **[Human checkpoint — optional]** If the agent cannot determine how to test the feature, ask for guidance.
5. Collect evidence (logs, screenshots, test results) — stored internally only.
6. Report pass/fail summary to the developer.

## Human Checkpoints

| Step | Reason |
|------|--------|
| Test guidance | Agent cannot determine how to validate the feature |

## Evidence Policy

All collected evidence (logs, screenshots, test output) is stored locally and never published to external systems without explicit instruction.
