"""
adf_builder.py — Helpers for building Atlassian Document Format (ADF) nodes.

Usage:
    from adf_builder import doc, h1, h2, h3, p, code, bold, em, bullet,
                            ordered, code_block, table, rule, text

All block-level functions return a dict that is a valid ADF node.
Pass a list of such nodes to doc() to get a complete ADF document.

Example:
    description = doc([
        h2("Technisch"),
        p("Intro text with ", code("inline code"), " and ", bold("bold"), "."),
        code_block("public enum Bedragsoort : short { ... }"),
        table(
            headers=["Component", "Aanpassing"],
            rows=[
                [code("Bedragsoort.cs"), text("+2 enum values")],
                [text("SQL script"),     text("INSERT in BedragsoortAuthorisatie")],
            ]
        ),
    ])
"""

import uuid


# ---------------------------------------------------------------------------
# Internals
# ---------------------------------------------------------------------------

def _lid() -> str:
    return str(uuid.uuid4())


def _inline(part):
    """Normalise a cell/paragraph part to an ADF inline node."""
    if isinstance(part, str):
        return {"type": "text", "text": part}
    return part  # already an ADF inline dict


# ---------------------------------------------------------------------------
# Inline nodes (return dicts to embed inside paragraph content lists)
# ---------------------------------------------------------------------------

def text(t: str) -> dict:
    return {"type": "text", "text": t}


def code(t: str) -> dict:
    return {"type": "text", "text": t, "marks": [{"type": "code"}]}


def bold(t: str) -> dict:
    return {"type": "text", "text": t, "marks": [{"type": "strong"}]}


def em(t: str) -> dict:
    return {"type": "text", "text": t, "marks": [{"type": "em"}]}


# ---------------------------------------------------------------------------
# Block nodes
# ---------------------------------------------------------------------------

def h1(t: str) -> dict:
    return {"type": "heading", "attrs": {"level": 1, "localId": _lid()},
            "content": [{"type": "text", "text": t}]}


def h2(t: str) -> dict:
    return {"type": "heading", "attrs": {"level": 2, "localId": _lid()},
            "content": [{"type": "text", "text": t}]}


def h3(t: str) -> dict:
    return {"type": "heading", "attrs": {"level": 3, "localId": _lid()},
            "content": [{"type": "text", "text": t}]}


def p(*parts) -> dict:
    """
    Paragraph.  Parts may be strings or inline ADF dicts (from text/code/bold/em).
    Adjacent string args are each turned into separate text nodes.
    """
    return {
        "type": "paragraph",
        "attrs": {"localId": _lid()},
        "content": [_inline(part) for part in parts],
    }


def rule() -> dict:
    return {"type": "rule", "attrs": {"localId": _lid()}}


def code_block(content: str, language: str | None = None) -> dict:
    attrs = {"localId": _lid()}
    if language:
        attrs["language"] = language
    return {"type": "codeBlock", "attrs": attrs,
            "content": [{"type": "text", "text": content}]}


def bullet(items: list) -> dict:
    """
    Bullet list.  Each item is either:
    - a string → plain text list item
    - a list of inline ADF nodes / strings → inline-formatted list item
    """
    list_items = []
    for item in items:
        if isinstance(item, str):
            content = [{"type": "text", "text": item}]
        else:
            content = [_inline(part) for part in item]
        list_items.append({
            "type": "listItem",
            "attrs": {"localId": _lid()},
            "content": [{"type": "paragraph", "attrs": {"localId": _lid()},
                         "content": content}],
        })
    return {"type": "bulletList", "content": list_items}


def ordered(items: list) -> dict:
    """
    Ordered list.  Same item format as bullet().
    """
    list_items = []
    for item in items:
        if isinstance(item, str):
            content = [{"type": "text", "text": item}]
        else:
            content = [_inline(part) for part in item]
        list_items.append({
            "type": "listItem",
            "attrs": {"localId": _lid()},
            "content": [{"type": "paragraph", "attrs": {"localId": _lid()},
                         "content": content}],
        })
    return {"type": "orderedList", "attrs": {"order": 1, "localId": _lid()},
            "content": list_items}


def table(headers: list, rows: list) -> dict:
    """
    Table.

    headers: list of strings or inline ADF dicts.
    rows: list of rows; each row is a list of cells;
          each cell is a string, an inline ADF dict, or a list of inline nodes.

    Example:
        table(
            headers=["Component", "Aanpassing"],
            rows=[
                [code("Bedragsoort.cs"), "+2 enum values"],
                ["SQL script",           "INSERT ..."],
            ]
        )
    """
    def _cell(parts, is_header: bool) -> dict:
        t = "tableHeader" if is_header else "tableCell"
        if isinstance(parts, (str, dict)):
            content = [_inline(parts)]
        else:
            content = [_inline(part) for part in parts]
        return {
            "type": t,
            "attrs": {"localId": _lid()},
            "content": [{"type": "paragraph", "attrs": {"localId": _lid()},
                         "content": content}],
        }

    header_row = {
        "type": "tableRow",
        "attrs": {"localId": _lid()},
        "content": [_cell(h, True) for h in headers],
    }
    body_rows = [
        {"type": "tableRow", "attrs": {"localId": _lid()},
         "content": [_cell(cell, False) for cell in row]}
        for row in rows
    ]
    return {
        "type": "table",
        "attrs": {"isNumberColumnEnabled": False, "layout": "center", "localId": _lid()},
        "content": [header_row] + body_rows,
    }


def doc(nodes: list) -> dict:
    """Wrap a list of block nodes into a complete ADF document."""
    return {"type": "doc", "version": 1, "content": nodes}
