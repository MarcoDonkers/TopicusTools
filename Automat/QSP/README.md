# QSP Automation

This folder contains all automation for the QSP position.

## Structure

```
QSP/
├── Skills/        ← reusable skills shared across workflows
└── Workflows/
    ├── 01-Technical-Analysis/    ← Analyse Jira stories assigned to Marco Donkers
    ├── 02-Feature-Development/   ← Develop features / fix bugs based on a story
    ├── 03-Feature-Testing/       ← Reset env, deploy, smoke-test own changes
    ├── 04-Slack-Monitoring/      ← Monitor Slack and propose replies
    └── 05-Epic-Automated-Tests/  ← Run automated test suite for an Epic
```

See `../Agents.md` for global principles and human-checkpoint rules.
