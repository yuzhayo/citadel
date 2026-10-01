#!/usr/bin/env python3
r"""Quit-only for one Agentrouter row. No PAT, no API key, no capture.

Steps:
  1. open profile -> console (logged-out bounces to /login: also a signal)
  2. chip? absent -> already logged out, done (exit 0, nothing to do)
  3. visible account-menu trigger -> click -> wait Quit TEXT visible
     (menu-open proof; items are plain divs, role queries find 0)
  4. click visible Quit text -> verify: /login in url OR chip gone
  5. screenshot + status, browser closed

Timeouts: goto 60s | idle 15s best-effort | chip 15s | menu text 5s |
          quit land 15s | TOTAL budget 180s.
Secrets: none touched. Runtime: Citadel venv python only.
"""

from __future__ import annotations

import sys
import time
from pathlib import Path

import os

# actions/ module: the Claim root (which holds _lib/ and open_agentrouter.py)
# is the parent directory -- pin it so this file works from any cwd.
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from open_agentrouter import (
    OUT_DIR, free_lock, log, read_table, safe_id, write_status,
)

CONSOLE_URL = "https://agentrouter.org/console"

T_GOTO = 60_000
T_IDLE = 15_000
T_CHIP_S = 15
T_MENU_S = 5
T_QUIT_MS = 15_000
TOTAL_BUDGET_S = 180

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


class BudgetExceeded(Exception):
    pass


# Kept local ON PURPOSE: this one polls at 0.5s, _lib.browser.wait_for at 1.0s.
# Per-script timing that differs stays in its own module (see _lib/timeouts.py).


def wait_for(page, predicate, timeout_s: float, interval_s: float = 0.5):
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        value = predicate()
        if value:
            return value
        page.wait_for_timeout(int(interval_s * 1000))
    return None


def _visible_texts(page, text: str) -> list:
    cands = page.get_by_text(text, exact=True)
    out = []
    for i in range(cands.count()):
        try:
            if cands.nth(i).is_visible():
                out.append(cands.nth(i))
        except Exception:
            pass
    return out


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
    status_path = OUT_DIR / f"quit_{profile_id}.status.json"
    deadline = time.time() + TOTAL_BUDGET_S
    quit_shot = str(OUT_DIR / f"quit_{profile_id}.png")
    # Evidence that must survive the final "closed" overwrite.
    saved_evidence: dict = {}

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

            check("chip")
            chips = wait_for(page, lambda: page.evaluate(_CHIPS), T_CHIP_S)
            if not chips:
                saved_evidence = {
                    "url": page.url, "logged_out": True,
                    "note": "chip absent on entry -> already logged out, nothing to do",
                }
                write_status(status_path, {
                    "stage": "saved", "profile": profile_id, **saved_evidence,
                })
                log(f"already logged out (url={page.url}); nothing to do")

            else:
                log(f"chip={chips[0]} -> quitting")
                check("trigger")
                trig_cands = page.locator(
                    'button[aria-haspopup="true"]', has_text=chips[0])
                trigger = None
                for i in range(trig_cands.count()):
                    try:
                        if trig_cands.nth(i).is_visible():
                            trigger = trig_cands.nth(i)
                            break
                    except Exception:
                        pass
                if trigger is None:
                    page.screenshot(path=quit_shot, full_page=False)
                    return fail("no visible account-menu trigger "
                                f"({trig_cands.count()} matches)")
                trigger.click()
                page.wait_for_timeout(800)

                check("menu")
                menu_open = wait_for(
                    page, lambda: len(_visible_texts(page, "Quit")) > 0, T_MENU_S)
                if not menu_open:
                    log("menu text not visible; retrying trigger once")
                    trigger.click()
                    page.wait_for_timeout(800)
                    menu_open = wait_for(
                        page, lambda: len(_visible_texts(page, "Quit")) > 0,
                        T_MENU_S)
                if not menu_open:
                    page.screenshot(path=quit_shot, full_page=False)
                    return fail("account menu did not open")

                check("quit click")
                quits = _visible_texts(page, "Quit")
                quits[0].click()
                page.wait_for_timeout(2000)
                try:
                    page.wait_for_load_state("networkidle", timeout=T_IDLE)
                except Exception:
                    pass

                final_url = page.url
                chips_after = page.evaluate(_CHIPS)
                logged_out = (not chips_after) or ("/login" in final_url.lower())
                page.screenshot(path=quit_shot, full_page=False)
                saved_evidence = {
                    "chip_before": chips[0], "final_url": final_url,
                    "logged_out": logged_out, "screenshot": quit_shot,
                }
                write_status(status_path, {
                    "stage": "saved", "profile": profile_id, **saved_evidence,
                })
                log(f"quit clicked -> url={final_url} chip_gone={not chips_after} "
                    f"logged_out={logged_out}")
                if not logged_out:
                    return fail("quit clicked but session still live "
                                f"(url={final_url})")

        write_status(status_path, {"stage": "closed", "profile": profile_id,
                                       **saved_evidence})
        log("browser closed; done")
        return 0

    except BudgetExceeded as exc:
        return fail(str(exc))
    except Exception as exc:
        return fail(f"{type(exc).__name__}: {exc}")


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
