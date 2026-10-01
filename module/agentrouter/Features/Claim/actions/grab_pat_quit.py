#!/usr/bin/env python3
r"""Step 4 + step 6 chain for one Agentrouter row: PAT then Quit.

Fixed order (owner's spec):
  1. click chip (account menu) -> menuitem Personal Settings  -> /console/personal
  2. click Security Settings section (lucide-shield-check div) -> reveal token area
  3. click Generate Token (semi-button-primary, key icon)     -> readonly input fills
  4. copy the readonly input (Ctrl+A/C, clipboard; input value fallback) -> save `pat`
  5. click chip -> menuitem Quit -> verify logged out
  explain = status json + screenshots + masked log.

PAT condition (owner's exact spec): profile JSON already holds `pat` -> skip
generation untouched, still run Quit.

Timeouts answer the standing question ("gak pasang time out?"):
  goto 60s | networkidle 15s best-effort | chip 30s | menu item 10s |
  route change 15s | input fill 20s | copy scroll 5s | quit land 15s |
  TOTAL budget 300s enforced at every step boundary.

Secrets: full PAT only in the profile JSON. stdout/status = masked.
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
PERSONAL_PATH = "/console/personal"

# Per-call timeouts (ms unless noted). The total budget below caps everything.
T_GOTO = 60_000
T_IDLE = 15_000
T_CHIP_S = 30
T_MENU_MS = 10_000
T_ROUTE_MS = 15_000
T_FILL_S = 20
T_QUIT_MS = 15_000
TOTAL_BUDGET_S = 300

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

# Owner-given element: <input class="semi-input semi-input-large" readonly ...>
_PAT_SEL = "input.semi-input.semi-input-large[readonly]"

_PAT_INPUTS = """
() => document.querySelectorAll('input.semi-input.semi-input-large[readonly]').length
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


class Chain:
    def __init__(self, profile_id: str, status_path: Path):
        self.profile_id = profile_id
        self.status_path = status_path
        self.deadline = time.time() + TOTAL_BUDGET_S
        self.stage = "starting"
        self.notes: list[str] = []

    def check(self, step: str) -> None:
        if time.time() > self.deadline:
            raise BudgetExceeded(f"total budget {TOTAL_BUDGET_S}s exceeded at {step}")

    def status(self, **extra) -> None:
        payload = {"stage": self.stage, "profile": self.profile_id, **extra}
        if self.notes:
            payload["notes"] = list(self.notes)
        write_status(self.status_path, payload)

    def fail(self, error: str) -> int:
        self.stage = "error"
        self.status(error=error)
        log(f"error: {error}")
        return 1


# mask_secret (from _lib.logging), wait_for (from _lib.browser) and
# profile_json_path (from _lib.paths) now come from open_agentrouter above.
# load_profile_json stays local ON PURPOSE: this one takes an explicit path and
# returns {} (not {"profile": id}) when the file is missing -- see _lib/creds.py.


def load_profile_json(path: Path, profile_id: str) -> dict:
    data: dict = {}
    if path.is_file():
        try:
            loaded = json.loads(path.read_text(encoding="utf-8-sig"))
            if isinstance(loaded, dict):
                data = loaded
        except json.JSONDecodeError:
            log("warning: profile json unreadable; rewriting from scratch")
    data.setdefault("profile", profile_id)
    return data


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
    status_path = OUT_DIR / f"pat_quit_{profile_id}.status.json"
    chain = Chain(profile_id, status_path)
    chain.status()
    json_path = profile_json_path(profile_id)
    shots = lambda name: str(OUT_DIR / f"pat_quit_{profile_id}_{name.removesuffix('.png')}.png")

    try:
        free_lock(row["profileDir"])

        from camoufox.sync_api import Camoufox

        chain.stage = "launching"
        chain.status(target=CONSOLE_URL)
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

            # --- step 1: Personal Settings ----------------------------------
            chain.check("chip")
            chips = wait_for(page, lambda: page.evaluate(_CHIPS), T_CHIP_S)
            if not chips:
                return chain.fail("github chip not found within 30s")
            log(f"chip={chips[0]}")

            chain.check("open menu")
            page.locator('button[aria-haspopup="true"]').filter(
                has_text=chips[0]).first.click()
            page.wait_for_timeout(800)

            chain.check("personal settings")
            # Owner-given element: the span INSIDE the dropdown item.
            # <span class="truncate font-medium text-sm"
            #       style="color: rgb(16, 185, 129);">Personal Settings</span>
            settings_cands = page.locator(
                'span.truncate.font-medium.text-sm',
                has_text="Personal Settings")
            settings_item = None
            for i in range(settings_cands.count()):
                if settings_cands.nth(i).is_visible():
                    settings_item = settings_cands.nth(i)
                    break
            if settings_item is None:
                page.screenshot(path=shots("menu.png"), full_page=False)
                return chain.fail(
                    "no visible Personal Settings span "
                    f"({settings_cands.count()} matches)")
            settings_item.click()
            page.wait_for_url(f"**{PERSONAL_PATH}**", timeout=T_ROUTE_MS)
            log(f"on personal page: {page.url}")

            # --- PAT conditional: already stored -> skip untouched -----------
            data = load_profile_json(json_path, profile_id)
            pat_saved = bool(data.get("pat"))
            if pat_saved:
                chain.notes.append("pat already in json -> generation skipped untouched")
                log("pat already stored -> skip generation")
                page.screenshot(path=shots("sec.png"), full_page=False)
            else:
                # --- step 2: Security Settings TAB (activate it) -------------------
                chain.check("security tab")
                tab = page.get_by_role("tab", name="Security Settings", exact=True)
                if tab.count() == 0:
                    # Semi Tabs fallback: tab item by class + text.
                    tab = page.locator(".semi-tabs-tab").filter(
                        has_text="Security Settings")
                tab.first.wait_for(state="visible", timeout=T_MENU_MS)
                tab.first.scroll_into_view_if_needed(timeout=5_000)
                tab.first.click()
                page.wait_for_timeout(1500)
                page.screenshot(path=shots("sec.png"), full_page=False)
                log("security tab activated")

                # --- step 3: Generate Token (first VISIBLE match) ---------------
                # Owner-given element:
                # <button class="semi-button semi-button-primary ...">
                #   ...<span class="semi-button-content-right">Generate Token</span>
                # Tab panels render hidden duplicates; .first can be a hidden
                # copy and wait visible on it times out forever. Iterate.
                chain.check("generate token")
                cands = page.locator(
                    "button.semi-button-primary", has_text="Generate Token")
                gen = None
                for i in range(cands.count()):
                    if cands.nth(i).is_visible():
                        gen = cands.nth(i)
                        break
                if gen is None:
                    page.screenshot(path=shots("pat.png"), full_page=False)
                    return chain.fail(
                        "no visible Generate Token button "
                        f"({cands.count()} hidden/non-visible matches)")
                gen.scroll_into_view_if_needed(timeout=5_000)
                gen.click()
                log(f"generate token clicked ({cands.count()} candidates)")

                # --- step 4: copy readonly input -> save --------------------
                chain.check("token fill")
                values = wait_for(page, lambda: page.evaluate(_PAT_VALUES), T_FILL_S)
                if not values:
                    page.screenshot(path=shots("pat.png"), full_page=False)
                    return chain.fail(
                        "readonly PAT input stayed empty 20s after Generate Token "
                        "(possible confirm modal -> see pat.png)")
                log(f"PAT input filled: {mask_secret(values[0])} (candidates={len(values)})")

                first = page.locator(_PAT_SEL).first
                first.scroll_into_view_if_needed(timeout=5_000)
                first.click()
                page.wait_for_timeout(400)
                hotkey = "Meta+A" if sys.platform == "darwin" else "Control+A"
                copykey = "Meta+C" if sys.platform == "darwin" else "Control+C"
                page.keyboard.press(hotkey)
                page.keyboard.press(copykey)
                page.wait_for_timeout(400)
                try:
                    clip = page.evaluate("navigator.clipboard.readText()")
                except Exception:
                    clip = None

                value = values[0] if not (clip or "").strip() else clip.strip()
                source = "input" if value == values[0] else "clipboard"
                if not value:
                    page.screenshot(path=shots("pat.png"), full_page=False)
                    return chain.fail("PAT value empty after copy (input + clipboard both empty)")

                page.screenshot(path=shots("pat.png"), full_page=False)
                data["pat"] = value
                data["updated_at"] = datetime.now(timezone.utc).isoformat(timespec="seconds")
                json_path.write_text(json.dumps(data, indent=2), encoding="utf-8")
                chain.notes.append(f"pat saved via {source}")
                log(f"saved pat ({mask_secret(value)}, via {source}) to {json_path.name}")

            # --- step 5: Quit ------------------------------------------------
            chain.check("quit menu")
            page.goto(CONSOLE_URL, wait_until="domcontentloaded", timeout=T_GOTO)
            page.wait_for_timeout(1200)
            chips2 = wait_for(page, lambda: page.evaluate(_CHIPS), T_CHIP_S)
            logged_out = not chips2
            final_url = page.url
            quit_shot = shots("quit.png")

            if not logged_out:
                # Re-open the menu: visible trigger only; verify the menu
                # actually opened ([role=menu] visible); retry the click once.
                trig_cands = page.locator(
                    'button[aria-haspopup="true"]', has_text=chips2[0])
                trigger = None
                for i in range(trig_cands.count()):
                    if trig_cands.nth(i).is_visible():
                        trigger = trig_cands.nth(i)
                        break
                if trigger is None:
                    page.screenshot(path=quit_shot, full_page=False)
                    return chain.fail(
                        "no visible account-menu trigger on revisit "
                        f"({trig_cands.count()} matches)")
                trigger.click()
                page.wait_for_timeout(800)
                # "Open" = the account dropdown's text is visible. The real
                # dropdown carries no [role=menu] (that matched another
                # container), so verify by its own items, page-wide.
                def _quit_visible() -> bool:
                    c = page.get_by_text("Quit", exact=True)
                    for i in range(c.count()):
                        try:
                            if c.nth(i).is_visible():
                                return True
                        except Exception:
                            pass
                    return False

                menu_open = wait_for(page, _quit_visible, 3)
                if not menu_open:
                    log("menu text not visible after click; retrying trigger once")
                    trigger.click()
                    page.wait_for_timeout(800)
                    menu_open = wait_for(page, _quit_visible, 3)
                if not menu_open:
                    page.screenshot(path=quit_shot, full_page=False)
                    return chain.fail("account menu did not open on revisit")
                # Items are plain divs in this render (role query finds 0);
                # the open menu CONTAINS visible text "Quit" (see quit.png).
                quit_cands = page.get_by_text("Quit", exact=True)
                quit_item = None
                for i in range(quit_cands.count()):
                    try:
                        if quit_cands.nth(i).is_visible():
                            quit_item = quit_cands.nth(i)
                            break
                    except Exception:
                        pass
                if quit_item is None:
                    page.screenshot(path=quit_shot, full_page=False)
                    return chain.fail(
                        "menu open but no visible Quit text "
                        f"({quit_cands.count()} matches)")
                quit_item.click()
                page.wait_for_timeout(2000)
                try:
                    page.wait_for_load_state("networkidle", timeout=T_IDLE)
                except Exception:
                    pass
                final_url = page.url
                chips3 = page.evaluate(_CHIPS)
                logged_out = (not chips3) or ("/login" in final_url.lower())
                log(f"quit clicked -> url={final_url} chip_gone={not chips3}")

            page.screenshot(path=quit_shot, full_page=False)
            chain.stage = "saved"
            chain.status(
                skipped_pat=pat_saved,
                pat_masked=(mask_secret(data["pat"]) if data.get("pat") else None),
                final_url=final_url,
                logged_out=logged_out,
                saved_to=str(json_path),
                keys_after=sorted(data.keys()),
                screenshots=[shots("sec.png"), shots("pat.png"), quit_shot],
            )
            log(f"quit done: logged_out={logged_out} url={final_url}")

        chain.stage = "closed"
        chain.status()
        log("browser closed; done")
        return 0

    except BudgetExceeded as exc:
        return chain.fail(str(exc))
    except Exception as exc:
        return chain.fail(f"{type(exc).__name__}: {exc}")


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
