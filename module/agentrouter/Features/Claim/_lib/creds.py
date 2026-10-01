#!/usr/bin/env python3
"""Per-profile credentials JSON (%TEMP%\\opencode\\agentrouter-<id>.json).

Full secrets live ONLY here; stdout and status files stay masked. Frozen from
the standalone scripts, including the one drift that mattered: flow_1x_single
backfilled a missing ``profile`` key on load, flow_1x did not -- so the backfill
is opt-in via ``ensure_profile`` and both behaviours are preserved.
"""

from __future__ import annotations

import json
from datetime import datetime, timezone

from .logging import log
from .paths import profile_json_path


def load_profile_json(profile_id: str, *, ensure_profile: bool = False) -> dict:
    path = profile_json_path(profile_id)
    if path.is_file():
        try:
            loaded = json.loads(path.read_text(encoding="utf-8-sig"))
            if isinstance(loaded, dict):
                if ensure_profile:
                    loaded.setdefault("profile", profile_id)
                return loaded
        except json.JSONDecodeError:
            log("warning: profile json unreadable; rewriting from scratch")
    return {"profile": profile_id}


def save_profile_json(profile_id: str, data: dict) -> None:
    data["updated_at"] = datetime.now(timezone.utc).isoformat(
        timespec="seconds")
    profile_json_path(profile_id).write_text(
        json.dumps(data, indent=2), encoding="utf-8")
