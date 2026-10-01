#!/usr/bin/env python3
r"""Combined 1x daily flow, ONE browser lifecycle (owner ordered: combine,
do NOT touch the finished piece files).

Same 7 steps as flow_1x.py, same proven selectors copied from the green
pieces (grab_api_key / grab_pat / quit_session / github_signin --click). Those
pieces now live in actions/ with a thin CLI shim over each; this file is the
fast path.

  1. goto /login -> chip?
  2. chip PRESENT  -> quit-inline (max 3 tries, 2.5s settle between)
  3. chip ABSENT confirmed -> login-inline (Sign in, never Console;
     Continue with GitHub visible-first; popup completes untouched;
     opener reloaded; chip_after proof or STOP)
  4. JSON api_key? exists -> SKIP | empty -> capture-inline
     (menu API Token -> /console/token -> sk- input -> copy-icon path
     d="M7 4c0-..." -> clipboard/input -> save)
  5. JSON pat? exists -> SKIP | empty -> capture-inline
     (menu Personal Settings emerald span -> Security tab ->
     Generate Token visible-first -> readonly input fills ->
     Ctrl+A/C -> save)
  6. quit-inline final -> verify /login + chip gone
  7. browser closed. End state deterministic: logged out.

Two strategies share this file, chosen on the CLI (--inline / --subprocess):

  * InlineStrategy     -- default for flow_1x_single.py: ONE browser, the
                          seven steps inline (the fast path).
  * SubprocessStrategy -- default for flow_1x.py: one script + one browser
                          per step, spawned from %TEMP%\opencode (the
                          fail-fast, diagnosable path; budget 1500s).

The state-changing steps live in actions/ (quit_session, github_signin,
grab_api_key, grab_pat) and the top-level file of each name is a thin CLI shim
onto actions.<name>.main. Shared primitives live in _lib/.

Timeouts: goto 60s | idle 15s best-effort | chip 15s | menu item 10s |
          route 15s | fill 20s | scroll 5s | popup settle 30s |
          TOTAL budget 600s enforced per boundary.
Secrets: full values only in profile JSON; stdout/status masked.
Runtime: Citadel venv python only.
"""

from __future__ import annotations

import re
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

from _lib import (
    OUT_DIR, RUNTIME_PY, SESSION_LIVE, free_lock, load_profile_json, log,
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

    def open_menu(self, chip: str) -> None:
        """Click the visible account-menu trigger; prove open by Quit text."""
        if not chip:
            raise StepFailed("open_menu: chip absent, session dead")
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


def run_inline(row: dict) -> int:
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
            headless=False,
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
# SubprocessStrategy -- the flow_1x behaviour: every step is its own script
# and its own browser launch, spawned with the Citadel venv python. The step
# scripts are resolved from OUT_DIR (%TEMP%\opencode) exactly as before, so
# deploy must place them (plus _lib/ and open_agentrouter.py) there.
# --------------------------------------------------------------------------

STEP_TIMEOUT_S = 300        # per spawned step (flow_1x value)
SUB_TOTAL_BUDGET_S = 1500   # whole subprocess flow (flow_1x value)
SUB_T_CHIP_S = 10           # flow_1x waited 10s (inline waits 15s) -- local

# The Claim folder that holds this file (and the step scripts beside it).
BIN_DIR = Path(__file__).resolve().parent


class SubprocessStrategy:
    """One script + one browser per step; proofs come from the status files."""

    def __init__(self, profile_id: str, profile_dir: str):
        self.profile_id = profile_id
        self.profile_dir = profile_dir
        self.status_path = OUT_DIR / f"flow_1x_{profile_id}.status.json"
        self.deadline = time.time() + SUB_TOTAL_BUDGET_S
        self.steps: list[dict] = []

    def check(self, step: str) -> None:
        if time.time() > self.deadline:
            raise BudgetExceeded(
                f"total budget {SUB_TOTAL_BUDGET_S}s exceeded at {step}")

    # Unlike InlineStrategy.record(), a failure here is NOT raised: the caller
    # decides whether that step ends the chain (flow_1x precedent).
    def record(self, name: str, ok: bool, detail: str = "") -> None:
        self.steps.append({"step": name, "ok": ok, "detail": detail,
                           "at": datetime.now(timezone.utc).isoformat(
                               timespec="seconds")})
        log(f"[{name}] {'ok' if ok else 'FAIL'} {detail}")

    def status(self, stage: str, **extra) -> None:
        write_status(self.status_path,
                     {"stage": stage, "profile": self.profile_id,
                      "steps": list(self.steps), **extra})

    def fail(self, error: str) -> int:
        self.status("error", error=error)
        log(f"error: {error}")
        return 1

    def read_chip(self) -> tuple[str | None, str]:
        """Quick check-launch: visit /login, read chip, close. (chip, url)."""
        self.check("read_chip")
        free_lock(self.profile_dir)
        from camoufox.sync_api import Camoufox
        with Camoufox(
            headless=False,
            persistent_context=True,
            user_data_dir=self.profile_dir,
            humanize=True,
            os="windows",
        ) as context:
            pages = list(context.pages)
            page = pages[0] if pages else context.new_page()
            page.goto(LOGIN_URL, wait_until="domcontentloaded", timeout=T_GOTO)
            try:
                page.wait_for_load_state("networkidle", timeout=T_IDLE)
            except Exception:
                pass
            chips = wait_for(page, lambda: page.evaluate(_CHIPS),
                             SUB_T_CHIP_S)
            return (chips[0] if chips else None, page.url)

    def step_script(self, script: str) -> Path:
        """Resolve one step script.

        The deploy copies the step shims (plus _lib/ and actions/) into
        OUT_DIR, and that copy always wins -- the historical working path.
        When it is absent (running the flow straight from the repo) fall back
        to the Claim folder holding this file, where the same scripts live.
        """
        deployed = OUT_DIR / script
        if deployed.is_file():
            return deployed
        return BIN_DIR / script

    def run_step(self, name: str, script: str,
                 args: list[str] | None = None) -> None:
        """Spawn one step script and wait. Raises StepFailed/BudgetExceeded."""
        self.check(name)
        script_path = self.step_script(script)
        out_path = OUT_DIR / f"flow_1x_{self.profile_id}_{name}.out"
        cmd = [str(RUNTIME_PY), str(script_path),
               *(args or [self.profile_id])]
        log(f"--- spawn {name}: "
            f"{' '.join([script, *(args or [self.profile_id])])} ---")
        try:
            proc = subprocess.run(
                cmd, capture_output=True, text=True, timeout=STEP_TIMEOUT_S,
                cwd=str(OUT_DIR), check=False,
            )
        except subprocess.TimeoutExpired:
            raise BudgetExceeded(f"step {name} exceeded {STEP_TIMEOUT_S}s")
        out_path.write_text(
            (proc.stdout or "") + "\n--- stderr ---\n" + (proc.stderr or ""),
            encoding="utf-8")
        if proc.returncode != 0:
            tail = (proc.stdout or "").strip().splitlines()[-3:]
            raise StepFailed(f"step {name} exit={proc.returncode}: "
                             + " | ".join(tail))


def run_subprocess(row: dict) -> int:
    """flow_1x: reset -> fresh login -> api_key -> pat -> final quit."""
    profile_id = row["profileId"]
    flow = SubprocessStrategy(profile_id, row["profileDir"])
    flow.status("starting")

    try:
        # --- steps 1-3: reset loop -------------------------------------
        chip, url = flow.read_chip()
        flow.record("check-login", True, f"chip={chip or 'absent'} url={url}")
        tries = 0
        while chip and tries < RESET_TRIES:
            tries += 1
            flow.run_step(f"reset-quit-{tries}", "quit_session.py")
            st = read_status("quit", profile_id)
            ok_quit = st.get("stage") in ("saved", "closed") and (
                st.get("logged_out") is True
                or "already logged out" in str(st.get("note", "")))
            if not ok_quit:
                flow.record(f"reset-quit-{tries}", False,
                            f"no logout proof: {st.get('error')}")
                return flow.fail(
                    f"reset quit try {tries} has no logout proof")
            flow.record(f"reset-quit-{tries}", True,
                        f"logged_out={st.get('logged_out')}")
            time.sleep(SETTLE_S)
            chip, url = flow.read_chip()
            flow.record("recheck-login", True,
                        f"chip={chip or 'absent'} url={url}")
        if chip:
            flow.record("reset-loop", False,
                        f"chip still present after {RESET_TRIES} quits")
            return flow.fail("session survived 3 quits; refusing login on "
                             "a live session")
        flow.record("reset-loop", True, "logout confirmed")

        # --- step 4: fresh login ---------------------------------------
        # args REPLACE the default [profile_id]: flags must come with it.
        flow.run_step("login", "github_signin.py", ["--click", profile_id])
        st = read_status("github_signin", profile_id)
        if st.get("stage") not in ("clicked", "closed") or not st.get("chip_after"):
            flow.record("login", False,
                        f"no login proof: chip_after={st.get('chip_after')} "
                        f"err={st.get('error')}")
            return flow.fail("login has no chip proof; capture would run "
                             "on a dead session")
        flow.record("login", True,
                    f"chip_after={st.get('chip_after')} "
                    f"url={st.get('url_after')}")

        # --- step 5: api_key -------------------------------------------
        data = load_profile_json(profile_id)
        if data.get("api_key"):
            flow.record("api_key", True, "exists -> SKIP")
        else:
            flow.run_step("capture-api", "grab_api_key.py")
            data = load_profile_json(profile_id)
            if not data.get("api_key"):
                flow.record("capture-api", False, "api_key still empty")
                return flow.fail("api_key capture produced nothing")
            flow.record("capture-api", True, "saved")

        # --- step 6: pat ------------------------------------------------
        data = load_profile_json(profile_id)
        if data.get("pat"):
            flow.record("pat", True, "exists -> SKIP")
        else:
            flow.run_step("capture-pat", "grab_pat.py")
            data = load_profile_json(profile_id)
            if not data.get("pat"):
                flow.record("capture-pat", False, "pat still empty")
                return flow.fail("pat capture produced nothing")
            flow.record("capture-pat", True, "saved")

        # --- step 7: final quit -----------------------------------------
        flow.run_step("final-quit", "quit_session.py")
        st = read_status("quit", profile_id)
        chip, url = flow.read_chip()
        if chip:
            flow.record("final-quit", False,
                        f"chip still present: {chip} url={url}")
            return flow.fail("final quit did not end the session")
        flow.record("final-quit", True, f"logged_out url={url}")
        data = load_profile_json(profile_id)

        flow.status("saved", keys_after=sorted(data.keys()))
        log(f"1x flow done: keys={sorted(data.keys())}")
        return 0

    except (BudgetExceeded, StepFailed) as exc:
        return flow.fail(f"{type(exc).__name__}: {exc}")
    except Exception as exc:
        return flow.fail(f"{type(exc).__name__}: {exc}")


# --------------------------------------------------------------------------
# CLI: ONE entry point, pluggable strategy.
#   flow_1x_single.py -> default "inline"     (one browser, inline steps)
#   flow_1x.py        -> default "subprocess" (script + browser per step)
# --inline / --subprocess override either way.
# --------------------------------------------------------------------------

def _resolve_strategy(argv: list[str], default: str) -> tuple[str, list[str]]:
    strategy = default
    rest: list[str] = []
    for arg in argv:
        if arg == "--inline":
            strategy = "inline"
        elif arg == "--subprocess":
            strategy = "subprocess"
        else:
            rest.append(arg)
    return strategy, rest


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


def main(argv: list[str], default_strategy: str = "inline") -> int:
    strategy, rest = _resolve_strategy(argv, default_strategy)
    row = resolve_row(rest)

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    if strategy == "subprocess":
        return run_subprocess(row)
    return run_inline(row)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
