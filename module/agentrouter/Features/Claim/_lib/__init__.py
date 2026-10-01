#!/usr/bin/env python3
"""Shared primitives for the Agentrouter Claim scripts.

Extracted from ``open_agentrouter.py`` so every probe, action, and flow shares
ONE implementation. Nothing here changes behaviour: the status.json shape, the
shared log file name, secret masking, the profile-lock ritual, and the
``github_<id>`` chip probe are byte-for-byte what the standalone scripts did.

A script (or shim) imports it from its own folder -- Python already puts the
script directory on ``sys.path``; doing it explicitly keeps ``python <file>.py``
working no matter the cwd:

    import sys, os
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from _lib import OUT_DIR, log, safe_id, read_table, write_status, free_lock
"""

from __future__ import annotations

from .paths import (
    ACCOUNTS_ROOT, CITADEL, CONSOLE_URL, HOME_URL, LOGIN_URL, OUT_DIR,
    PERSONAL_PATH, PROFILES_ROOT, RUNTIME_PY, SHORTCUTS_JSON, TARGET_URL,
    TOKEN_URL, profile_json_path, status_path,
)
from .ids import SAFE_ID, safe_id
from .logging import LOG_FILE, log, mask_secret
from .status import read_status, write_status
from .table import display_name, print_table, read_table
from .creds import load_profile_json, save_profile_json
from .browser import (
    CHIPS_JS, classify, free_lock, launch, read_chips, visible_first,
    visible_texts, wait_for,
)
from .timeouts import T_FILL_S, T_GOTO, T_IDLE, T_MENU_MS, T_ROUTE_MS

__all__ = [
    "ACCOUNTS_ROOT", "CHIPS_JS", "CITADEL", "CONSOLE_URL", "HOME_URL",
    "LOG_FILE", "LOGIN_URL", "OUT_DIR", "PERSONAL_PATH", "PROFILES_ROOT",
    "RUNTIME_PY", "SAFE_ID", "SHORTCUTS_JSON", "TARGET_URL", "TOKEN_URL",
    "T_FILL_S", "T_GOTO", "T_IDLE", "T_MENU_MS", "T_ROUTE_MS",
    "classify", "display_name", "free_lock", "launch", "load_profile_json",
    "log", "mask_secret", "print_table", "profile_json_path", "read_chips",
    "read_status", "read_table", "safe_id", "save_profile_json", "status_path",
    "visible_first", "visible_texts", "wait_for", "write_status",
]
