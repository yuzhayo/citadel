#!/usr/bin/env python3
"""status.json writer/reader.

Shape frozen (written_at first, then the payload) so a watcher that reads these
files sees no difference after the refactor.
"""

from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path

from .paths import OUT_DIR


def write_status(path: Path, payload: dict) -> None:
    payload = {"written_at": datetime.now(timezone.utc).isoformat(), **payload}
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")


def read_status(kind: str, profile_id: str) -> dict:
    """Latest ``<kind>_<profileId>.status.json``, or {} when missing/unreadable."""
    for candidate in OUT_DIR.glob(f"{kind}_{profile_id}.status.json"):
        try:
            loaded = json.loads(candidate.read_text(encoding="utf-8"))
            if isinstance(loaded, dict):
                return loaded
        except json.JSONDecodeError:
            pass
    return {}
