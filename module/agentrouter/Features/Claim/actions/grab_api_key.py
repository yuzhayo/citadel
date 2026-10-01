#!/usr/bin/env python3
r"""Click API Token in the account dropdown, click Copy, save the key to JSON.

Order is fixed by the instruction:
  1. click chip (`github_*` account button, aria-haspopup) -> dropdown
  2. click menuitem `API Token`  -> /console/token
  3. click the Copy button, identified ONLY by its icon path:
       d="M7 4c0-1.1.9-2 2-2h11a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2h-1V8c0-2-1-3-3-3H7V4Z"
  4. save the key (the readonly input's value) into the profile JSON as `api_key`

Saves into:  %TEMP%\opencode\agentrouter-<profileId>.json
Secrets: full value ONLY in that JSON. stdout/status = sk-xxxx...yyyy mask.

Runtime: Citadel venv python only. Launch detached, poll status file.
"""

from __future__ import annotations

import json
import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import os

# actions/ module: the Claim root (which holds _lib/ and open_agentrouter.py)
# is the parent directory -- pin it so this file works from any cwd.
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from open_agentrouter import (
    OUT_DIR, free_lock, log, mask_secret, profile_json_path, read_table,
    safe_id, wait_for, write_status,
)

CONSOLE_URL = "https://agentrouter.org/console"
TOKEN_PATH = "/console/token"

# The Copy icon the instruction gave — exact path `d`.
COPY_PATH_D = ("M7 4c0-1.1.9-2 2-2h11a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2h-1"
               "V8c0-2-1-3-3-3H7V4Z")

_CHIPS = """
() => {
  const out = [];
  document.querySelectorAll('span').forEach((el) => {
    const t = (el.textContent || '').trim();
    if (/^github_\\d+$/.test(t)) out.push(t);
  });
  return out;
}
"""

_FIND_KEY = """
() => {
  const inputs = [...document.querySelectorAll('input,textarea')];
  const hit = inputs.find((el) => (el.value || '').startsWith('sk-'));
  return hit ? hit.value : null;
}
"""


# profile_json_path (from _lib.paths) and wait_for (from _lib.browser) are now
# imported via open_agentrouter above -- one implementation for every script.


def main(argv: list[str]) -> int:
    profile_id = safe_id(argv[0]) if argv else None
    if not profile_id:
        rows = read_table()
        if len(rows) != 1:
            raise SystemExit("profile id required when the table has multiple rows")
        profile_id = rows[0]["profileId"]

    rows = read_table()
    row = next((r for r in rows if r["profileId"].lower() == profile_id.lower()), None)
    if row is None:
        raise SystemExit(f"refused: {profile_id} not a row of the Agentrouter table")

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    status_path = OUT_DIR / f"api_key_{profile_id}.status.json"
    write_status(status_path, {"stage": "starting", "profile": profile_id})

    free_lock(row["profileDir"])

    from camoufox.sync_api import Camoufox

    write_status(status_path, {"stage": "launching", "profile": profile_id})
    with Camoufox(
        headless=False,
        persistent_context=True,
        user_data_dir=row["profileDir"],
        humanize=True,
        os="windows",
    ) as context:
        pages = list(context.pages)
        page = pages[0] if pages else context.new_page()
        page.goto(CONSOLE_URL, wait_until="domcontentloaded", timeout=60_000)
        try:
            page.wait_for_load_state("networkidle", timeout=15_000)
        except Exception:
            pass

        # --- step 1: chip (dropdown trigger) ---------------------------------
        chips = wait_for(page, lambda: page.evaluate(_CHIPS), 30)
        if not chips:
            write_status(status_path, {"stage": "error", "profile": profile_id,
                                       "error": "github chip not found within 30s"})
            log("error: chip not found")
            return 1
        log(f"chip={chips[0]}")

        page.locator(
            'button[aria-haspopup="true"]'
        ).filter(has_text=chips[0]).first.click()
        page.wait_for_timeout(800)

        # --- step 2: menuitem API Token --------------------------------------
        item = page.get_by_role("menuitem", name="API Token", exact=True)
        item.first.wait_for(state="visible", timeout=10_000)
        item.first.click()
        page.wait_for_url(f"**{TOKEN_PATH}**", timeout=15_000)
        page.wait_for_load_state("networkidle", timeout=15_000)
        log(f"on token page: {page.url}")

        # --- the key from the readonly input ---------------------------------
        key = wait_for(page, lambda: page.evaluate(_FIND_KEY), 15)
        if not key:
            write_status(status_path, {"stage": "error", "profile": profile_id,
                                       "error": "no sk- input found on /console/token"})
            log("error: no sk- input")
            return 1
        log(f"key present: {mask_secret(key)}")

        # --- step 3: click Copy (icon path is the identifier) ----------------
        copy_locator = page.locator(f'svg path[d="{COPY_PATH_D}"]')
        count = copy_locator.count()
        if count == 0:
            write_status(status_path, {
                "stage": "error", "profile": profile_id,
                "error": "copy icon path not found on /console/token",
                "key_masked": mask_secret(key)})
            log("error: copy icon not found")
            return 1

        copy_locator.first.scroll_into_view_if_needed(timeout=5_000)
        copy_locator.first.click(force=True)
        page.wait_for_timeout(600)
        log(f"copy clicked (icon hits={count})")

        # Clipboard may be denied to the page; input value is the save source.
        try:
            clip = page.evaluate("navigator.clipboard.readText()")
        except Exception:
            clip = None
        saved_value = clip if (clip or "").startswith("sk-") else key
        source = "clipboard" if saved_value == clip else "input"

        # --- step 4: save to profile JSON ------------------------------------
        json_path = profile_json_path(profile_id)
        data: dict = {}
        if json_path.is_file():
            try:
                data = json.loads(json_path.read_text(encoding="utf-8-sig"))
            except json.JSONDecodeError:
                log("warning: profile json unreadable; rewriting from scratch")
        if not isinstance(data, dict):
            data = {}
        data.setdefault("profile", profile_id)
        data["api_key"] = saved_value
        data["updated_at"] = datetime.now(timezone.utc).isoformat(timespec="seconds")
        json_path.write_text(json.dumps(data, indent=2), encoding="utf-8")

        write_status(status_path, {
            "stage": "saved", "profile": profile_id,
            "url": page.url, "copy_hits": count, "value_source": source,
            "api_key_masked": mask_secret(saved_value),
            "saved_to": str(json_path), "keys_after": sorted(data.keys()),
        })
        log(f"saved api_key ({mask_secret(saved_value)}, via {source}) to {json_path.name}")

        # Owner watches the headed window: hold until HE closes it.
        log("holding window open; close it when done")
        while True:
            time.sleep(2)
            if not [p for p in context.pages if not p.is_closed()]:
                break

    write_status(status_path, {"stage": "closed", "profile": profile_id})
    log("browser closed; done")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
