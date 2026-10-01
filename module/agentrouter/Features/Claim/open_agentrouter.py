#!/usr/bin/env python3
r"""Open one row of the Agentrouter shortcut table in Camoufox at agentrouter.org.

The table source is the module's own store:
    %LocalAppData%\Citadel\agentrouter\shortcuts.json   (ShortcutCatalog, schema 1)
The browser profile is the CamoProf folder that row points at:
    %LocalAppData%\Citadel\Credenz\google\profiles\<profileId>
The account name shown in the table comes from:
    %LocalAppData%\Citadel\Credenz\google\accounts\<profileId>\identity.json

Usage (Citadel runtime python ONLY):
    python open_agentrouter.py --list
    python open_agentrouter.py --profile <profileId>

Rules baked in from the handoff:
  * Camoufox(headless=False, persistent_context=True, user_data_dir=..., humanize=True, os="windows")
  * one browser per profile: leftover camoufox/firefox processes holding THIS profile dir
    are killed first -- targeted by profile dir, never Chrome, never other profiles
  * secrets masked everywhere; full values stay in their own JSON
  * writes OUT\open_<profileId>.status.json in stages so a hung launch is diagnosable
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# The shared primitives now live in the `_lib` package; re-export them here so
# existing `from open_agentrouter import ...` callers keep working unchanged.
from _lib import (  # noqa: F401
    ACCOUNTS_ROOT, CHIPS_JS, CITADEL, CONSOLE_URL, HOME_URL, LOGGED_OUT,
    LOG_FILE, LOGIN_URL, OUT_DIR, PERSONAL_PATH, PROFILES_ROOT, RUNTIME_PY,
    SAFE_ID, SESSION_JS, SESSION_LIVE, SESSION_UNKNOWN, SHORTCUTS_JSON,
    TARGET_URL, TOKEN_URL, T_FILL_S, T_GOTO, T_IDLE, T_MENU_MS, T_ROUTE_MS,
    classify, display_name, free_lock, launch, load_profile_json, log,
    mask_secret, print_table, profile_json_path, read_chips, read_status,
    read_table, safe_id, save_profile_json, session_state, status_path,
    visible_first, visible_texts, wait_for, write_status,
)


def open_profile(profile_id: str) -> int:
    profile_id = safe_id(profile_id)
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    status_path = OUT_DIR / f"open_{profile_id}.status.json"

    rows = read_table()
    row = next((r for r in rows if r["profileId"].lower() == profile_id.lower()), None)
    if row is None:
        print_table(rows)
        raise SystemExit(f"refused: {profile_id} is not a row of the Agentrouter table")

    profile_dir = Path(row["profileDir"])
    if not profile_dir.is_dir():
        raise SystemExit(f"refused: profile folder missing (row would show Missing): {profile_dir}")

    write_status(status_path, {"stage": "starting", "profile": profile_id,
                               "account": row["account"], "target": TARGET_URL})
    log(f'row: {row["account"]} ({profile_id}) status={"Ready" if row["exists"] else "Missing"}')
    log(f"profile dir: {profile_dir}")

    free_lock(str(profile_dir))

    write_status(status_path, {"stage": "launching", "profile": profile_id,
                               "account": row["account"], "target": TARGET_URL})
    log(f"launching Camoufox headed -> {TARGET_URL}")

    try:
        from camoufox.sync_api import Camoufox
    except ImportError as exc:
        write_status(status_path, {"stage": "error", "profile": profile_id,
                                   "error": f"camoufox import failed: {exc}"})
        raise SystemExit(f"camoufox unavailable in this interpreter: {exc}")

    try:
        with Camoufox(
            headless=False,
            persistent_context=True,
            user_data_dir=str(profile_dir),
            humanize=True,
            os="windows",
        ) as context:
            pages = list(context.pages)
            page = pages[0] if pages else context.new_page()
            page.goto(TARGET_URL, wait_until="domcontentloaded", timeout=60_000)
            try:
                page.wait_for_load_state("networkidle", timeout=15_000)
            except Exception:
                pass  # idle is a nicety, not a gate

            final_url = page.url
            title = page.title()
            state = classify(final_url)
            write_status(status_path, {
                "stage": "ready", "profile": profile_id, "account": row["account"],
                "url": final_url, "title": title, "state": state,
                "target": TARGET_URL,
            })
            log(f"open: url={final_url} title={title!r} state={state}")

            # Owner watches the headed window; hold until it is closed.
            while True:
                time.sleep(2)
                open_pages = [p for p in context.pages if not p.is_closed()]
                if not open_pages:
                    break

    except Exception as exc:
        write_status(status_path, {"stage": "error", "profile": profile_id,
                                   "account": row["account"], "error": f"{type(exc).__name__}: {exc}"})
        log(f"error: {type(exc).__name__}: {exc}")
        return 1

    write_status(status_path, {"stage": "closed", "profile": profile_id,
                               "account": row["account"]})
    log("window closed by owner; done")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Open an Agentrouter table row at agentrouter.org")
    parser.add_argument("--list", action="store_true", help="print the table and exit")
    parser.add_argument("--profile", help="profileId from the Agentrouter table")
    args = parser.parse_args(argv)

    rows = read_table()

    if args.list:
        print_table(rows)
        return 0

    if not args.profile:
        print_table(rows)
        if len(rows) == 1:
            log(f"single row; using {rows[0]['profileId']}")
            return open_profile(rows[0]["profileId"])
        raise SystemExit("--profile <profileId> required when the table has 0 or many rows")

    return open_profile(args.profile)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
