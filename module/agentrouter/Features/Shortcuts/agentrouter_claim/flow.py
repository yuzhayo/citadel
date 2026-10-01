#!/usr/bin/env python3
#!/usr/bin/env python3
r"""Claim the daily reward for ONE Agentrouter profile.

Seven steps, one browser lifecycle, one evidence trail:

  1. goto /login, then the conditional verifier: chip?
  2. chip PRESENT -> quit (max 3 tries), no extra navigation
  3. chip ABSENT confirmed -> login (open the Sign in trigger, then Continue
     with GitHub). The OAuth popup may never self-close, so completion is read
     from the popup URL and the chip is proven after ONE console reload.
  4. JSON api_key? exists -> SKIP | empty -> capture
  5. JSON pat?     exists -> SKIP | empty -> capture
  6. quit final -> verify /login and chip gone
  7. browser closed. End state deterministic: logged out.

Why logout first: capture always happens on the FRESH session, never on a stale
inherited one, and a silently failed logout must not stack a login on a live
session.

`headless` is LITERAL: True hides the browser for the whole run, False shows
it. Nothing flips it mid-run.

Status, credentials and screenshots go to Citadel's own data folder -- never to
%TEMP%; see _lib.OUT_DIR.

Secrets: full values only in the profile JSON; stdout and status are masked.
Runtime: driven by the citizen's pyhost process. A manual run uses the Citadel
venv python: python flow.py <profileId> [--headless]
"""

from __future__ import annotations

import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

from ._lib import (
    OUT_DIR, SESSION_LIVE, dismiss_notice, free_lock, load_profile_json, log,
    mask_secret, read_status, read_table, safe_id, save_profile_json,
    session_state, visible_first, visible_texts, wait_for, write_status,
)

HOME_URL = "https://agentrouter.org"
LOGIN_URL = HOME_URL + "/login"
CONSOLE_URL = HOME_URL + "/console"
TOKEN_URL = HOME_URL + "/console/token"
PERSONAL_PATH = "/console/personal"

T_GOTO = 60_000
T_IDLE = 15_000
T_CHIP_S = 15
T_MENU_MS = 10_000
T_ROUTE_MS = 15_000
T_FILL_S = 20
T_POPUP_S = 30
TOTAL_BUDGET_S = 600
RESET_TRIES = 3
SETTLE_S = 2.5

# Owner-given copy icon path (grab_api_key proven: exactly 1 hit).
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

_SIGNIN_CANDS = """
() => {
  const out = [];
  document.querySelectorAll('a,button').forEach((el) => {
    const t = ((el.textContent || '').trim().replace(/\\s+/g, ' ')).slice(0, 40);
    const href = el.getAttribute('href') || '';
    if (/sign.?in|log.?in/i.test(t) || href === '/login' || href.endsWith('/login')) {
      const r = el.getBoundingClientRect();
      out.push({tag: el.tagName, text: t, href: href,
                box: {x: Math.round(r.x), y: Math.round(r.y)},
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
                box: {x: Math.round(r.x), y: Math.round(r.y)},
                visible: r.width > 0 && r.height > 0,
                disabled: el.disabled === true});
    }
  });
  return out.slice(0, 10);
}
"""

_FIND_KEY = """
() => {
  const inputs = [...document.querySelectorAll('input,textarea')];
  const hit = inputs.find((el) => (el.value || '').startsWith('sk-'));
  return hit ? hit.value : null;
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


class StepFailed(Exception):
    pass


# mask_secret (here) and wait_for / visible_first / visible_texts now live in
# _lib.logging and _lib.browser -- one copy for every Claim script, imported
# above. The bodies are identical, so behaviour is unchanged.


class InlineStrategy:
    """One browser, seven steps, one evidence trail (the Claim flow)."""

    def __init__(self, profile_id: str, profile_dir: str):
        self.profile_id = profile_id
        self.profile_dir = profile_dir
        self.status_path = OUT_DIR / f"flow_1x_single_{profile_id}.status.json"
        self.deadline = time.time() + TOTAL_BUDGET_S
        self.steps: list[dict] = []
        self.data: dict = {}
        self.page = None

    def check(self, step: str) -> None:
        if time.time() > self.deadline:
            raise BudgetExceeded(
                f"total budget {TOTAL_BUDGET_S}s exceeded at {step}")

    def record(self, name: str, ok: bool, detail: str = "") -> None:
        self.steps.append({"step": name, "ok": ok, "detail": detail,
                           "at": datetime.now(timezone.utc).isoformat(
                               timespec="seconds")})
        log(f"[{name}] {'ok' if ok else 'FAIL'} {detail}")
        if not ok:
            raise StepFailed(f"{name}: {detail}")

    def status(self, stage: str, **extra) -> None:
        write_status(self.status_path,
                     {"stage": stage, "profile": self.profile_id,
                      "steps": list(self.steps), **extra})

    def fail(self, error: str) -> int:
        self.status("error", error=error)
        log(f"error: {error}")
        return 1

    def shots(self, name: str) -> str:
        return str(OUT_DIR / f"flow_1x_single_{self.profile_id}_{name}.png")

    # -- shared primitives (copied logic, single page) --------------------

    def session(self, timeout_s: float = T_CHIP_S,
                confirm_out: int = 3) -> tuple[str, dict]:
        """Conditional verifier -- see _lib.browser.session_state.

        "live" on the first poll that sees the chip; "out" only after
        ``confirm_out`` polls agree, so a chip still rendering on /login
        cannot be mistaken for a logout.
        """
        return session_state(self.page, timeout_s, confirm_out=confirm_out)

    def clear_notice(self) -> None:
        """Clear the daily System Notice if it is up.

        It is a modal (``aria-modal="true"``), so while it is visible every
        click aimed at the page behind it is eaten. Called before the first
        interaction on a page, because a fresh load can bring it back.
        """
        result = dismiss_notice(self.page)
        if result != "none":
            log(f"system notice: {result}")

    def open_menu(self, chip: str) -> None:
        """Click the visible account-menu trigger; prove open by Quit text."""
        if not chip:
            raise StepFailed("open_menu: chip absent, session dead")
        # The menu cannot be clicked through an open modal overlay.
        self.clear_notice()
        trig = visible_first(
            self.page,
            self.page.locator('button[aria-haspopup="true"]', has_text=chip))
        if trig is None:
            raise StepFailed("open_menu: no visible account-menu trigger")
        trig.click()
        self.page.wait_for_timeout(800)
        if not visible_texts(self.page, "Quit"):
            trig.click()
            self.page.wait_for_timeout(800)
        if not visible_texts(self.page, "Quit"):
            self.page.screenshot(path=self.shots("menu.png"), full_page=False)
            raise StepFailed("open_menu: menu did not open")

    def quit_inline(self) -> None:
        """Click Quit (atomic JS find+click) + verify /login or chip gone.

        Playwright click raced the dropdown: menu closed between the
        visibility check and the click (html intercepts pointer events,
        then detached). One evaluate finds the smallest visible exact
        "Quit" match (the inner span, not a container) and clicks it in
        the same tick -- no window for the race.
        """
        clicked = self.page.evaluate("""() => {
          let best = null, bestArea = Infinity;
          for (const el of document.querySelectorAll('span,div,a,li,button')) {
            if ((el.textContent || '').trim() !== 'Quit') continue;
            const r = el.getBoundingClientRect();
            if (r.width <= 0 || r.height <= 0) continue;
            const a = r.width * r.height;
            if (a < bestArea) { best = el; bestArea = a; }
          }
          if (!best) return false;
          best.click();
          return true;
        }""")
        if not clicked:
            self.page.screenshot(path=self.shots("quit.png"), full_page=False)
            raise StepFailed("quit_inline: no visible Quit text at click time")
        self.page.wait_for_timeout(2000)
        try:
            self.page.wait_for_load_state("networkidle", timeout=T_IDLE)
        except Exception:
            pass
        final_url = self.page.url
        gone = not self.page.evaluate(_CHIPS)
        self.page.screenshot(path=self.shots("quit.png"), full_page=False)
        if not (gone or "/login" in final_url.lower()):
            raise StepFailed(f"quit clicked but session live ({final_url})")
        log(f"quit -> url={final_url} chip_gone={gone}")


# profile_json_path / load_profile_json / save_profile_json now live in
# _lib.paths and _lib.creds (imported above). load_profile_json here is called
# with ensure_profile=True to keep the original "profile" key backfill.


def _popup_landed(popup) -> bool:
    """True once the OAuth popup has landed back on the app (or is gone).

    Live observation, twice: the popup reaches agentrouter.org and then just
    sits there -- it never self-closes. Waiting on ``is_closed()`` therefore
    burns the whole T_POPUP_S budget every single run. The URL arriving back
    on the app is the real completion signal.
    """
    try:
        if popup.is_closed():
            return True
        url = (popup.url or "").lower()
    except Exception:
        return True
    return "agentrouter.org" in url and "/login" not in url


def run_inline(row: dict, headless: bool = False) -> int:
    """InlineStrategy: ONE browser, seven steps, one evidence trail."""
    profile_id = row["profileId"]
    flow = InlineStrategy(profile_id, row["profileDir"])
    flow.status("starting")
    flow.data = load_profile_json(profile_id, ensure_profile=True)

    try:
        flow.check("launch")
        free_lock(row["profileDir"])

        from camoufox.sync_api import Camoufox

        flow.status("launching")
        with Camoufox(
            headless=headless,
            persistent_context=True,
            user_data_dir=row["profileDir"],
            humanize=True,
            os="windows",
        ) as context:
            pages = list(context.pages)
            flow.page = pages[0] if pages else context.new_page()
            page = flow.page

            # --- steps 1-3: ONE entry navigation + the verifier ---------
            flow.check("check-login")
            page.goto(LOGIN_URL, wait_until="domcontentloaded", timeout=T_GOTO)
            try:
                page.wait_for_load_state("networkidle", timeout=T_IDLE)
            except Exception:
                pass
            # A signed-in session can still be mid-render, so the entry check is
            # the one place that insists on a longer "out" streak.
            state, info = flow.session(T_CHIP_S, confirm_out=6)
            chip = info["chip"]
            flow.record("check-login", True,
                        f"chip={chip or 'absent'} url={info['url']}")

            tries = 0
            while state == SESSION_LIVE and tries < RESET_TRIES:
                tries += 1
                flow.check(f"reset-quit-{tries}")
                flow.open_menu(chip)
                flow.quit_inline()
                flow.record(f"reset-quit-{tries}", True, "logged_out")
                time.sleep(SETTLE_S)
                # No navigation and no blind sleep: the Quit click lands on
                # /login, and the verifier answers "out" on its FIRST poll.
                state, info = flow.session()
                chip = info["chip"]
                flow.record("recheck-login", True,
                            f"chip={chip or 'absent'}")
            if state == SESSION_LIVE:
                flow.record("reset-loop", False,
                            f"chip still present after {RESET_TRIES} quits")
            flow.record("reset-loop", True, "logout confirmed")

            # --- step 4: fresh login ------------------------------------
            # We are ALREADY on /login (the entry navigation, or the Quit
            # click), so the old HOME tour and the second /login navigation are
            # gone. Navigate ONLY if we actually drifted somewhere else.
            flow.check("login-probe")
            if "/login" not in (info["url"] or page.url or "").lower():
                page.goto(LOGIN_URL, wait_until="domcontentloaded",
                          timeout=T_GOTO)
            flow.clear_notice()
            page.screenshot(path=flow.shots("login.png"), full_page=False)
            gh_cands = page.evaluate(_GHBTN_CANDS)
            gh_visible = [c for c in gh_cands
                          if c["visible"] and not c["disabled"]]
            if not gh_visible:
                flow.record("login-probe", False, "no visible GitHub button")
            target = gh_visible[0]
            log(f"github button: [{target['tag']}] {target['text']!r}")

            # The provider list sits behind a "Sign in" trigger. Live proof
            # (22:07): clicking Continue with GitHub WITHOUT opening it first
            # produced no popup at all -- expect_popup timed out after 5s and
            # the login gate correctly failed. The old code enumerated this
            # list on HOME and then clicked it here; the CLICK is the part that
            # matters, so it stays and only the HOME round trip is dropped.
            flow.check("signin-click")
            for cand in [c for c in page.evaluate(_SIGNIN_CANDS)
                         if c["visible"]
                         and re.search(r"sign.?in|log.?in", c["text"], re.I)]:
                try:
                    page.locator(
                        f"{cand['tag'].lower()}:has-text(\"{cand['text']}\")"
                    ).first.click(timeout=T_MENU_MS)
                    log(f"signin clicked: {cand['text']!r}")
                    break
                except Exception as exc:
                    log(f"signin candidate failed: {type(exc).__name__}")

            flow.check("github-click")
            events: dict = {"github_requests": [], "popup": None,
                            "has_code": False, "completed": False,
                            "click_error": None}

            def _on_request(req) -> None:
                if "github.com" in (req.url or ""):
                    events["github_requests"].append(
                        f"{req.method} {req.url[:160]}")

            page.on("request", _on_request)
            page.screenshot(path=flow.shots("before-click.png"),
                            full_page=False)
            try:
                with page.expect_popup(timeout=5_000) as popup_info:
                    page.locator(
                        f"{target['tag'].lower()}:has-text(\"{target['text']}\")"
                    ).first.click(timeout=T_MENU_MS)
                popup = popup_info.value
                popup.on("request", _on_request)
                page.wait_for_timeout(2000)
                try:
                    popup_url = popup.url
                except Exception:
                    popup_url = "(closed already)"
                events["popup"] = popup_url.split("?")[0]
                events["has_code"] = "code=" in popup_url
                log(f"popup: {events['popup']} has_code={events['has_code']}")
                completed = wait_for(page, lambda: _popup_landed(popup),
                                     T_POPUP_S)
                if not completed:
                    log("popup did not reach the app in 30s; closing it")
                try:
                    popup.close()
                except Exception:
                    pass
                events["completed"] = bool(completed)
            except Exception as exc:
                events["click_error"] = f"{type(exc).__name__}: {exc}"
                log(f"no popup ({events['click_error']})")

            # Opener is stale: the login completed in the popup context, so
            # ONE reload of the console is needed -- and the verifier proves it.
            page.goto(CONSOLE_URL, wait_until="domcontentloaded",
                      timeout=T_GOTO)
            try:
                page.wait_for_load_state("networkidle", timeout=T_IDLE)
            except Exception:
                pass
            page.screenshot(path=flow.shots("after-click.png"),
                            full_page=False)
            state, info = flow.session()
            chip = info["chip"]
            if state != SESSION_LIVE:
                flow.record("login", False,
                            f"no chip proof (state={state} "
                            f"popup={events['popup']} "
                            f"code={events['has_code']})")
            flow.record("login", True,
                        f"chip_after={chip} url={info['url']}")

            # --- step 5: api_key ----------------------------------------
            flow.check("api_key")
            if flow.data.get("api_key"):
                flow.record("api_key", True, "exists -> SKIP")
            else:
                flow.open_menu(chip)
                api_item = visible_first(
                    page, page.get_by_role("menuitem", name="API Token",
                                           exact=True))
                if api_item is None:
                    api_item = visible_first(
                        page, page.locator(':text-is("API Token")'))
                if api_item is None:
                    flow.record("capture-api", False,
                                "no visible API Token item")
                api_item.click()
                page.wait_for_url("**/console/token**", timeout=T_ROUTE_MS)
                try:
                    page.wait_for_load_state("networkidle", timeout=T_IDLE)
                except Exception:
                    pass
                key = wait_for(page, lambda: page.evaluate(_FIND_KEY), 15)
                if not key:
                    flow.record("capture-api", False, "no sk- input")
                log(f"key present: {mask_secret(key)}")
                copy_loc = page.locator(f'svg path[d="{COPY_PATH_D}"]')
                if copy_loc.count() == 0:
                    flow.record("capture-api", False, "copy icon not found")
                copy_loc.first.scroll_into_view_if_needed(timeout=5_000)
                copy_loc.first.click(force=True)
                page.wait_for_timeout(600)
                try:
                    clip = page.evaluate("navigator.clipboard.readText()")
                except Exception:
                    clip = None
                value = key if not (clip or "").startswith("sk-") else clip
                flow.data["api_key"] = value
                save_profile_json(profile_id, flow.data)
                flow.record("capture-api", True,
                            f"saved {mask_secret(value)}")

            # --- step 6: pat ----------------------------------------------
            flow.check("pat")
            if flow.data.get("pat"):
                flow.record("pat", True, "exists -> SKIP")
            else:
                flow.open_menu(chip)
                settings_cands = page.locator(
                    "span.truncate.font-medium.text-sm",
                    has_text="Personal Settings")
                settings_item = visible_first(page, settings_cands)
                if settings_item is None:
                    flow.record("capture-pat", False,
                                "no visible Personal Settings span")
                settings_item.click()
                page.wait_for_url(f"**{PERSONAL_PATH}**", timeout=T_ROUTE_MS)
                tab = page.get_by_role("tab", name="Security Settings",
                                       exact=True)
                if tab.count() == 0:
                    tab = page.locator(".semi-tabs-tab").filter(
                        has_text="Security Settings")
                tab.first.wait_for(state="visible", timeout=T_MENU_MS)
                tab.first.scroll_into_view_if_needed(timeout=5_000)
                tab.first.click()
                page.wait_for_timeout(1500)
                page.screenshot(path=flow.shots("sec.png"), full_page=False)
                cands = page.locator("button.semi-button-primary",
                                     has_text="Generate Token")
                gen = visible_first(page, cands)
                if gen is None:
                    flow.record("capture-pat", False,
                                "no visible Generate Token")
                gen.scroll_into_view_if_needed(timeout=5_000)
                gen.click()
                log("generate token clicked")
                values = wait_for(page,
                                  lambda: page.evaluate(_PAT_VALUES), T_FILL_S)
                if not values:
                    flow.record("capture-pat", False,
                                "PAT input stayed empty 20s")
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
                if not value:
                    flow.record("capture-pat", False, "PAT value empty")
                page.screenshot(path=flow.shots("pat.png"), full_page=False)
                flow.data["pat"] = value
                save_profile_json(profile_id, flow.data)
                flow.record("capture-pat", True,
                            f"saved {mask_secret(value)}")

            # --- step 7: final quit ---------------------------------------
            flow.check("final-quit")
            # Kept on purpose: a known page where the account menu is proven
            # to exist. The blind 1200ms sleep and both chip polls are gone --
            # the verifier decides, and answers "out" on its first poll.
            page.goto(CONSOLE_URL, wait_until="domcontentloaded",
                      timeout=T_GOTO)
            state, info = flow.session(5)
            if state != SESSION_LIVE:
                flow.record("final-quit", True,
                            "already logged out before final quit")
            else:
                flow.open_menu(info["chip"])
                flow.quit_inline()
                flow.record("final-quit", True, f"logged_out url={page.url}")
            state, info = flow.session(5)
            if state == SESSION_LIVE:
                flow.record("final-verify", False,
                            f"chip still present: {info['chip']}")

            flow.status("saved", keys_after=sorted(flow.data.keys()))
            log(f"1x single done: keys={sorted(flow.data.keys())}")

        flow.status("saved", keys_after=sorted(flow.data.keys()))
        log("browser closed; done")
        return 0

    except (BudgetExceeded, StepFailed) as exc:
        return flow.fail(f"{type(exc).__name__}: {exc}")
    except Exception as exc:
        return flow.fail(f"{type(exc).__name__}: {exc}")


# --------------------------------------------------------------------------
# CLI: python flow.py <profileId> [--headless]
# --------------------------------------------------------------------------

def resolve_row(rest: list[str]) -> dict:
    """argv (flags stripped) -> the Agentrouter table row. Frozen messages."""
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
    return row


def split_flags(argv: list[str]) -> tuple[bool, list[str]]:
    """Pull --headless out; every other token stays an argument.

    Only the known flag is consumed, so a bad token still reaches safe_id and
    keeps its original refusal message.
    """
    headless = False
    rest: list[str] = []
    for arg in argv:
        if arg == "--headless":
            headless = True
        else:
            rest.append(arg)
    return headless, rest


def main(argv: list[str], headless: bool = False) -> int:
    """Run the claim flow for one profile.

    ``headless`` is literal: True hides the browser, False shows it. The pyhost
    command passes the UI toggle's value; the CLI defaults to visible.
    """
    flag_headless, rest = split_flags(argv)
    row = resolve_row(rest)

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    return run_inline(row, headless=headless or flag_headless)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
