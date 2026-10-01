#!/usr/bin/env python3
r"""GitHub sign-in script for one Agentrouter row (logged-out session).

Flow:
  1. open profile -> https://agentrouter.org, read chip (github_\d+?)
  2. click Sign in (header) -> /login
  3. click Continue with GitHub -> capture what happens

Modes:
  --probe   read-only: chip present? Sign-in link resolves? GitHub button
            resolves? Dumps selectors/boxes. NO clicks. (default)
  --click   performs both clicks with instrumentation: popup listener,
            console listener, github.com request log, before/after
            screenshots + URL/title. NEVER enters credentials, NEVER
            confirms any OAuth authorize screen.

Timeouts: goto 60s | idle 15s best-effort | chip 15s | signin 10s |
          login land 15s | github button 10s | post-click settle 12s |
          TOTAL budget 240s enforced per step.

Secrets: none touched. Runtime: Citadel venv python only.
"""

from __future__ import annotations

import re
import sys
import time
from pathlib import Path

import os

# actions/ module: the Claim root (which holds _lib/ and open_agentrouter.py)
# is the parent directory -- pin it so this file works from any cwd.
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from open_agentrouter import (
    OUT_DIR, free_lock, log, read_table, safe_id, wait_for, write_status,
)

HOME_URL = "https://agentrouter.org"
LOGIN_PATH = "/login"

T_GOTO = 60_000
T_IDLE = 15_000
T_CHIP_S = 15
T_SIGNIN_MS = 10_000
T_ROUTE_MS = 15_000
T_GHBTN_MS = 10_000
T_SETTLE_MS = 12_000
TOTAL_BUDGET_S = 240

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

_SIGNIN_CANDS = """
() => {
  const out = [];
  document.querySelectorAll('a,button').forEach((el) => {
    const t = ((el.textContent || '').trim().replace(/\\s+/g, ' ')).slice(0, 40);
    const href = el.getAttribute('href') || '';
    if (/sign.?in|log.?in/i.test(t) || href === '/login' || href.endsWith('/login')) {
      const r = el.getBoundingClientRect();
      out.push({tag: el.tagName, text: t, href: href,
                cls: String(el.className || '').slice(0, 120),
                box: {x: Math.round(r.x), y: Math.round(r.y),
                      w: Math.round(r.width), h: Math.round(r.height)},
                visible: r.width > 0 && r.height > 0});
    }
  });
  return out.slice(0, 10);
}
"""

_GHBTN_CANDS = """
() => {
  const out = [];
  document.querySelectorAll('a,button').forEach((el) => {
    const t = ((el.textContent || '').trim().replace(/\\s+/g, ' ')).slice(0, 60);
    if (/github/i.test(t)) {
      const r = el.getBoundingClientRect();
      out.push({tag: el.tagName, text: t,
                href: el.getAttribute('href') || '',
                cls: String(el.className || '').slice(0, 160),
                box: {x: Math.round(r.x), y: Math.round(r.y),
                      w: Math.round(r.width), h: Math.round(r.height)},
                visible: r.width > 0 && r.height > 0,
                disabled: el.disabled === true});
    }
  });
  return out.slice(0, 10);
}
"""


class BudgetExceeded(Exception):
    pass


class Flow:
    def __init__(self, profile_id: str, mode: str, status_path: Path):
        self.profile_id = profile_id
        self.mode = mode
        self.status_path = status_path
        self.deadline = time.time() + TOTAL_BUDGET_S
        self.stage = "starting"
        # Evidence that must survive the final "closed" overwrite.
        self.evidence: dict = {}

    def check(self, step: str) -> None:
        if time.time() > self.deadline:
            raise BudgetExceeded(f"total budget {TOTAL_BUDGET_S}s exceeded at {step}")

    def status(self, **extra) -> None:
        write_status(self.status_path,
                     {"stage": self.stage, "profile": self.profile_id,
                      "mode": self.mode, **extra})

    def fail(self, error: str) -> int:
        self.stage = "error"
        self.status(error=error)
        log(f"error: {error}")
        return 1


# wait_for now comes from _lib.browser (same 1.0s default) -- imported via
# open_agentrouter above. One implementation for every Claim script.


def main(argv: list[str]) -> int:
    mode = "probe"
    rest = []
    for arg in argv:
        if arg == "--click":
            mode = "click"
        elif arg == "--probe":
            mode = "probe"
        else:
            rest.append(arg)

    profile_id = safe_id(rest[0]) if rest else None
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
    status_path = OUT_DIR / f"github_signin_{profile_id}.status.json"
    flow = Flow(profile_id, mode, status_path)
    flow.status()
    shots = lambda name: str(OUT_DIR / f"github_signin_{profile_id}_{name}.png")

    try:
        free_lock(row["profileDir"])

        from camoufox.sync_api import Camoufox

        flow.stage = "launching"
        flow.status(target=HOME_URL)
        with Camoufox(
            headless=False,
            persistent_context=True,
            user_data_dir=row["profileDir"],
            humanize=True,
            os="windows",
        ) as context:
            pages = list(context.pages)
            page = pages[0] if pages else context.new_page()
            page.goto(HOME_URL, wait_until="domcontentloaded", timeout=T_GOTO)
            try:
                page.wait_for_load_state("networkidle", timeout=T_IDLE)
            except Exception:
                pass

            # --- chip? (logged-in proof) -----------------------------------
            flow.check("chip")
            chips = wait_for(page, lambda: page.evaluate(_CHIPS), T_CHIP_S)
            chip = chips[0] if chips else None
            log(f"chip={'absent (logged out)' if not chip else chip}")
            flow.stage = "probed-home"
            flow.status(url=page.url, chip=chip, chip_count=len(chips or []))

            # --- sign-in candidates (read-only) ----------------------------
            signin_cands = page.evaluate(_SIGNIN_CANDS)
            log(f"signin candidates: {len(signin_cands)}")
            for cand in signin_cands:
                log(f"  [{cand['tag']}] {cand['text']!r} href={cand['href']!r} "
                    f"visible={cand['visible']}")

            # --- /login page: github button candidates (read-only) ---------
            flow.check("login page")
            page.goto(HOME_URL + LOGIN_PATH, wait_until="domcontentloaded",
                      timeout=T_GOTO)
            try:
                page.wait_for_load_state("networkidle", timeout=T_IDLE)
            except Exception:
                pass
            page.screenshot(path=shots("login.png"), full_page=False)

            gh_cands = page.evaluate(_GHBTN_CANDS)
            log(f"github candidates on /login: {len(gh_cands)}")
            for cand in gh_cands:
                log(f"  [{cand['tag']}] {cand['text']!r} href={cand['href']!r} "
                    f"visible={cand['visible']} disabled={cand['disabled']}")

            flow.stage = "probed"
            flow.status(url=page.url, title=page.title(), chip=chip,
                        signin_candidates=signin_cands,
                        github_candidates=gh_cands,
                        screenshots=[shots("login.png")])
            log(f"probe done on {page.url}")

            if mode == "probe":
                log("probe mode: no clicks performed; browser closing")
            else:
                # --- --click: sign-in then github, instrumented ------------
                events: dict = {
                    "console": [], "github_requests": [],
                    "popup": None, "clicked": False, "click_error": None,
                }
                page.on("console",
                        lambda msg: events["console"].append(
                            f"{msg.type}: {msg.text[:200]}"))

                def _on_request(req) -> None:
                    if "github.com" in (req.url or ""):
                        events["github_requests"].append(
                            f"{req.method} {req.url[:220]}")

                page.on("request", _on_request)

                flow.check("signin click")
                # Owner: never click Console. Sign in (link/button) only —
                # all land on /login anyway, and the probe already navigated
                # there, so no target = continue, not fallback to Console.
                clicked_signin = False
                signin_targets = [
                    c for c in signin_cands
                    if c["visible"]
                    and re.search(r"sign.?in|log.?in", c["text"], re.I)]
                for cand in signin_targets:
                    try:
                        el = page.locator(
                            f"{cand['tag'].lower()}:has-text(\"{cand['text']}\")"
                        ).first
                        el.click(timeout=T_SIGNIN_MS)
                        clicked_signin = True
                        log(f"signin clicked: {cand['text']!r}")
                        break
                    except Exception as exc:
                        log(f"signin candidate failed: {type(exc).__name__}")
                if not clicked_signin:
                    # Already on /login from the probe above; acceptable.
                    flow.status(note="signin click skipped: already on /login")
                    log("signin click skipped (already on /login)")

                try:
                    page.wait_for_url(f"**{LOGIN_PATH}**", timeout=T_ROUTE_MS)
                except Exception:
                    pass
                log(f"after signin: {page.url}")

                flow.check("github click")
                gh_visible = [c for c in gh_cands if c["visible"]
                              and not c["disabled"]]
                if not gh_visible:
                    page.screenshot(path=shots("github.png"), full_page=False)
                    return flow.fail("no visible enabled GitHub button on /login")
                target = gh_visible[0]
                page.screenshot(path=shots("before-click.png"), full_page=False)

                try:
                    with page.expect_popup(timeout=5_000) as popup_info:
                        page.locator(
                            f"{target['tag'].lower()}:has-text(\"{target['text']}\")"
                        ).first.click(timeout=T_GHBTN_MS)
                    popup = popup_info.value
                    popup.on("request", _on_request)
                    page.wait_for_timeout(2000)
                    try:
                        popup_url = popup.url
                    except Exception:
                        popup_url = "(closed already)"
                    # Never store the query string: the callback carries a
                    # single-use OAuth code. Keep origin+path + flags only.
                    events["popup"] = popup_url.split("?")[0]
                    events["popup_has_code"] = "code=" in popup_url
                    log(f"popup opened: {events['popup']} "
                        f"has_code={events['popup_has_code']}")
                    # Let the popup FINISH: it self-closes after the callback,
                    # or the opener shows the chip. Never close it early.
                    completed = wait_for(
                        page,
                        lambda: popup.is_closed() or bool(page.evaluate(_CHIPS)),
                        30)
                    if not completed:
                        log("popup did not finish in 30s; closing it")
                        try:
                            popup.close()
                        except Exception:
                            pass
                    events["popup_completed"] = bool(completed)
                except Exception as exc:
                    events["click_error"] = f"{type(exc).__name__}: {exc}"
                    log(f"no popup ({events['click_error']}); same-tab navigation assumed")

                events["clicked"] = True
                # The opener page is stale: the login completed inside the
                # popup context. Reload the opener first, then read the chip.
                try:
                    page.goto(HOME_URL + "/console",
                              wait_until="domcontentloaded", timeout=T_GOTO)
                except Exception as exc:
                    log(f"opener reload failed: {type(exc).__name__}")
                try:
                    page.wait_for_load_state("networkidle", timeout=T_IDLE)
                except Exception:
                    pass
                page.wait_for_timeout(2000)
                page.screenshot(path=shots("after-click.png"), full_page=False)

                chips_after = page.evaluate(_CHIPS)
                flow.stage = "clicked"
                flow.evidence = {
                    "url_before_click": HOME_URL + LOGIN_PATH,
                    "url_after": page.url, "title_after": page.title(),
                    "chip_after": (chips_after[0] if chips_after else None),
                    "github_contacted": bool(events["github_requests"]),
                    "github_requests": events["github_requests"][:20],
                    "popup": events["popup"],
                    "popup_has_code": events.get("popup_has_code"),
                    "popup_completed": events.get("popup_completed"),
                    "click_error": events["click_error"],
                    "console_tail": events["console"][-15:],
                    "screenshots": [shots("login.png"), shots("before-click.png"),
                                    shots("after-click.png")],
                }
                flow.status(**flow.evidence)
                log(f"clicked: url={page.url} github_contacted="
                    f"{bool(events['github_requests'])} "
                    f"chip_after={chips_after[0] if chips_after else None}")

        flow.stage = "closed"
        flow.status(**flow.evidence)
        log("browser closed; done")
        return 0

    except BudgetExceeded as exc:
        return flow.fail(str(exc))
    except Exception as exc:
        return flow.fail(f"{type(exc).__name__}: {exc}")


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
