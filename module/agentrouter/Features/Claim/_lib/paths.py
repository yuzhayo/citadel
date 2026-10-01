#!/usr/bin/env python3
"""Filesystem locations, agentrouter service URLs, and the derived
status/creds paths.

Frozen from ``open_agentrouter.py``: every value here is the exact one the
standalone scripts used, so switching a script onto ``_lib`` cannot move a
file or a route.
"""

from __future__ import annotations

import os
from pathlib import Path

# -- vault / store roots -----------------------------------------------------
CITADEL = Path(os.environ["LOCALAPPDATA"]) / "Citadel"
SHORTCUTS_JSON = CITADEL / "agentrouter" / "shortcuts.json"
PROFILES_ROOT = CITADEL / "Credenz" / "google" / "profiles"
ACCOUNTS_ROOT = CITADEL / "Credenz" / "google" / "accounts"
RUNTIME_PY = CITADEL / "runtime" / ".venv" / "Scripts" / "python.exe"

# Scratch dir every script shares: status files, screenshots, and the log.
OUT_DIR = Path(os.environ.get("TEMP", ".")) / "opencode"

# -- agentrouter service -----------------------------------------------------
TARGET_URL = "https://agentrouter.org"
HOME_URL = TARGET_URL
LOGIN_URL = HOME_URL + "/login"
CONSOLE_URL = HOME_URL + "/console"
TOKEN_URL = HOME_URL + "/console/token"
PERSONAL_PATH = "/console/personal"  # route; join to HOME_URL when a full URL is needed


def profile_json_path(profile_id: str) -> Path:
    """Per-profile credential store: %TEMP%\\opencode\\agentrouter-<id>.json."""
    return OUT_DIR / f"agentrouter-{profile_id}.json"


def status_path(kind: str, profile_id: str) -> Path:
    """Status file for one step: ``<kind>_<profileId>.status.json`` (frozen).

    e.g. ``status_path("quit", pid)``        -> ``quit_<pid>.status.json``
         ``status_path("flow_1x_single", pid)`` -> ``flow_1x_single_<pid>.status.json``
    """
    return OUT_DIR / f"{kind}_{profile_id}.status.json"
