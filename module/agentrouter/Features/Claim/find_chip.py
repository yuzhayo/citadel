#!/usr/bin/env python3
r"""Locate the `github_258838` chip LIVE inside the opened Camoufox profile.

Reuses the profile lock-freeing + launch pattern from open_agentrouter.py,
then evaluates DOM against the real signed-in session:
  1. find every element whose text is exactly `github_258838`
  2. dump its ancestry chain, parent/grandparent HTML, cursor, box
  3. click the chip (account menu trigger) and dump the menu that opens
  4. screenshot + write find_chip_<profileId>.status.json

Runtime: Citadel venv python only. Secrets stay masked in stdout.
"""

from __future__ import annotations

import json
import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

from open_agentrouter import (  # table read, id guard, lock free, status writer
    OUT_DIR, TARGET_URL, free_lock, log, read_table, safe_id, write_status,
)

CONSOLE_URL = "https://agentrouter.org/console"
WANT_TEXT = "github_258838"

_FINDER = """
(text) => {
  const hits = [];
  document.querySelectorAll('span,div,a,button').forEach((el) => {
    if ((el.textContent || '').trim() === text) hits.push(el);
  });
  const box = (r) => ({x: Math.round(r.x), y: Math.round(r.y),
                       w: Math.round(r.width), h: Math.round(r.height)});
  return hits.map((el) => {
    const chain = [];
    let cur = el;
    while (cur && cur !== document.body) {
      const cls = (typeof cur.className === 'string' && cur.className.trim())
        ? '.' + cur.className.trim().split(/\\s+/).slice(0, 4).join('.')
        : '';
      chain.push(cur.tagName.toLowerCase() + cls);
      cur = cur.parentElement;
    }
    const parent = el.parentElement;
    const grand = parent && parent.parentElement;
    return {
      tag: el.tagName,
      cls: String(el.className || ''),
      role: el.getAttribute('role'),
      aria: el.getAttribute('aria-haspopup') || el.getAttribute('aria-expanded'),
      box: box(el.getBoundingClientRect()),
      cursor: parent ? getComputedStyle(parent).cursor : null,
      chain: chain,
      parentHtml: parent ? parent.outerHTML.slice(0, 2000) : null,
      grandHtml: grand ? grand.outerHTML.slice(0, 3000) : null,
    };
  });
}
"""

_MENU = """
() => {
  const items = [];
  document.querySelectorAll('[role="menuitem"],[role="option"],li').forEach((el) => {
    const t = (el.textContent || '').trim();
    if (t && t.length < 60) items.push({tag: el.tagName, role: el.getAttribute('role'), text: t});
  });
  const seen = new Set();
  return items.filter((i) => (seen.has(i.text) ? false : (seen.add(i.text), true))).slice(0, 40);
}
"""


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
    status_path = OUT_DIR / f"find_chip_{profile_id}.status.json"
    write_status(status_path, {"stage": "starting", "profile": profile_id})

    log(f'opening profile row: {row["account"]} ({profile_id})')
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
        title = page.title()
        state = "login" if "/login" in url.lower() else ("console" if "/console" in url.lower() else "other")
        log(f"page: url={url} title={title!r} state={state}")

        # Wait up to 20s for the chip to appear (session may redirect).
        hits = []
        deadline = time.time() + 20
        while time.time() < deadline:
            hits = page.evaluate(_FINDER, WANT_TEXT)
            if hits:
                break
            page.wait_for_timeout(1000)

        shot = OUT_DIR / f"find_chip_{profile_id}.png"
        page.screenshot(path=str(shot), full_page=False)

        report = {
            "stage": "ready", "profile": profile_id, "account": row["account"],
            "url": url, "title": title, "state": state,
            "chip_found": bool(hits), "chip_count": len(hits),
            "hits": hits, "screenshot": str(shot),
        }

        # Chip is the account menu trigger: open it and dump what's inside.
        if hits:
            try:
                box = hits[0]["box"]
                page.mouse.click(box["x"] + box["w"] / 2, box["y"] + box["h"] / 2)
                page.wait_for_timeout(1200)
                report["menu_items"] = page.evaluate(_MENU)
                report["url_after_click"] = page.url
                page.screenshot(path=str(OUT_DIR / f"find_chip_{profile_id}_menu.png"),
                                full_page=False)
            except Exception as exc:
                report["menu_error"] = f"{type(exc).__name__}: {exc}"

        write_status(status_path, report)
        log(f"chip_found={report['chip_found']} count={report['chip_count']}")

        # Window stays for the owner.
        while True:
            time.sleep(2)
            if not [p for p in context.pages if not p.is_closed()]:
                break

    write_status(status_path, {"stage": "closed", "profile": profile_id})
    log("window closed; done")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except Exception as exc:
        OUT_DIR.mkdir(parents=True, exist_ok=True)
        log(f"error: {type(exc).__name__}: {exc}")
        raise
