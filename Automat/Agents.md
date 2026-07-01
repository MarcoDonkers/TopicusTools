# Automat — Agent Automation Project

## Vision

The goal of this project is to automate all steps of work across roles and responsibilities.
Human involvement is kept to the minimum necessary: the human is only consulted when a decision cannot be made autonomously, a write action must be guarded, or ambiguous information requires confirmation.

---

## Principles

- **Human-in-the-loop only where required.** Automate everything that can be automated. Present the human with a clear, concise choice when their input is genuinely needed.
- **Write actions are strictly guarded.** Any operation that modifies an external system (Jira, Git, Slack, etc.) must pass through guardrails and require explicit human confirmation unless the risk is negligible.
- **Evidence is internal.** Artifacts collected during automated steps (logs, screenshots, test results) remain local unless explicitly published.
- **Transparency.** Every automated decision and action is logged so the human can review what was done and why.

---

## Repository Structure

```
Automat/
├── Agents.md          ← this file
└── QSP/               ← automation for the QSP position
    ├── Skills/        ← reusable skills shared across workflows
    └── Workflows/
        ├── 01-Technical-Analysis/
        ├── 02-Feature-Development/
        ├── 03-Feature-Testing/
        ├── 04-Slack-Monitoring/
        └── 05-Epic-Automated-Tests/
```

---

## Roles in Scope

| Role | Folder   | Status      |
|------|----------|-------------|
| QSP  | `QSP/`   | In progress |

---

## Local Codebases

| Codebase    | Path                              | Notes                                          |
|-------------|-----------------------------------|------------------------------------------------|
| QSP.Core    | `C:\WorkEnvironment\QSP.Core\`    | Main QSP application; solution file: `FinGen.QSP.sln`. Contains `Force.Financieel`, `FORCE.Provisie`, `FinGen.Documentgeneratie`, `Database/` etc. |

---

## External Integrations

| System  | Access Method                              | Notes                                          |
|---------|--------------------------------------------|------------------------------------------------|
| Jira    | MCP / REST API                             | Read stories assigned to Marco Donkers         |
| Git     | Local disk + Git CLI                       | Repositories available locally                 |
| Slack   | MCP / API or automated browser with human login | Read messages; propose replies for approval |
| Browser | Automated browsing (human login if needed) | Fallback for systems without API access        |

---

## Human Checkpoints (global)

These are the categories of decision that always require human confirmation, regardless of workflow:

1. **Write to external system** — any create/update/delete on Jira, Git remote, Slack, or similar.
2. **Ambiguous story / requirement** — when the agent cannot determine intent with sufficient confidence.
3. **Branching strategy** — branch names and base branch selection before any `git push`.
4. **Test guidance** — when automated testing cannot determine how to validate a feature.
5. **Security / credential handling** — any action involving secrets, tokens, or personal data.
