#!/usr/bin/env python3
r"""PAT-only for one Agentrouter row. No quit, no API key.

Requires a LIVE session (chip present); if absent -> error
"not logged in, run login first". Never logs in by itself.

Steps:
  1. JSON already holds `pat` -> skip untouched, done (exit 0)
  2. open profile -> console -> chip must exist
  3. chip -> menuitem Personal Settings (owner element:
     span.truncate.font-medium.text-sm, emerald) -> /console/personal
  4. Security Settings TAB (role=tab, .semi-tabs-tab fallback) -> activate
  5. Generate Token (owner element: button.semi-button-primary, first
     VISIBLE match; hidden duplicates exist) -> click
  6. readonly PAT input (owner element: input.semi-input.semi-input-large)
     fills within 20s -> click input, Ctrl+A/C -> clipboard (input fallback)
  7. save `pat` to profile JSON, screenshot + status, browser closed

Timeouts: goto 60s | idle 15s best-effort | chip 30s | menu item 10s |
          route 15s | tab 10s | fill 20s | scroll 5s | TOTAL budget 300s.
Secrets: full PAT only in the profile JSON; stdout/status masked.
Runtime: Citadel venv python only.
"""

from __future__ import annotations

import json
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
PERSONAL_PATH = "/console/personal"

T_GOTO = 60_000
T_IDLE = 15_000
T_CHIP_S = 30
T_MENU_MS = 10_000
T_ROUTE_MS = 15_000
T_FILL_S = 20
TOTAL_BUDGET_S = 300

# Owner-given elements.
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

_PAT_VALUES = """
() => {
  const out = [];
  document.querySelectorAll('input.semi-input.semi-input-large[readonly]').forEach((el) => {
    if ((el.value || '').length > 0) out.push(el.value);
  });
  return out;
}
"""


class BudgetExceeded(Exception):
    pass


# mask_secret (from _lib.logging), wait_for (from _lib.browser) and
# profile_json_path (from _lib.paths) are now imported via open_agentrouter
# above -- one implementation for every Claim script.


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
    status_path = OUT_DIR / f"pat_{profile_id}.status.json"
    deadline = time.time() + TOTAL_BUDGET_S
    shots = lambda name: str(OUT_DIR / f"pat_{profile_id}_{name}.png")
    json_path = profile_json_path(profile_id)

    def check(step: str) -> None:
        if time.time() > deadline:
            raise BudgetExceeded(f"total budget {TOTAL_BUDGET_S}s exceeded at {step}")

    def fail(error: str) -> int:
        write_status(status_path, {"stage": "error", "profile": profile_id,
                                   "error": error})
        log(f"error: {error}")
        return 1

    try:
        write_status(status_path, {"stage": "starting", "profile": profile_id})

        # --- step 1: conditional skip --------------------------------------
        data: dict = {}
        if json_path.is_file():
            try:
                loaded = json.loads(json_path.read_text(encoding="utf-8-sig"))
                if isinstance(loaded, dict):
                    data = loaded
            except json.JSONDecodeError:
                log("warning: profile json unreadable; rewriting from scratch")
        data.setdefault("profile", profile_id)
        if data.get("pat"):
            write_status(status_path, {
                "stage": "saved", "profile": profile_id,
                "skipped": True,
                "pat_masked": mask_secret(str(data["pat"])),
                "note": "pat already in json -> generation skipped untouched",
            })
            log("pat already stored -> skip generation")
            return 0

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
            page.goto(CONSOLE_URL, wait_until="domcontentloaded", timeout=T_GOTO)
            try:
                page.wait_for_load_state("networkidle", timeout=T_IDLE)
            except Exception:
                pass

            # --- step 2: session must be live ------------------------------
            check("chip")
            chips = wait_for(page, lambda: page.evaluate(_CHIPS), T_CHIP_S)
            if not chips:
                page.screenshot(path=shots("login.png"), full_page=False)
                return fail("not logged in (no chip) -> run login first")
            log(f"chip={chips[0]}")

            # --- step 3: Personal Settings ---------------------------------
            check("menu")
            page.locator('button[aria-haspopup="true"]').filter(
                has_text=chips[0]).first.click()
            page.wait_for_timeout(800)
            settings_cands = page.locator(
                "span.truncate.font-medium.text-sm",
                has_text="Personal Settings")
            settings_item = None
            for i in range(settings_cands.count()):
                try:
                    if settings_cands.nth(i).is_visible():
                        settings_item = settings_cands.nth(i)
                        break
                except Exception:
                    pass
            if settings_item is None:
                page.screenshot(path=shots("menu.png"), full_page=False)
                return fail("no visible Personal Settings span")
            settings_item.click()
            page.wait_for_url(f"**{PERSONAL_PATH}**", timeout=T_ROUTE_MS)
            log(f"on personal page: {page.url}")

            # --- step 4: Security Settings TAB -----------------------------
            check("security tab")
            tab = page.get_by_role("tab", name="Security Settings", exact=True)
            if tab.count() == 0:
                tab = page.locator(".semi-tabs-tab").filter(
                    has_text="Security Settings")
            tab.first.wait_for(state="visible", timeout=T_MENU_MS)
            tab.first.scroll_into_view_if_needed(timeout=5_000)
            tab.first.click()
            page.wait_for_timeout(1500)
            page.screenshot(path=shots("sec.png"), full_page=False)
            log("security tab activated")

            # --- step 5: Generate Token (first VISIBLE) --------------------
            check("generate token")
            cands = page.locator(
                "button.semi-button-primary", has_text="Generate Token")
            gen = None
            for i in range(cands.count()):
                try:
                    if cands.nth(i).is_visible():
                        gen = cands.nth(i)
                        break
                except Exception:
                    pass
            if gen is None:
                page.screenshot(path=shots("pat.png"), full_page=False)
                return fail(f"no visible Generate Token button "
                            f"({cands.count()} matches)")
            gen.scroll_into_view_if_needed(timeout=5_000)
            gen.click()
            log(f"generate token clicked ({cands.count()} candidates)")

            # --- step 6: copy readonly input -------------------------------
            check("token fill")
            values = wait_for(page, lambda: page.evaluate(_PAT_VALUES), T_FILL_S)
            if not values:
                page.screenshot(path=shots("pat.png"), full_page=False)
                return fail("readonly PAT input stayed empty 20s after Generate")
            log(f"PAT input filled: {mask_secret(values[0])} "
                f"(candidates={len(values)})")

            first = page.locator(
                "input.semi-input.semi-input-large[readonly]").first
            first.scroll_into_view_if_needed(timeout=5_000)
            first.click()
            page.wait_for_timeout(400)
            page.keyboard.press("Control+A")
            page.keyboard.press("Control+C")
            page.wait_for_timeout(400)
            try:
                clip = page.evaluate("navigator.clipboard.readText()")
            except Exception:
                clip = None
            value = values[0] if not (clip or "").strip() else clip.strip()
            source = "input" if value == values[0] else "clipboard"
            if not value:
                page.screenshot(path=shots("pat.png"), full_page=False)
                return fail("PAT value empty after copy")

            # --- step 7: save ----------------------------------------------
            page.screenshot(path=shots("pat.png"), full_page=False)
            data["pat"] = value
            data["updated_at"] = datetime.now(timezone.utc).isoformat(
                timespec="seconds")
            json_path.write_text(json.dumps(data, indent=2), encoding="utf-8")
            write_status(status_path, {
                "stage": "saved", "profile": profile_id,
                "value_source": source, "pat_masked": mask_secret(value),
                "saved_to": str(json_path), "keys_after": sorted(data.keys()),
                "screenshots": [shots("sec.png"), shots("pat.png")],
            })
            log(f"saved pat ({mask_secret(value)}, via {source}) "
                f"to {json_path.name}")

        write_status(status_path, {"stage": "closed", "profile": profile_id})
        log("browser closed; done")
        return 0

    except BudgetExceeded as exc:
        return fail(str(exc))
    except Exception as exc:
        return fail(f"{type(exc).__name__}: {exc}")


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
