"""Mechanisms for the Agentrouter claim flow: paths, ids, logging, status,
the shortcut table, the per-profile credential file, timeouts, and the Camoufox
launch/session helpers.

One module, one consumer (flow.py). Paths come from Citadel's own data folder,
never from the caller's environment or working directory.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path


# ----------------------------------------------------------------------
# from _lib/paths.py
# ----------------------------------------------------------------------


# -- vault / store roots -----------------------------------------------------
CITADEL = Path(os.environ["LOCALAPPDATA"]) / "Citadel"
SHORTCUTS_JSON = CITADEL / "agentrouter" / "shortcuts.json"
PROFILES_ROOT = CITADEL / "Credenz" / "google" / "profiles"
ACCOUNTS_ROOT = CITADEL / "Credenz" / "google" / "accounts"

# Status files, screenshots and the shared log for every claim run.
OUT_DIR = CITADEL / "agentrouter" / "runs"

# -- agentrouter service -----------------------------------------------------
TARGET_URL = "https://agentrouter.org"
HOME_URL = TARGET_URL
LOGIN_URL = HOME_URL + "/login"
CONSOLE_URL = HOME_URL + "/console"
TOKEN_URL = HOME_URL + "/console/token"
PERSONAL_PATH = "/console/personal"  # route; join to HOME_URL when a full URL is needed


def profile_json_path(profile_id: str) -> Path:
    """Per-profile credential store: <OUT_DIR>/agentrouter-<id>.json."""
    return OUT_DIR / f"agentrouter-{profile_id}.json"


def status_path(kind: str, profile_id: str) -> Path:
    """Status file for one step: ``<kind>_<profileId>.status.json`` (frozen).

    e.g. ``status_path("quit", pid)``        -> ``quit_<pid>.status.json``
         ``status_path("flow_1x_single", pid)`` -> ``flow_1x_single_<pid>.status.json``
    """
    return OUT_DIR / f"{kind}_{profile_id}.status.json"


# ----------------------------------------------------------------------
# from _lib/ids.py
# ----------------------------------------------------------------------


SAFE_ID = re.compile(r"^[A-Za-z0-9._-]+$")


def safe_id(profile_id: str) -> str:
    if not SAFE_ID.match(profile_id) or profile_id in (".", ".."):
        raise SystemExit(
            f"refused: profile id is not a safe path segment: {profile_id!r}")
    return profile_id


# ----------------------------------------------------------------------
# from _lib/logging.py
# ----------------------------------------------------------------------


LOG_FILE = OUT_DIR / "open_agentrouter.log"


def log(message: str) -> None:
    stamp = datetime.now().strftime("%H:%M:%S")
    line = f"[{stamp}] {message}"
    try:
        print(line, flush=True)
    except UnicodeEncodeError:
        # Redirected stdout under a CJK-blind locale (cp1252): bypass it.
        sys.stdout.buffer.write((line + "\n").encode("utf-8", "replace"))
        sys.stdout.buffer.flush()
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    with open(LOG_FILE, "a", encoding="utf-8", errors="replace") as handle:
        handle.write(line + "\n")


def mask_secret(value: str) -> str:
    """sk-lvh...EHjr style -- never the full value in stdout."""
    if not value:
        return "—"
    if len(value) <= 8:
        return "•" * len(value)
    return f"{value[:4]}...{value[-4:]}"


# ----------------------------------------------------------------------
# from _lib/status.py
# ----------------------------------------------------------------------


def write_status(path: Path, payload: dict) -> None:
    payload = {"written_at": datetime.now(timezone.utc).isoformat(), **payload}
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")


def read_status(kind: str, profile_id: str) -> dict:
    """Latest ``<kind>_<profileId>.status.json``, or {} when missing/unreadable."""
    for candidate in OUT_DIR.glob(f"{kind}_{profile_id}.status.json"):
        try:
            loaded = json.loads(candidate.read_text(encoding="utf-8"))
            if isinstance(loaded, dict):
                return loaded
        except json.JSONDecodeError:
            pass
    return {}


# ----------------------------------------------------------------------
# from _lib/table.py
# ----------------------------------------------------------------------


def display_name(profile_id: str) -> str | None:
    """identity.json email; case-insensitive read, None when absent/corrupt."""
    path = ACCOUNTS_ROOT / profile_id / "identity.json"
    if not path.is_file():
        return None
    try:
        record = json.loads(path.read_text(encoding="utf-8-sig"))
    except (json.JSONDecodeError, OSError):
        return None
    if not isinstance(record, dict):
        return None
    for key, value in record.items():
        if key.lower() == "email" and isinstance(value, str) and value.strip():
            return value.strip()
    return None


def read_table() -> list[dict]:
    """Rows exactly as the Agentrouter Launcher tab would present them."""
    if not SHORTCUTS_JSON.exists():
        raise SystemExit(f"table store missing: {SHORTCUTS_JSON}")

    try:
        document = json.loads(SHORTCUTS_JSON.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError as exc:
        raise SystemExit(f"table store unreadable: {exc}")

    if not isinstance(document, dict) or document.get("schema") != 1:
        raise SystemExit(
            f"table store schema not 1: {document.get('schema')!r}")

    rows = []
    for entry in document.get("profiles") or []:
        profile_id = str(entry.get("profileId") or "")
        if not SAFE_ID.match(profile_id) or profile_id in (".", ".."):
            continue  # ShortcutCatalog drops these too
        added = str(entry.get("addedAtUtc") or "")
        folder = PROFILES_ROOT / profile_id
        rows.append({
            "profileId": profile_id,
            "exists": folder.is_dir(),
            "account": display_name(profile_id) or profile_id,
            "added": added,
            "profileDir": str(folder),
        })
    return sorted(rows, key=lambda row: row["account"].lower())


def print_table(rows: list[dict]) -> None:
    if not rows:
        log("table empty — no profiles added in Agentrouter")
        return
    log(f"table: {len(rows)} row(s)")
    for row in rows:
        status = "Ready" if row["exists"] else "Missing"
        log(f'  {row["account"]:<34} {status:<8} {row["profileId"]}')


# ----------------------------------------------------------------------
# from _lib/creds.py
# ----------------------------------------------------------------------


def load_profile_json(profile_id: str, *, ensure_profile: bool = False) -> dict:
    path = profile_json_path(profile_id)
    if path.is_file():
        try:
            loaded = json.loads(path.read_text(encoding="utf-8-sig"))
            if isinstance(loaded, dict):
                if ensure_profile:
                    loaded.setdefault("profile", profile_id)
                return loaded
        except json.JSONDecodeError:
            log("warning: profile json unreadable; rewriting from scratch")
    return {"profile": profile_id}


def save_profile_json(profile_id: str, data: dict) -> None:
    # Keep the run schema stable from the first credential write. ``null`` is
    # an explicit not-yet-fetched balance, not a fabricated numeric value;
    # the C# reader treats incomplete balance blocks as unavailable.
    data.setdefault("balance", None)
    data["updated_at"] = datetime.now(timezone.utc).isoformat(
        timespec="seconds")
    profile_json_path(profile_id).write_text(
        json.dumps(data, indent=2), encoding="utf-8")


# ----------------------------------------------------------------------
# from _lib/timeouts.py
# ----------------------------------------------------------------------


T_GOTO = 60_000       # page.goto(..., wait_until="domcontentloaded")
T_IDLE = 15_000       # page.wait_for_load_state("networkidle") -- best effort
T_MENU_MS = 10_000    # click a menu item / tab
T_ROUTE_MS = 15_000   # page.wait_for_url after a menu route change
T_FILL_S = 20         # wait for a generated value to appear in an input


# ----------------------------------------------------------------------
# from _lib/browser.py
# ----------------------------------------------------------------------


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


# The daily "System Notice" is a Semi modal (role=dialog, aria-modal=true).
# While it is up it EATS clicks aimed at the page behind it, so it can break
# any step that has to click. One evaluate finds the smallest visible element
# whose exact text is "Close Today" (the inner span, which bubbles to its
# button) and clicks it -- the same race-free shape as the Quit click.
#
# Only "Close Today" is pressed. The header X dismisses for the session only,
# so the notice would simply come back.
NOTICE_JS = r"""
() => {
  const visible = (el) => {
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0;
  };
  const dialogs = [...document.querySelectorAll(
      'div[role="dialog"][aria-modal="true"], div.semi-modal-content'
    )].filter((el) => visible(el) && (el.textContent || '').includes('Close Today'));
  if (dialogs.length === 0) return { present: false, dismissed: false };
  let best = null;
  let bestArea = Infinity;
  for (const el of dialogs[0].querySelectorAll('button, span, div')) {
    if ((el.textContent || '').trim() !== 'Close Today') continue;
    if (!visible(el)) continue;
    const r = el.getBoundingClientRect();
    const area = r.width * r.height;
    if (area < bestArea) { best = el; bestArea = area; }
  }
  if (!best) return { present: true, dismissed: false };
  best.click();
  return { present: true, dismissed: true };
}
"""


def dismiss_notice(page, timeout_s: float = 3.0) -> str:
    """Click the daily System Notice's "Close Today" when it is up.

    Returns ``"none"`` (nothing was there -- one evaluate, no waiting),
    ``"dismissed"``, or ``"stuck"`` when the modal is up but its button never
    appeared within ``timeout_s``.

    Called before the first interaction on a page, because a fresh page load
    can bring the notice back.
    """
    deadline = time.time() + timeout_s
    while True:
        try:
            seen = page.evaluate(NOTICE_JS)
        except Exception:
            seen = None
        if not isinstance(seen, dict) or not seen.get("present"):
            return "none"
        if seen.get("dismissed"):
            return "dismissed"
        if time.time() >= deadline:
            return "stuck"
        page.wait_for_timeout(250)


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
