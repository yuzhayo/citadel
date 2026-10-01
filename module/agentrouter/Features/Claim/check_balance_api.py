#!/usr/bin/env python3
r"""Browserless balance check (READ-ONLY GETs, no mutations, no browser).

Routes tried in order, cezh03 JSON creds (masked everywhere):
  A. GET /api/user/self + Bearer <pat> + New-API-User header
  B. GET /v1/dashboard/billing/subscription + Bearer <sk- key>
  C. GET /v1/dashboard/billing/usage + Bearer <sk- key>

Browser-like headers (Aliyun WAF beat set basis). 20s per route.
Verdict per route: quota fields when 200, status code otherwise.
Secrets: never printed. Runtime: Citadel venv python only.
"""

from __future__ import annotations

import json
import re
import sys
import time
import urllib.request
import urllib.error
from pathlib import Path

from open_agentrouter import (
    OUT_DIR, log, mask_secret, read_table, safe_id, write_status,
)

BASE = "https://agentrouter.org"
TIMEOUT_S = 20

# Proven 14-header set (opt1/userself experiments): MOBILE UA + client
# hints + Sec-Fetch trio is what beats the Aliyun WAF. Desktop UA without
# them gets the challenge page. Shape copied, creds always ours.
BROWSER_HEADERS = {
    "Accept": "application/json, text/plain, */*",
    "Accept-Language": "en-US,en;q=0.9",
    "Cache-Control": "no-store",
    "Connection": "keep-alive",
    "Referer": BASE + "/console",
    "Sec-Fetch-Dest": "empty",
    "Sec-Fetch-Mode": "cors",
    "Sec-Fetch-Site": "same-origin",
    "User-Agent": ("Mozilla/5.0 (Linux; Android 15; Pixel 9) "
                   "AppleWebKit/537.36 (KHTML, like Gecko) "
                   "Chrome/154.0.0.0 Mobile Safari/537.36"),
    "sec-ch-ua": ('"Chromium";v="154", "Google Chrome";v="154", '
                  '"Not A(Brand";v="99"'),
    "sec-ch-ua-mobile": "?1",
    "sec-ch-ua-platform": '"Android"',
}


# mask_secret now comes from _lib.logging, re-exported by open_agentrouter.


def get(path: str, headers: dict) -> tuple[int | None, dict | str]:
    req = urllib.request.Request(
        BASE + path,
        headers={**BROWSER_HEADERS, **headers},
        method="GET")
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT_S) as resp:
            body = resp.read().decode("utf-8", "replace")
            try:
                return resp.status, json.loads(body)
            except json.JSONDecodeError:
                return resp.status, body[:300]
    except urllib.error.HTTPError as exc:
        try:
            detail = exc.read().decode("utf-8", "replace")[:200]
        except Exception:
            detail = ""
        return exc.code, detail
    except Exception as exc:
        return None, f"{type(exc).__name__}: {exc}"


def pick_quota(payload) -> str:
    """Extract quota-ish numbers without dumping the payload."""
    if not isinstance(payload, dict):
        return f"non-json ({str(payload)[:80]})"
    found = {}

    def walk(node, depth=0):
        if depth > 3 or len(found) >= 6:
            return
        if isinstance(node, dict):
            for key, value in node.items():
                if (isinstance(value, (int, float))
                        and any(k in key.lower()
                                for k in ("quota", "balance", "used",
                                          "consum", "total"))):
                    found[key] = value
                else:
                    walk(value, depth + 1)
        elif isinstance(node, list):
            for item in node[:5]:
                walk(item, depth + 1)

    walk(payload)
    if not found:
        keys = list(payload.keys())[:10] if isinstance(payload, dict) else []
        return f"no quota fields (top keys: {keys})"
    parts = []
    for key, value in found.items():
        if isinstance(value, (int, float)) and abs(value) >= 1000:
            parts.append(f"{key}={value} (~${value / 500000:.2f})")
        else:
            parts.append(f"{key}={value}")
    return "; ".join(parts)


def pick_log(payload) -> str:
    """First log rows, masked + truncated (reward proof lives here)."""
    if not isinstance(payload, dict):
        return f"non-json ({str(payload)[:80]})"
    data = payload.get("data")
    rows = data if isinstance(data, list) else []
    if not rows:
        keys = list(payload.keys())[:10]
        return f"no rows (top keys: {keys})"
    parts = []
    for row in rows[:3]:
        if not isinstance(row, dict):
            continue
        text = json.dumps(row, ensure_ascii=False)
        text = re.sub(r"(sk-[A-Za-z0-9_-]{6})[A-Za-z0-9_-]*([A-Za-z0-9_-]{4})",
                      lambda m: m.group(1)[:4] + "..." + m.group(2), text)
        parts.append(text[:220])
    return f"{len(rows)} row(s) :: " + " || ".join(parts)


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
    status_path = OUT_DIR / f"balance_api_{profile_id}.status.json"
    write_status(status_path, {"stage": "starting", "profile": profile_id})

    json_path = OUT_DIR / f"agentrouter-{profile_id}.json"
    try:
        creds = json.loads(json_path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        write_status(status_path, {"stage": "error", "profile": profile_id,
                                   "error": f"profile json unreadable: {exc}"})
        raise SystemExit(f"profile json unreadable: {exc}")

    api_key = str(creds.get("api_key") or "")
    pat = str(creds.get("pat") or "")
    user_id = str(creds.get("user_id") or "")
    log(f"creds: api_key={mask_secret(api_key)} pat={mask_secret(pat)} "
        f"user_id={user_id or '—'}")

    routes = [
        ("A:self-pat",
         "/api/user/self",
         {"Authorization": f"Bearer {pat}", "New-API-User": user_id}
         if pat and user_id else None,
         pick_quota),
        ("B:billing-sub",
         "/v1/dashboard/billing/subscription",
         {"Authorization": f"Bearer {api_key}"} if api_key else None,
         pick_quota),
        ("C:billing-usage",
         "/v1/dashboard/billing/usage",
         {"Authorization": f"Bearer {api_key}"} if api_key else None,
         pick_quota),
        ("D:log-self",
         "/api/log/self?p=0&page_size=5",
         {"Authorization": f"Bearer {pat}", "New-API-User": user_id}
         if pat and user_id else None,
         pick_log),
        ("E:log-stat",
         "/api/log/self/stat",
         {"Authorization": f"Bearer {pat}", "New-API-User": user_id}
         if pat and user_id else None,
         pick_quota),
    ]

    results = []
    for name, path, headers, picker in routes:
        if headers is None:
            log(f"[{name}] skipped (credential missing)")
            results.append({"route": name, "skipped": "credential missing"})
            continue
        t0 = time.time()
        status, payload = get(path, headers)
        dt = time.time() - t0
        summary = picker(payload) if status == 200 else str(payload)[:160]
        log(f"[{name}] {path} -> {status} in {dt:.1f}s :: {summary}")
        results.append({"route": name, "path": path, "status": status,
                        "seconds": round(dt, 1), "summary": summary})

    write_status(status_path, {"stage": "saved", "profile": profile_id,
                               "results": results})
    ok = [r for r in results if r.get("status") == 200]
    log(f"done: {len(ok)}/{len(results)} routes returned 200")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
