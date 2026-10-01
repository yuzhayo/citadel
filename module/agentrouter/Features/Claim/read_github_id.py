#!/usr/bin/env python3
r"""Read github_id from the LIVE account chip and save it to the profile JSON.

The chip is `github_<GitHub user id>` in the console header
(`span.semi-typography` inside the account-menu button, see
AGENTROUTER-DOM-FINDINGS.md). This reads it from the signed-in page —
not from repo code, not from memory.

Saves into:  %TEMP%\opencode\agentrouter-<profileId>.json
  { "github_id": 258838, ...existing keys preserved..., "updated_at": ... }

Runtime: Citadel venv python only. Launch detached, poll status file.
"""

from __future__ import annotations

import json
import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

from open_agentrouter import (
    OUT_DIR, free_lock, log, profile_json_path, read_table, safe_id,
    write_status,
)

CONSOLE_URL = "https://agentrouter.org/console"
GITHUB_ID = re.compile(r"^github_(\d+)$")

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


# profile_json_path now comes from _lib.paths, re-exported by open_agentrouter.


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
    status_path = OUT_DIR / f"github_id_{profile_id}.status.json"
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

        url = page.url
        state = "login" if "/login" in url.lower() else (
            "console" if "/console" in url.lower() else "other")

        # Wait up to 30s for the chip (session may redirect through login).
        chips: list[str] = []
        deadline = time.time() + 30
        while time.time() < deadline:
            chips = page.evaluate(_CHIPS)
            if chips:
                break
            page.wait_for_timeout(1000)

        if not chips:
            write_status(status_path, {
                "stage": "error", "profile": profile_id, "url": url, "state": state,
                "error": "github chip not found within 30s"})
            log(f"error: chip not found (url={url} state={state})")
            return 1

        match = GITHUB_ID.match(chips[0])
        if not match:
            write_status(status_path, {
                "stage": "error", "profile": profile_id,
                "error": f"chip text did not match github_<id>: {chips[0]!r}"})
            log(f"error: bad chip text {chips[0]!r}")
            return 1

        github_id = int(match.group(1))

        # Save into the profile JSON — preserve every existing key.
        json_path = profile_json_path(profile_id)
        data: dict = {}
        if json_path.is_file():
            try:
                data = json.loads(json_path.read_text(encoding="utf-8-sig"))
            except json.JSONDecodeError:
                log("warning: profile json unreadable; rewriting from scratch")
                data = {"profile": profile_id}
        else:
            data = {"profile": profile_id}
        if not isinstance(data, dict):
            data = {"profile": profile_id}

        data["github_id"] = github_id
        data["updated_at"] = datetime.now(timezone.utc).isoformat(timespec="seconds")
        json_path.write_text(json.dumps(data, indent=2), encoding="utf-8")

        write_status(status_path, {
            "stage": "saved", "profile": profile_id,
            "chip": chips[0], "github_id": github_id,
            "chip_count": len(chips), "url": url, "state": state,
            "saved_to": str(json_path),
            "keys_after": sorted(data.keys()),
        })
        log(f"chip={chips[0]} count={len(chips)} -> github_id={github_id} saved to {json_path.name}")

    write_status(status_path, {"stage": "closed", "profile": profile_id})
    log("browser closed; done")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
