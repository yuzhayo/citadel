#!/usr/bin/env python3
"""Camoufox launch ritual, the profile lock-freeing, and small page helpers
shared by every script.

The launch kwargs are frozen (persistent context, humanize, os="windows");
``headless`` defaults to False so behaviour matches every existing script -- the
C# "Show browser while claiming" toggle will pass ``headless=True`` later.
"""

from __future__ import annotations

import re
import subprocess
import time
from contextlib import contextmanager

from .logging import log

_LOGIN_MARKERS = ("/login", "/signin", "/sign-in", "/auth")
_CONSOLE_MARKERS = ("/console", "/dashboard")

# Raw string on purpose: the JS regex must receive a single backslash (\d).
CHIPS_JS = r"""
() => {
  const out = [];
  document.querySelectorAll('span').forEach((el) => {
    const t = (el.textContent || '').trim();
    if (/^github_\d+$/.test(t)) out.push(t);
  });
  return out;
}
"""


def free_lock(profile_dir: str) -> None:
    """Kill leftovers holding THIS profile dir. Targeted: never Chrome, never
    a different profile. One browser per profile or the user-data-dir lock stays."""
    marker = profile_dir.replace("'", "''")
    script = (
        "Get-CimInstance Win32_Process | Where-Object { "
        f"$_.CommandLine -and $_.CommandLine.Contains('{marker}') -and "
        "$_.Name -match 'camoufox|firefox|geckodriver|python' } | "
        "ForEach-Object { $_.ProcessId }"
    )
    try:
        output = subprocess.run(
            ["powershell", "-NoProfile", "-Command", script],
            capture_output=True, text=True, timeout=30, check=False,
        ).stdout
    except (OSError, subprocess.TimeoutExpired):
        return

    pids = [int(token) for token in re.findall(r"\d+", output)]
    if not pids:
        return
    log(f"freeing profile lock: killing {len(pids)} leftover process(es): {pids}")
    for pid in pids:
        subprocess.run(
            ["powershell", "-NoProfile", "-Command",
             f"Stop-Process -Id {pid} -Force -ErrorAction SilentlyContinue"],
            capture_output=True, timeout=20, check=False,
        )
    time.sleep(1.5)


def classify(url: str) -> str:
    lowered = url.lower()
    if any(marker in lowered for marker in _LOGIN_MARKERS):
        return "login"
    if any(marker in lowered for marker in _CONSOLE_MARKERS):
        return "console"
    return "other"


@contextmanager
def launch(profile_dir: str, *, headless: bool = False):
    """Open the persistent Camoufox profile; yield ``(context, page)``.

    Caller must run free_lock(profile_dir) first (one browser per profile).
    """
    from camoufox.sync_api import Camoufox

    with Camoufox(
        headless=headless,
        persistent_context=True,
        user_data_dir=str(profile_dir),
        humanize=True,
        os="windows",
    ) as context:
        pages = list(context.pages)
        page = pages[0] if pages else context.new_page()
        yield context, page


def wait_for(page, predicate, timeout_s: float, interval_s: float = 1.0):
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        value = predicate()
        if value:
            return value
        page.wait_for_timeout(int(interval_s * 1000))
    return None


def visible_first(page, locator):
    """First visible match of a locator, or None (hidden duplicates exist)."""
    for i in range(locator.count()):
        try:
            if locator.nth(i).is_visible():
                return locator.nth(i)
        except Exception:
            pass
    return None


def visible_texts(page, text: str) -> list:
    cands = page.get_by_text(text, exact=True)
    out = []
    for i in range(cands.count()):
        try:
            if cands.nth(i).is_visible():
                out.append(cands.nth(i))
        except Exception:
            pass
    return out


def read_chips(page, timeout_s: float) -> list[str]:
    """Wait up to ``timeout_s`` for the ``github_<id>`` chip; [] when absent."""
    return wait_for(page, lambda: page.evaluate(CHIPS_JS), timeout_s) or []


# One round trip that answers "is a session live?" AND "is this an explicit
# logged-out page?" -- so confirming a logout costs one poll, not a timeout.
SESSION_JS = r"""
() => {
  const chip = [...document.querySelectorAll('span')]
    .map((el) => (el.textContent || '').trim())
    .find((t) => /^github_\d+$/.test(t)) || null;
  const path = (location.pathname || '').toLowerCase();
  const onLogin = ['/login', '/signin', '/sign-in', '/auth']
    .some((m) => path.includes(m));
  const signIn = [...document.querySelectorAll('a,button')]
    .some((el) => /sign.?in|log.?in/i.test((el.textContent || '').trim()));
  return { chip: chip, onLogin: onLogin, signIn: signIn, href: location.href };
}
"""

SESSION_LIVE = "live"        # a github_<id> chip is present -> session alive
LOGGED_OUT = "out"           # explicit logged-out marker -> session dead
SESSION_UNKNOWN = "unknown"  # neither, and the timeout ran out


def session_state(page, timeout_s: float = 15.0, interval_s: float = 0.5,
                  confirm_out: int = 3):
    """Conditional session verifier -> ``(state, info)``.

    ``state`` is ``SESSION_LIVE`` when a ``github_<id>`` chip is present,
    ``LOGGED_OUT`` when the page says logged out (a login route, or a visible
    Sign in affordance), else ``SESSION_UNKNOWN`` once ``timeout_s`` runs out.

    "live" is returned on the first poll that sees the chip. "out" must be
    seen ``confirm_out`` times in a row first -- NOT an accident: the live site
    serves ``/login`` WITH the chip present on a signed-in session, so a chip
    that is still rendering would otherwise be mistaken for a logout.

    This is the whole point of the function. ``wait_for`` only stops on a
    TRUTHY value, so an empty chip list made every "confirm logged out" check
    burn its entire timeout; here "out" is a first-class answer, costing
    ``confirm_out`` polls instead. ``info`` always carries ``chip``/``url``
    for logging and for the account-menu trigger.
    """
    deadline = time.time() + timeout_s
    info: dict = {"chip": None, "onLogin": False, "signIn": False, "url": ""}
    out_streak = 0
    while True:
        try:
            observed = page.evaluate(SESSION_JS)
        except Exception:
            observed = None
        if isinstance(observed, dict):
            info = {
                "chip": observed.get("chip") or None,
                "onLogin": bool(observed.get("onLogin")),
                "signIn": bool(observed.get("signIn")),
                "url": observed.get("href") or str(getattr(page, "url", "") or ""),
            }
        else:
            info = {**info, "url": str(getattr(page, "url", "") or "")}
        if info["chip"]:
            return SESSION_LIVE, info
        if info["onLogin"] or info["signIn"]:
            out_streak += 1
            if out_streak >= confirm_out:
                return LOGGED_OUT, info
        else:
            out_streak = 0
        if time.time() >= deadline:
            return SESSION_UNKNOWN, info
        page.wait_for_timeout(int(interval_s * 1000))
