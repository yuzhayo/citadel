"""Resolusi CDN URL untuk Yuzvid — port ``extract_cdn_url`` dari doodlib.

Bedanya dari sumber: browser di sini memakai **camoufox** (mesin yang sudah
terbukti di Citadel — agentrouter/camoprof/mangareader), bukan Playwright
mandiri. Logika langkah-nya setia pada doodlib:

  1. buka ``/d/{hash}`` → regex ``/download/{token}/n/{file_id}`` di source
  2. buka ``/download/{token}/n/{file_id}`` → POLL ``<a href*="cloudatacdn">``
  3. HEAD → Content-Length + ETag

Provenance:
  Sumber:   C:\\VSCODE\\LTX-QUASAR\\dood\\dood_downloader\\doodlib.py
  Fungsi:   extract_cdn_url (di-port 2026-10-04)
  Registri browser: camoufox sync (pola agentrouter_claim/flow.py)

Modul ini HANYA jalan di worker thread (browser sync). Tidak menyentuh pyhost
event loop.
"""

import logging
import re
import time
from typing import Any, Dict

log = logging.getLogger("yuzvid_downloader.resolve")

# Ad-block pola (dibekukan dari doodlib — samo persis, jangan diubah tanpa
# memperbarui provenance di atas).
AD_BLOCK_PATTERNS = [
    "**/wearadmiration.com/**",
    "**/hikerfaquirs.com/**",
    "**/gibbingbegild.qpon/**",
    "**/runative-syndicate.com/**",
    "**/i.doodcdn.io/ads/**",
]

HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                  "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36",
    "Accept": "*/*",
    "Referer": "https://playmogo.com/",
    "Accept-Encoding": "identity",
}

_HASH_RE = re.compile(r"/(?:d|e)/([a-zA-Z0-9]+)")
_BARE_HASH_RE = re.compile(r"^[a-zA-Z0-9]{5,20}$")


def extract_hash(url: str) -> str | None:
    """Ambil hash ID dari URL DoodStream, atau None bila tidak ada."""
    m = _HASH_RE.search(url or "")
    if m:
        return m.group(1)
    if _BARE_HASH_RE.match((url or "").strip()):
        return url.strip()
    return None


def extract_cdn_url(url: str, timeout: int = 15) -> Dict[str, Any]:
    """Resolve CDN URL dari link DoodStream/PlayMogo.

    Returns dict: success, cdn_url, filename, file_size, etag, error.
    """
    from camoufox.sync_api import Camoufox  # berat: impor saat dipakai

    hash_id = extract_hash(url)
    result: Dict[str, Any] = {"success": False, "cdn_url": None, "filename": None,
                              "file_size": 0, "etag": None, "error": None}
    if not hash_id:
        result["error"] = "Tidak bisa ambil hash dari: %s" % url
        return result

    cdn_url = None
    try:
        # Non-persistent: Camoufox() mengembalikan Browser; halaman dibuat
        # lewat browser.new_page(). (persistent_context=True akan
        # mengembalikan context — di sini tidak perlu profile.)
        with Camoufox(headless=True, humanize=True, os="windows") as browser:
            page = browser.new_page()

            try:
                # Step 1: /d/{hash}
                site_url = "https://playmogo.com/d/%s" % hash_id
                page.goto(site_url, timeout=timeout * 1000, wait_until="domcontentloaded")
                time.sleep(2)

                html = page.evaluate("document.documentElement.outerHTML")
                dl_match = re.search(r"/download/([a-z0-9]+)/n/([\da-f\-]+)", html)
                if not dl_match:
                    result["error"] = "Tidak ada URL download di source halaman"
                    return result

                dl_url = "https://playmogo.com/download/%s/n/%s" % (
                    dl_match.group(1), dl_match.group(2))

                # Step 2: /download/{token}/n/{file_id}
                try:
                    page.goto(dl_url, timeout=timeout * 1000, wait_until="commit")
                except Exception:
                    pass

                for _ in range(15):
                    time.sleep(1)
                    try:
                        cdn_url = page.evaluate(
                            """() => {
                                const a = document.querySelector('a[href*="cloudatacdn"]');
                                return a ? a.href : null;
                            }""")
                        if cdn_url:
                            break
                    except Exception:
                        pass
            except Exception as e:
                log.warning("navigasi resolve gagal: %s", e)
    except Exception as e:  # noqa: BLE001 - laporkan terstruktur ke C#
        result["error"] = "%s: %s" % (type(e).__name__, e)
        return result

    if not cdn_url:
        result["error"] = "Tidak bisa ekstrak CDN URL"
        return result

    # Nama file — segmen path terakhir sebelum query.
    fn_match = re.search(r'cloudatacdn\.com/[^/]+/([^/?]+\.[a-z0-9]+)', cdn_url, re.IGNORECASE)
    if not fn_match:
        fn_match = re.search(r'/([^/?]+\.(?:mp4|mkv|avi|mov|ts|flv))(?:\?|$)', cdn_url, re.IGNORECASE)
    filename = fn_match.group(1) if fn_match else ("%s.mp4" % hash_id)

    result["cdn_url"] = cdn_url
    result["filename"] = filename

    # Step 3: HEAD untuk ukuran + ETag
    try:
        import requests
        h = requests.head(cdn_url, headers=HEADERS, timeout=30, allow_redirects=True)
        result["file_size"] = int(h.headers.get("Content-Length", 0))
        result["etag"] = h.headers.get("ETag", "")
    except Exception as e:
        log.warning("HEAD request gagal: %s", e)

    result["success"] = True
    return result
