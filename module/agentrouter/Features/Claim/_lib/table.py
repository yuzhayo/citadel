#!/usr/bin/env python3
"""The Agentrouter Launcher table, read straight from the module's own store
(``shortcuts.json``, ShortcutCatalog schema 1). Frozen from open_agentrouter.py.
"""

from __future__ import annotations

import json

from .ids import SAFE_ID
from .logging import log
from .paths import ACCOUNTS_ROOT, PROFILES_ROOT, SHORTCUTS_JSON


def display_name(profile_id: str) -> str | None:
    """identity.json email; case-insensitive read, None when absent/corrupt."""
    path = ACCOUNTS_ROOT / profile_id / "identity.json"
    if not path.is_file():
        return None
    try:
        record = json.loads(path.read_text(encoding="utf-8-sig"))
    except (json.JSONDecodeError, OSError):
        return None
    if not isinstance(record, dict):
        return None
    for key, value in record.items():
        if key.lower() == "email" and isinstance(value, str) and value.strip():
            return value.strip()
    return None


def read_table() -> list[dict]:
    """Rows exactly as the Agentrouter Launcher tab would present them."""
    if not SHORTCUTS_JSON.exists():
        raise SystemExit(f"table store missing: {SHORTCUTS_JSON}")

    try:
        document = json.loads(SHORTCUTS_JSON.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError as exc:
        raise SystemExit(f"table store unreadable: {exc}")

    if not isinstance(document, dict) or document.get("schema") != 1:
        raise SystemExit(
            f"table store schema not 1: {document.get('schema')!r}")

    rows = []
    for entry in document.get("profiles") or []:
        profile_id = str(entry.get("profileId") or "")
        if not SAFE_ID.match(profile_id) or profile_id in (".", ".."):
            continue  # ShortcutCatalog drops these too
        added = str(entry.get("addedAtUtc") or "")
        folder = PROFILES_ROOT / profile_id
        rows.append({
            "profileId": profile_id,
            "exists": folder.is_dir(),
            "account": display_name(profile_id) or profile_id,
            "added": added,
            "profileDir": str(folder),
        })
    return sorted(rows, key=lambda row: row["account"].lower())


def print_table(rows: list[dict]) -> None:
    if not rows:
        log("table empty — no profiles added in Agentrouter")
        return
    log(f"table: {len(rows)} row(s)")
    for row in rows:
        status = "Ready" if row["exists"] else "Missing"
        log(f'  {row["account"]:<34} {status:<8} {row["profileId"]}')
