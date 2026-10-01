#!/usr/bin/env python3
r"""Recon (READ-ONLY): hunt the daily check-in ($25 签到) trigger on the site.

Steps:
  1. open profile; chip absent -> login-inline (proven github_signin flow:
     Sign in only, GitHub visible-first, popup completes untouched,
     opener reloaded, chip proof or STOP)
  2. visit each console page (/console, /console/token, /console/log,
     /console/topup, /console/personal, /console/model-status);
     dump every element whose text matches check-in keywords
     (签到, check-in, checkin, sign-in, 每日, claim, $25, 签到送)
     with tag/class/box/visible/href; screenshot each page
  3. write status + HOLD browser open for the owner. NO clicks on candidates.

Timeouts: goto 60s | idle 15s best-effort | chip 15s | popup settle 30s |
          TOTAL budget 600s.
Secrets: none touched. Runtime: Citadel venv python only.
"""

from __future__ import annotations

import re
import sys
import time
from pathlib import Path

from open_agentrouter import (
    OUT_DIR, free_lock, log, read_table, safe_id, write_status,
)

HOME_URL = "https://agentrouter.org"
LOGIN_URL = HOME_URL + "/login"
CONSOLE_URL = HOME_URL + "/console"
PAGES = ["/console", "/console/token", "/console/log",
         "/console/topup", "/console/personal", "/console/model-status"]

T_GOTO = 60_000
T_IDLE = 15_000
T_CHIP_S = 15
T_MENU_MS = 10_000
T_ROUTE_MS = 15_000
T_POPUP_S = 30
TOTAL_BUDGET_S = 600

KEYWORDS = ["签到", "check-in", "checkin", "sign-in", "每日", "claim",
            "$25", "签到送", "check in"]

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

_HUNT = """
(keywords) => {
  const out = [];
  const lowered = keywords.map((k) => k.toLowerCase());
  document.querySelectorAll('a,button,div,span,li').forEach((el) => {
    const t = ((el.textContent || '').trim().replace(/\\s+/g, ' '));
    if (!t || t.length > 80) return;
    const tl = t.toLowerCase();
    const hit = lowered.filter((k) => tl.includes(k));
    if (!hit.length) return;
    const r = el.getBoundingClientRect();
    if (r.width <= 0 || r.height <= 0) return;
    out.push({tag: el.tagName,
              text: t.slice(0, 80), matched: hit,
              href: el.getAttribute('href') || '',
              cls: String(el.className || '').slice(0, 120),
              box: {x: Math.round(r.x), y: Math.round(r.y),
                    w: Math.round(r.width), h: Math.round(r.height)}});
  });
  const seen = new Set();
  return out.filter((e) => {
    const k = e.tag + '|' + e.text;
    if (seen.has(k)) return false;
    seen.add(k);
    return true;
  }).slice(0, 30);
}
"""


class BudgetExceeded(Exception):
    pass


class StepFailed(Exception):
    pass


def wait_for(page, predicate, timeout_s: float, interval_s: float = 1.0):
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        value = predicate()
        if value:
            return value
        page.wait_for_timeout(int(interval_s * 1000))
    return None


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
    status_path = OUT_DIR / f"find_checkin_{profile_id}.status.json"
    deadline = time.time() + TOTAL_BUDGET_S
    shots = lambda name: str(OUT_DIR / f"find_checkin_{profile_id}_{name}.png")
    pages_hit: dict = {}

    def check(step: str) -> None:
        if time.time() > deadline:
            raise BudgetExceeded(f"total budget {TOTAL_BUDGET_S}s exceeded at {step}")

    def fail(error: str) -> int:
        write_status(status_path, {"stage": "error", "profile": profile_id,
                                   "error": error, "pages": pages_hit})
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

            # --- login-inline (proven flow, condensed) ---------------------
            check("login-check")
            page.goto(LOGIN_URL, wait_until="domcontentloaded", timeout=T_GOTO)
            try:
                page.wait_for_load_state("networkidle", timeout=T_IDLE)
            except Exception:
                pass
            chips = wait_for(page, lambda: page.evaluate(_CHIPS), T_CHIP_S)
            if not chips:
                check("github-click")
                gh = page.locator("button", has_text=re.compile("GitHub")).first
                gh.wait_for(state="visible", timeout=T_MENU_MS)
                page.screenshot(path=shots("login.png"), full_page=False)
                try:
                    with page.expect_popup(timeout=5_000) as popup_info:
                        gh.click(timeout=T_MENU_MS)
                    popup = popup_info.value
                    page.wait_for_timeout(2000)
                    completed = wait_for(
                        page,
                        lambda: popup.is_closed()
                        or bool(page.evaluate(_CHIPS)), T_POPUP_S)
                    if not completed:
                        try:
                            popup.close()
                        except Exception:
                            pass
                except Exception as exc:
                    log(f"no popup ({type(exc).__name__})")
                page.goto(CONSOLE_URL, wait_until="domcontentloaded",
                          timeout=T_GOTO)
                try:
                    page.wait_for_load_state("networkidle", timeout=T_IDLE)
                except Exception:
                    pass
                page.wait_for_timeout(2000)
                chips = wait_for(page, lambda: page.evaluate(_CHIPS), 5)
                if not chips:
                    page.screenshot(path=shots("login.png"), full_page=False)
                    return fail("login has no chip proof")
                log(f"logged in: chip={chips[0]}")
            else:
                log(f"already logged in: chip={chips[0]}")

            write_status(status_path, {
                "stage": "hunting", "profile": profile_id,
                "chip": chips[0] if chips else None})

            # --- hunt each page (read-only) --------------------------------
            for path in PAGES:
                check(f"hunt {path}")
                url = HOME_URL + path
                try:
                    page.goto(url, wait_until="domcontentloaded",
                              timeout=T_GOTO)
                except Exception as exc:
                    pages_hit[path] = {"error": f"goto: {type(exc).__name__}"}
                    continue
                try:
                    page.wait_for_load_state("networkidle", timeout=T_IDLE)
                except Exception:
                    pass
                page.wait_for_timeout(1200)
                hits = page.evaluate(_HUNT, KEYWORDS)
                shot = shots(path.strip("/").replace("/", "_") + ".png")
                page.screenshot(path=shot, full_page=False)
                pages_hit[path] = {"url": page.url, "hits": hits,
                                   "screenshot": shot}
                log(f"{path}: {len(hits)} candidate(s)")
                for hit in hits[:12]:
                    log(f"  [{hit['tag']}] {hit['text']!r} "
                        f"matched={hit['matched']}")

            total = sum(len(v.get("hits", [])) for v in pages_hit.values()
                        if isinstance(v, dict))
            write_status(status_path, {
                "stage": "ready", "profile": profile_id,
                "chip": chips[0] if chips else None,
                "total_candidates": total, "pages": pages_hit,
            })
            log(f"hunt done: {total} candidate(s) across {len(pages_hit)} pages")
            log("holding window open; close it when done")

            while True:
                time.sleep(2)
                if not [p for p in context.pages if not p.is_closed()]:
                    break

        write_status(status_path, {"stage": "closed", "profile": profile_id})
        log("browser closed; done")
        return 0

    except (BudgetExceeded, StepFailed) as exc:
        return fail(f"{type(exc).__name__}: {exc}")
    except Exception as exc:
        return fail(f"{type(exc).__name__}: {exc}")


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
