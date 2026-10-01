#!/usr/bin/env python3
"""Profile-id guard.

The same allow-list the C# module enforces before an id becomes a path segment
(ShortcutCatalog drops non-matching ids too). Frozen from open_agentrouter.py.
"""

from __future__ import annotations

import re

SAFE_ID = re.compile(r"^[A-Za-z0-9._-]+$")


def safe_id(profile_id: str) -> str:
    if not SAFE_ID.match(profile_id) or profile_id in (".", ".."):
        raise SystemExit(
            f"refused: profile id is not a safe path segment: {profile_id!r}")
    return profile_id
