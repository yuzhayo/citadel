#!/usr/bin/env python3
"""Console + file logger and the secret masker.

Frozen from ``open_agentrouter.py``: the shared log file keeps its original name
so any tooling that tails it keeps working. ``mask_secret`` is the single copy
of the masker that grab_pat / grab_pat_quit / check_balance_api each defined.
"""

from __future__ import annotations

import sys
from datetime import datetime

from .paths import OUT_DIR

LOG_FILE = OUT_DIR / "open_agentrouter.log"


def log(message: str) -> None:
    stamp = datetime.now().strftime("%H:%M:%S")
    line = f"[{stamp}] {message}"
    try:
        print(line, flush=True)
    except UnicodeEncodeError:
        # Redirected stdout under a CJK-blind locale (cp1252): bypass it.
        sys.stdout.buffer.write((line + "\n").encode("utf-8", "replace"))
        sys.stdout.buffer.flush()
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    with open(LOG_FILE, "a", encoding="utf-8", errors="replace") as handle:
        handle.write(line + "\n")


def mask_secret(value: str) -> str:
    """sk-lvh...EHjr style -- never the full value in stdout."""
    if not value:
        return "—"
    if len(value) <= 8:
        return "•" * len(value)
    return f"{value[:4]}...{value[-4:]}"
