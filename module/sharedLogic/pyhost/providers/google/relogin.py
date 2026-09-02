"""google.relogin — satu percobaan login email/password biasa pada
session headed yang sudah terbuka. Isi dipindahkan verbatim dari
pyhost.py (refactor provider boundary, tanpa perubahan perilaku).
"""

import os

from providers import PyhostError, log
from providers.google import (
    EMAIL_RE,
    SIGNIN_URL,
    detect_google_email,
    google_url_state,
    is_browser_closed_error,
    is_visible,
)


async def relogin(host, sess, sid, msg):
    if sess.get("headless"):
        raise PyhostError(
            "HEADLESS_RELOGIN",
            "relogin harus memakai browser headed")

    email = msg.get("email")
    password = msg.get("password")
    if not isinstance(email, str) or not EMAIL_RE.fullmatch(email.strip()):
        raise PyhostError("BAD_CREDENTIAL_INPUT", "email tidak sah")
    if not isinstance(password, str) or not password:
        raise PyhostError("BAD_CREDENTIAL_INPUT", "password kosong")

    page = sess["page"]
    try:
        await page.goto(SIGNIN_URL, wait_until="domcontentloaded",
                        timeout=45000)
        email_input = page.locator('input[type="email"]').first
        if await is_visible(email_input, timeout=5000):
            await email_input.fill(email.strip())
            next_button = page.locator(
                "#identifierNext button, #identifierNext").first
            if await is_visible(next_button):
                await next_button.click()
            else:
                await email_input.press("Enter")

        try:
            await page.wait_for_selector(
                'input[type="password"]', state="visible", timeout=15000)
        except Exception:  # noqa: BLE001 - challenge before password
            return {"state": "action_required", "email": None,
                    "url": page.url or ""}

        password_input = page.locator('input[type="password"]').first
        await password_input.fill(password)
        password_next = page.locator(
            "#passwordNext button, #passwordNext").first
        if await is_visible(password_next):
            await password_next.click()
        else:
            await password_input.press("Enter")
        await page.wait_for_timeout(5000)
    except Exception as e:  # noqa: BLE001 - classify browser/network split
        if is_browser_closed_error(e):
            await host._drop_session(sid, forget_on_failure=True)
            raise PyhostError("BROWSER_GONE", "jendela browser ditutup")
        raise PyhostError(
            "RELOGIN_FAILED",
            "navigasi relog gagal; session dipertahankan")

    url = page.url or ""
    state = google_url_state(url)
    if state == "active":
        detected = await detect_google_email(page)
        try:
            state_path = os.path.join(sess["dir"], "_session_state.json")
            await sess["ctx"].storage_state(path=state_path)
        except Exception as e:  # noqa: BLE001 - optional artifact
            log("storage_state setelah relog gagal: %s" % e)
        return {"state": "active", "email": detected, "url": url}

    errors = []
    try:
        errors = await page.locator(
            '[aria-live="assertive"], .o6cuMc').all_text_contents()
    except Exception:  # noqa: BLE001 - error marker optional
        pass
    if await is_visible(password_input) and any(
            isinstance(value, str) and value.strip() for value in errors):
        return {"state": "credential_rejected", "email": None,
                "url": url}
    return {"state": "action_required", "email": None, "url": url}
