"""Read-only nav scan: find which console page holds Generate Token / PAT. No clicks."""
import json
import sys
from pathlib import Path
from camoufox.sync_api import Camoufox

PROFILE = sys.argv[1] if len(sys.argv) > 1 else "p_770b6f415fa64ffaa617d8f59b48634e"
BASE = "https://agentrouter.org"
OUT = Path(r"C:\Users\YUZHA\AppData\Local\Temp\opencode")
USERDIR = Path(r"C:\Users\YUZHA\AppData\Local\Citadel\Credenz\google\profiles") / PROFILE

with Camoufox(headless=False, persistent_context=True,
              user_data_dir=str(USERDIR), humanize=True, os="windows") as browser:
    page = browser.new_page()
    page.goto(BASE + "/console", wait_until="domcontentloaded", timeout=45000)
    page.wait_for_timeout(2500)
    print("console url:", page.url, flush=True)
    links = []
    for a in page.query_selector_all("a[href]"):
        try:
            href = a.get_attribute("href")
            text = (a.inner_text() or "").strip()[:40]
        except Exception:
            continue
        if href and href.startswith("/"):
            links.append({"text": text, "href": href})
    seen = set()
    uniq = []
    for l in links:
        k = l["href"]
        if k not in seen:
            seen.add(k)
            uniq.append(l)
    print("NAV:", json.dumps(uniq, ensure_ascii=False), flush=True)
    for path in ["/console", "/console/token", "/console/personal", "/console/log"]:
        try:
            page.goto(BASE + path, wait_until="domcontentloaded", timeout=30000)
            page.wait_for_timeout(2000)
            body = page.inner_text("body")
            hits = [kw for kw in ["Generate Token", "PAT", "Personal Access"] if kw.lower() in body.lower()]
            print(f"{path} -> {page.url} hits={hits}", flush=True)
        except Exception as e:
            print(f"{path} ERR {str(e)[:120]}", flush=True)
    try:
        page.close()
    except Exception:
        pass
print("SCAN DONE", flush=True)
