"""google.inspect — periksa resident profile di myaccount.google.com.

Isi dipindahkan verbatim dari pyhost.py (refactor provider boundary,
tanpa perubahan perilaku).
"""

import os

from providers import PyhostError, log
from providers.google import (
    CHECK_URL,
    detect_google_email,
    google_url_state,
    is_browser_closed_error,
)


async def inspect(host, sess, sid):
    page = sess["page"]
    try:
        await page.goto(CHECK_URL, wait_until="domcontentloaded",
                        timeout=45000)
        await page.wait_for_timeout(4000)
    except Exception as e:  # noqa: BLE001 - klasifikasi di bawah
        # codex audit #4: kebanyakan kegagalan di sini BUKAN browser mati.
        # DNS, timeout jaringan, dan navigasi gagal bisa terjadi dengan
        # browser hidup utuh — maka session dipertahankan.
        if is_browser_closed_error(e):
            await host._drop_session(sid, forget_on_failure=True)
            raise PyhostError("BROWSER_GONE",
                              "jendela browser sudah ditutup")
        raise PyhostError(
            "VERIFY_FAILED",
            "navigasi verify gagal (session dipertahankan): %s" % e)
    url = page.url or ""
    state = google_url_state(url)
    email = await detect_google_email(page) if state == "active" else None
    state_saved = False
    if state == "active":
        try:
            state_path = os.path.join(sess["dir"], "_session_state.json")
            await sess["ctx"].storage_state(path=state_path)
            state_saved = True
        except Exception as e:  # noqa: BLE001 - artefak opsional
            log("storage_state gagal: %s" % e)
    return {"state": state, "email": email, "url": url,
            "state_saved": state_saved}
