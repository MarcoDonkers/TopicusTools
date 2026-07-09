# Skill: Jira Attachments

## Purpose

Upload files to a Jira issue as attachments, or download existing attachments
from a Jira issue. Use this when a script, document, or other artifact needs
to be stored on a ticket.

## When to invoke

- A SQL script or other file needs to be attached to a Jira issue.
- An existing attachment (e.g. a SQL script) needs to be retrieved for review
  or modification.
- Triggered automatically at the end of Workflow 01 (Technical Analysis) when
  the analysis includes a SQL script.

---

## Programs

| Script | Location |
|--------|----------|
| Upload attachment | `QSP/Programs/Jira/attach_to_jira.py` |
| Download attachment(s) | `QSP/Programs/Jira/download_attachment.py` |

---

## Steps

### Upload a file

```
cd QSP\Programs\Jira
python attach_to_jira.py <issue-key> <file-path>
```

**Example:**
```
python attach_to_jira.py QSP-94452 path\to\script.sql
```

The script uploads the file using `multipart/form-data` via the browser session
(carries the Atlassian SSO cookies). No separate authentication step needed if
the session is still valid.

### Download attachments

```
cd QSP\Programs\Jira
python download_attachment.py <issue-key> [--name <filter>] [--out <dir>]
```

**Examples:**
```
# Download all attachments
python download_attachment.py QSP-94452

# Download only .sql files
python download_attachment.py QSP-94452 --name .sql

# Download to a specific folder
python download_attachment.py QSP-94452 --name script --out C:\Temp
```

---

## Output contract

Both scripts exit with code `0` on success and a non-zero code on error.
Error details are printed to stdout.

---

## Human checkpoints

- **Upload:** Always present the file content to the user and wait for explicit
  approval before uploading. Never upload without confirmation.
- **Download:** Read-only — no confirmation required.

---

## Related

- Workflow: `QSP/Skills/workflow-01-ta.md` (SQL scripts attached after TA)
- Session management: `QSP/Programs/Jira/jira_client.py`
