"""Capture password Google milik fitur Add Profile (camoprof).

Listener page dipasang HANYA pada page enrollment yang di-claim, dan
mati bersama page itu. Validasi dua lapis, sama seperti kontrak lama:
event time (JS) dan sekali lagi di Python sebelum nilai disimpan.
Mengosongkan field membuang kandidat.
"""

from urllib.parse import urlparse

from providers import log

from camoprof_add_profile.add_profile_state import (
    CAPTURING_STATES,
    MAX_PASSWORD_LENGTH,
    STATE_ARMED,
    STATE_PASSWORD_OBSERVED,
)

EXPOSED_NAME = "__camoprofEnrollmentInput"

# Inisialisasi script: dipasang via add_init_script sehingga bertahan
# lintas navigasi multi-langkah Google. Origin + field divalidasi di
# event time; nilai KOSONG ikut diteruskan agar clear-field membuang
# kandidat lama.
INIT_SCRIPT = """
(() => {
  const report = (event) => {
    try {
      const el = event.target;
      if (!el || el.nodeType !== 1) return;
      if (location.hostname !== "accounts.google.com") return;
      if (!el.matches("input[type=\\"password\\"]")) return;
      const value = el.value;
      if (typeof value !== "string") return;
      window.__camoprofEnrollmentInput(value);
    } catch (_) {
      // listener tidak boleh mengganggu halaman
    }
  };
  document.addEventListener("input", report, true);
})();
"""


def make_input_callback(enrollment):
    """Callback expose_function — menerima nilai dari field password
    Google pada page enrollment, setelah re-validasi origin sisi
    Python (hostname TEPAT accounts.google.com)."""

    async def on_password_input(value):
        try:
            if enrollment.state not in CAPTURING_STATES:
                return
            if not isinstance(value, str):
                return
            page = enrollment.page
            url = (page.url or "") if page is not None else ""
            host_name = (urlparse(url).hostname or "").lower()
            if host_name != "accounts.google.com":
                return
            if value:
                if len(value) > MAX_PASSWORD_LENGTH:
                    return
                enrollment.password = value
                if enrollment.state == STATE_ARMED:
                    enrollment.state = STATE_PASSWORD_OBSERVED
            else:
                # Field dikosongkan: kandidat lama/parsial dibuang.
                enrollment.password = None
                if enrollment.state == STATE_PASSWORD_OBSERVED:
                    enrollment.state = STATE_ARMED
        except Exception:  # noqa: BLE001 - callback tidak boleh melempar
            pass

    return on_password_input


async def arm_listener(page, enrollment):
    """Pasang capture pada page yang di-claim. Dua lapis validasi
    (JS + Python) tetap berlaku; nilai tidak pernah di-log."""
    from providers import PyhostError

    try:
        await page.expose_function(
            EXPOSED_NAME, make_input_callback(enrollment))
        await page.add_init_script(INIT_SCRIPT)
    except Exception as e:  # noqa: BLE001 - gagal memasang listener
        log("pasang listener enrollment gagal: %s: %s"
            % (type(e).__name__, e))
        raise PyhostError(
            "ENROLLMENT_START_FAILED", "listener enrollment tidak terpasang")
