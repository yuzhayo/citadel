"""State machine enrollment milik fitur Add Profile (camoprof).

Adapter tipis di atas blok generik yang sudah ada (kontrak tasks/plan.md):

- TIDAK ada lease/claim/rotasi. Page enrollment E dibuat biasa
  (ctx.new_page — pola yang terbukti), listener dipasang di E SEBELUM
  E dinavigasi, page resident R ditutup setelah E hidup (satu jendela;
  context tak pernah kehabisan page), dan referensi session
  ``sess["page"]`` ditukar lewat helper generik ``host.set_primary_page``
  sehingga selalu menunjuk page hidup.
- host/sess adalah PARAMETER fungsi — tidak ada backlink tersimpan.
- teardown (finish/cancel/expiry/gagal navigasi) mengakhiri flow:
  registry session dilepas segera, lalu page E dan context ditutup di
  latar. Tidak ada replacement page atau browser menganggur.
- state machine, validasi origin/field, one-shot finish, empty-clear,
  dan mati-secret-bersama-session memakai helper Google lama
  (providers/google) — kebijakan lama, tidak diubah.
"""

import asyncio

from providers import PyhostError, log
from providers.google import (
    EMAIL_RE,
    SIGNIN_URL,
    detect_google_email,
    google_url_state,
    is_browser_closed_error,
)

from camoprof_add_profile.add_profile_state import (
    ENROLLMENT_TIMEOUT_SEC,
    TERMINAL_STATES,
    STATE_ARMED,
    STATE_BROWSER_GONE,
    STATE_CANCELLED,
    STATE_CHALLENGE,
    STATE_COMPLETE,
    STATE_CONSUMED,
    STATE_EXPIRED,
    STATE_FAILED,
    STATE_PASSWORD_OBSERVED,
    STATE_WAITING,
    STATE_WRONG_ACCOUNT,
)
from camoprof_add_profile.password_capture import arm_listener


class AddProfileEnrollment:
    __slots__ = ("profile", "sid", "ctx", "page", "expected_email",
                 "deadline", "state", "password", "email", "expire_task",
                 "navigate_task", "close_task")

    def __init__(self, profile, sid, ctx, expected_email, deadline):
        self.profile = profile
        self.sid = sid
        self.ctx = ctx
        self.page = None
        self.expected_email = expected_email
        self.deadline = deadline
        self.state = STATE_ARMED
        self.password = None
        self.email = None
        self.expire_task = None
        self.navigate_task = None
        self.close_task = None


async def start(host, sess, sid, msg):
    """Buat page enrollment, arm listener, BARU navigasi.

    Urutan: page E dibuat -> listener dipasang di E -> referensi
    session ditukar ke E (helper generik) -> R ditutup (E sudah hidup,
    context aman) -> navigasi E ke login Google sebagai task latar
    (start kembali segera; protokol v1 berurutan, goto yang ditunggu
    akan mengantri cancel di belakangnya).
    """
    if sess.get("headless"):
        raise PyhostError("HEADLESS_ENROLLMENT",
                          "enrollment harus memakai browser headed")

    profile = sess["profile"]
    existing = host.add_profile_enrollments.get(profile)
    if existing is not None and existing.state not in TERMINAL_STATES:
        raise PyhostError("ENROLLMENT_ACTIVE",
                          "profile sudah punya enrollment aktif: " + profile)

    expected_email = msg.get("expected_email")
    if expected_email is not None:
        if not isinstance(expected_email, str) \
                or not EMAIL_RE.fullmatch(expected_email.strip()):
            raise PyhostError("BAD_CREDENTIAL_INPUT",
                              "expected_email tidak sah")
        expected_email = expected_email.strip().lower()

    loop = asyncio.get_running_loop()
    enr = AddProfileEnrollment(
        profile, sid, sess["ctx"], expected_email,
        loop.time() + ENROLLMENT_TIMEOUT_SEC)

    resident = sess.get("page")
    try:
        page = await sess["ctx"].new_page()
        enr.page = page
        await arm_listener(page, enr)
    except Exception as e:  # noqa: BLE001 - gagal memasang listener
        log("siapkan page enrollment gagal: %s: %s"
            % (type(e).__name__, e))
        await _close_quietly(enr)
        raise PyhostError("ENROLLMENT_START_FAILED",
                          "page enrollment tidak siap")

    # Referensi session menunjuk page hidup E sebelum R ditutup —
    # invariant: session terdaftar ⇔ primary page hidup.
    host.set_primary_page(sid, page)
    if resident is not None and resident is not page:
        try:
            if not _page_is_closed(resident):
                await resident.close()
        except Exception as e:  # noqa: BLE001 - best effort
            log("tutup page resident gagal: %s" % type(e).__name__)

    host.add_profile_enrollments[profile] = enr
    enr.expire_task = asyncio.ensure_future(_expire_later(host, enr))
    enr.navigate_task = asyncio.ensure_future(_navigate_later(host, enr))
    return {"session": sid, "state": STATE_ARMED}


async def status(host, sid):
    enr = _find(host, sid)
    if enr is None:
        raise PyhostError("ENROLLMENT_NOT_FOUND", "enrollment: %r" % (sid,))
    if enr.state not in TERMINAL_STATES and enr.state != STATE_COMPLETE:
        await _advance(host, enr)
    return _snapshot(enr)


async def finish(host, sid):
    """Email/password TEPAT SATU kali setelah Complete — SEGERA.

    Secret diserahkan dalam milidetik; browser dimatikan di latar
    (retire_session). Finish TIDAK boleh menunggu shutdown context
    (detik) — kalau menunggu, dialog C# terpaku di "saving credential…"
    selama browser mati."""
    enr = _find(host, sid)
    if enr is None:
        raise PyhostError("ENROLLMENT_NOT_FOUND", "enrollment: %r" % (sid,))
    if enr.state == STATE_CONSUMED:
        raise PyhostError("ENROLLMENT_CONSUMED",
                          "password enrollment sudah diambil; satu kali saja")
    if enr.state == STATE_WRONG_ACCOUNT:
        raise PyhostError("WRONG_ACCOUNT",
                          "email aktif berbeda dari expected_email")
    if enr.state != STATE_COMPLETE:
        raise PyhostError("ENROLLMENT_NOT_COMPLETE",
                          "finish ditolak pada state %r" % (enr.state,))
    email = enr.email
    password = enr.password
    await _finish_flow(host, enr, STATE_CONSUMED)
    return {"session": sid, "email": email, "password": password}


async def cancel(host, sid):
    """Pembatalan instan: state terminal + secret dibuang + registry
    dilepas; browser dimatikan di latar. Command ini tidak pernah
    menunggu shutdown browser."""
    enr = _find(host, sid)
    if enr is None:
        return {"session": sid, "state": "none"}
    if enr.state not in TERMINAL_STATES:
        await _finish_flow(host, enr, STATE_CANCELLED)
    return {"session": sid, "state": enr.state}


def disarm_for_session(host, sid, profile):
    """Sinkron; dipanggil hook lifecycle saat session mati. Secret
    dibuang; page tidak disentuh (context sedang mati).

    Self-cancel guard: kalau browser mati DARI DALAM _navigate_later,
    task itu sendiri yang memicu _drop_session — jangan cancel diri
    sendiri (CancelledError di tengah __aexit__ context close akan
    meninggalkan session mati tetap terdaftar).
    """
    enr = host.add_profile_enrollments.get(profile)
    if enr is None or enr.sid != sid:
        return
    _end(enr, STATE_BROWSER_GONE)
    task = enr.navigate_task
    if task is not None and task is not asyncio.current_task():
        task.cancel()
    enr.page = None
    enr.ctx = None


# ---- internals ---------------------------------------------------------


def _find(host, sid):
    for enr in host.add_profile_enrollments.values():
        if enr.sid == sid:
            return enr
    return None


def _page_is_closed(page):
    try:
        return bool(page.is_closed())
    except Exception:  # noqa: BLE001 - page mati dianggap tertutup
        return True


async def _advance(host, enr):
    page = enr.page
    if page is None or _page_is_closed(page):
        # Page enrollment (satu-satunya page = jendela) ditutup manual:
        # session terbukti mati — buang secret, terminal browser_gone,
        # registry dilepas; sisa pemusnahan di latar.
        await _finish_flow(host, enr, STATE_BROWSER_GONE)
        return
    if asyncio.get_running_loop().time() >= enr.deadline:
        await _finish_flow(host, enr, STATE_EXPIRED)
        return

    url = page.url or ""
    gs = google_url_state(url)
    if gs == "active":
        email = await detect_google_email(page)
        if email:
            if enr.expected_email and email != enr.expected_email:
                enr.email = email
                await _finish_flow(host, enr, STATE_WRONG_ACCOUNT)
            else:
                enr.email = email
                enr.state = STATE_COMPLETE
        return
    if gs == "signed_out":
        path = url.split("//", 1)[-1].split("/", 1)[-1].lower()
        if "challenge" in path:
            enr.state = STATE_CHALLENGE
        else:
            enr.state = (STATE_PASSWORD_OBSERVED if enr.password
                         else STATE_ARMED)
        return
    enr.state = STATE_WAITING if enr.password else STATE_ARMED


def _snapshot(enr):
    return {
        "session": enr.sid,
        "state": enr.state,
        "email": enr.email,
        "has_password": enr.password is not None,
        "challenge": enr.state == STATE_CHALLENGE,
        "url": (enr.page.url or "") if enr.page is not None else "",
    }


def _end(enr, state):
    if enr.state in TERMINAL_STATES:
        return
    enr.state = state
    enr.password = None
    if enr.expire_task is not None:
        enr.expire_task.cancel()
        enr.expire_task = None


async def _stop_navigation(enr):
    task = enr.navigate_task
    enr.navigate_task = None
    if task is None or task.done():
        return
    task.cancel()
    try:
        await task
    except asyncio.CancelledError:
        pass
    except Exception as e:  # noqa: BLE001 - pembatalan best effort
        log("pembatalan navigasi enrollment error: %s" % type(e).__name__)


async def _finish_flow(host, enr, state):
    """Terminal CEPAT: buang secret, hentikan task navigasi, lepas
    registry, jadwalkan kematian browser di latar. Respons command
    tidak pernah menunggu shutdown browser.

    (Jalur kegagalan navigasi TIDAK memakai ini — dia berjalan DI
    DALAM task navigasi; _stop_navigation akan menunggu dirinya
    sendiri. Jalur itu mengakhiri secara inline.)"""
    _end(enr, state)
    await _stop_navigation(enr)
    page, enr.page = enr.page, None
    enr.close_task = host.retire_session(enr.sid, page)


async def _close_quietly(enr):
    page, enr.page = enr.page, None
    if page is None:
        return
    try:
        await page.close()
    except Exception:  # noqa: BLE001 - best effort
        pass


async def _navigate_later(host, enr):
    """Task latar: navigasikan page enrollment ke login Google.
    Kegagalan non-browser -> state failed (jujur lewat status); browser
    mati -> _drop_session (hook lifecycle membuang secret)."""
    try:
        await enr.page.goto(
            SIGNIN_URL, wait_until="domcontentloaded", timeout=45000)
    except asyncio.CancelledError:
        raise
    except Exception as e:  # noqa: BLE001 - klasifikasi browser/jaringan
        if is_browser_closed_error(e):
            await host._drop_session(enr.sid, forget_on_failure=True)
            return
        log("navigasi enrollment gagal: %s: %s" % (type(e).__name__, e))
        # Inline (kita DI DALAM task navigasi — _stop_navigation akan
        # menunggu diri sendiri): terminal + secret dibuang + registry
        # lepas; browser dimatikan di latar.
        _end(enr, STATE_FAILED)
        page, enr.page = enr.page, None
        enr.close_task = host.retire_session(enr.sid, page)


async def _expire_later(host, enr):
    """Task latar: jatuhkan enrollment yang tidak selesai dalam
    10 menit — termasuk dari complete: secret tidak menunggu selamanya
    sebuah finish yang tidak datang."""
    try:
        delay = max(
            0.0, enr.deadline - asyncio.get_running_loop().time())
        await asyncio.sleep(delay)
        if enr.state not in TERMINAL_STATES:
            await _finish_flow(host, enr, STATE_EXPIRED)
    except asyncio.CancelledError:
        pass
    except Exception as e:  # noqa: BLE001 - task latar tak boleh mati diam
        log("expire enrollment gagal: %s: %s" % (type(e).__name__, e))
