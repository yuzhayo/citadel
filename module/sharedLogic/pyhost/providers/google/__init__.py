"""Helper Google bersama: klasifikasi URL, deteksi email, selector.

Dipakai inspection.py, relogin.py, dan enrollment.py. Konstanta dan
fungsi di sini adalah satu-satunya sumber kebenaran untuk hal-hal
Google di pyhost — jangan mendefinisikan ulang di modul lain.
"""

import re
from urllib.parse import urlparse

from providers import log

CHECK_URL = "https://myaccount.google.com/"
SIGNIN_URL = (
    "https://accounts.google.com/signin/v2/identifier"
    "?service=accountsettings&continue=https%3A%2F%2Fmyaccount.google.com%2F"
)
EMAIL_RE = re.compile(
    r"[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+"
    r"@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?"
    r"(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+"
)


def google_url_state(url):
    parsed = urlparse(url or "")
    host = (parsed.hostname or "").lower()
    if host == "myaccount.google.com":
        return "active"
    if host == "accounts.google.com":
        return "signed_out"
    if host in ("www.google.com", "google.com") \
            and parsed.path.startswith("/account/about"):
        return "signed_out"
    return "unknown"


def extract_email(values):
    for value in values or ():
        if not isinstance(value, str):
            continue
        match = EMAIL_RE.search(value)
        if match:
            return match.group(0).lower()
    return None


async def detect_google_email(page):
    """Read account identity only; never use Google's display name."""
    try:
        values = await page.evaluate("""
            () => Array.from(document.querySelectorAll(
                '[data-email], [aria-label*="@"], a[href*="SignOutOptions"]'))
              .flatMap(node => [
                node.getAttribute('data-email'),
                node.getAttribute('aria-label'),
                node.textContent
              ])
              .filter(Boolean)
        """)
        email = extract_email(values)
        if email:
            return email
    except Exception as e:  # noqa: BLE001 - DOM fallback is best effort
        log("deteksi email via DOM gagal: %s" % type(e).__name__)

    for selector, attribute in (
            ("[data-email]", "data-email"),
            ('[aria-label*="@"]', "aria-label")):
        try:
            locator = page.locator(selector).first
            value = await locator.get_attribute(attribute)
            email = extract_email((value,))
            if email:
                return email
        except Exception:  # noqa: BLE001 - selector optional
            continue
    return None


def is_browser_closed_error(error):
    text = str(error).lower()
    return any(marker in text for marker in (
        "target closed", "target page, context or browser has been closed",
        "browser has been closed", "connection closed", "page closed",
        "context destroyed"))


async def is_visible(locator, timeout=1000):
    try:
        return await locator.is_visible(timeout=timeout)
    except Exception:  # noqa: BLE001 - absence is a normal branch
        return False
