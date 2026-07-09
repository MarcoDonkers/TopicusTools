# Workflow 01: Technical Analysis (TA)

## Purpose

Guide Marco through a complete Technical Analysis for a Jira TA task or
incident. The workflow starts the moment a TA ticket is picked up and ends
with a written analysis ready to paste into Jira.

## When to invoke

- Ticket type is "Technische analyse", "Impactanalyse", or
  "Risico en dreigingsanalyse".
- User says "start TA", "begin technische analyse", or picks up a TA ticket
  via the `pickup-ticket` skill.

---

## Steps

### 1. Pick up the ticket

Run the pickup skill if not already done:

```
cd QSP\Programs\Jira
python pickup_ticket.py <TICKET-KEY>
```

### 2. Present full context

The script output contains:
- Ticket summary, type, status, URL
- Parent story summary + description (the business requirement)
- Linked issues (related bugs, stories, incidents)
- Comments (prior discussion, decisions, constraints)

Present all of this to Marco in a clean, readable format. Never truncate
descriptions or comments — every detail can matter for the analysis.

### 3. Ask for additional context

After presenting the ticket, ask:

> "Is there anything else I should know before we start? For example:
> a Slack thread, a related PR, a specific area of the codebase to look at,
> or a colleague who reported this?"

Wait for the user's response. If they provide extra context (links, names,
descriptions), incorporate it before proceeding.

### 4. Structure the analysis

Based on the ticket type, produce a structured analysis:

#### Technische analyse / Impactanalyse

Answer the following:

1. **Wat is het probleem / de wens?**
   Summarise in one paragraph based on the ticket + context.

2. **Welke componenten zijn geraakt?**
   List relevant code areas, services, or modules. Ask the user to point to
   the codebase or describe the domain if not clear.

3. **Wat is de verwachte aanpak?**
   Describe a concrete implementation direction.

> **Note:** Risico's / neveneffecten and effort estimates are **not** part of the
> Technische analyse output. They belong to the separate Risico en dreigingsanalyse
> sub-task and planning steps respectively.

#### Risico en dreigingsanalyse

Answer the following:

1. **Wat verandert er?**
   Describe the feature or change under review.

2. **Welke risico's introduceert dit?**
   Security, data integrity, availability, compliance.

3. **Mitigerende maatregelen**
   For each risk: how is it addressed or accepted?

4. **Conclusie**
   Safe to proceed / needs review / blocked.

### 5. Write the analysis to Jira

**Human checkpoint — mandatory.** Always present the full analysis to the user
and wait for explicit approval **before** calling any Jira write API.
Do not combine step 4 (producing the analysis) and step 5 (writing it) into a
single action.

Once the user has approved, offer to write it:

> "Zal ik de analyse in het omschrijving-veld van [story-key] zetten en het script als bijlage toevoegen?"

Write both in one action:
1. Update the description via `PUT /rest/api/3/issue/<story-key>`
2. Attach any SQL scripts via `POST /rest/api/3/issue/<story-key>/attachments` (see SQL script convention below)

The target ticket is the **parent story** (or the story the TA task links to),
not the TA task (e.g. QSP-99746) itself. The analysis lives in the description
of the story under analysis (e.g. QSP-99202).

#### How to write ADF programmatically

Use `adf_builder.py` — a reusable helper in `QSP/Programs/Jira/`:

```python
from adf_builder import doc, h1, h2, h3, p, text, code, bold, bullet, code_block, table

nodes = [
    h2("Technisch"),
    p("Intro met ", code("inline code"), " en ", bold("bold"), "."),
    code_block("public enum Bedragsoort : short { ... }"),
    table(
        headers=["Component", "Aanpassing"],
        rows=[
            [code("Bedragsoort.cs"), "+2 enum values"],
            ["SQL script",           "INSERT in BedragsoortAuthorisatie"],
        ]
    ),
]
description = {"type": "doc", "version": 1, "content": nodes}
await jira._api_call("PUT", "/rest/api/3/issue/<story-key>", {"fields": {"description": description}})
```

Key rules for valid ADF:
- Every block node needs `"attrs": {"localId": str(uuid.uuid4())}` — `adf_builder` handles this automatically.
- Tables use `"layout": "center"` (not "default").
- Cell content must be a list of **ADF inline dicts**, not raw strings — use `text("...")` or `code("...")`.
- `codeBlock` does not require a `language` attribute.

See `QSP-99202` as a worked example of a complete restructured Technisch section.

---

## Conventions

### Author name
**Never include the user's name** in Jira descriptions, comments, or SQL scripts. Do not add author attribution anywhere in output that goes to external systems or committed files.

### SQL scripts produced during TA
When the analysis involves a SQL fix/cleanup script, the script **must always start with a comment block** containing:

```sql
-- <ticket-key> — <short description>
-- <month year>
--
-- UITVOERPROTOCOL
-- 1. Voer eerst uit met ROLLBACK + diagnostiek-SELECTs aan — valideer aantallen.
-- 2. Laat de uitkomst bevestigen.
-- 3. Voer daarna uit met COMMIT.

BEGIN TRANSACTION
...
-- ROLLBACK  ← verwijder deze regel pas nadat aantallen zijn gevalideerd
COMMIT
```

The script must also be **attached as a `.sql` file** to the Jira story (in addition to
being included as a code block in the description). Use the reusable attachment skill:

```
cd QSP\Programs\Jira
python attach_to_jira.py <story-key> <path-to-script.sql>
```

See `QSP/Skills/jira-attachments.md` for full documentation.

Name the file `<ticket-key>_<short_description>.sql`, e.g.
`QSP-96994_verwijder_vervalkalender_2747912_DL8.sql`.

---

## Output contract

This workflow is **read-heavy** (steps 1–3) and then **write-once** (step 5).
No write actions are taken without explicit user confirmation.

---

## Human checkpoints

| Step | Checkpoint |
|------|-----------|
| After step 2 | User confirms context is sufficient or adds more |
| After step 4 | User reviews and approves the analysis |
| Before step 5 | User confirms before posting to Jira |

---

## Related

- Triggered from: `QSP/Skills/pickup-ticket.md`
- Script: `QSP/Programs/Jira/pickup_ticket.py`
- ADF builder: `QSP/Programs/Jira/adf_builder.py`
- Batch description updater: `QSP/Programs/Jira/update_story_description.py`
- Jira update description API: `PUT /rest/api/3/issue/<key>` via `JiraClient._api_call`
- Example: QSP-99202 — analysis written to description of the story, not to the TA task QSP-99746
