"""State machine enrollment milik fitur Add Profile (camoprof).

Perbedaan struktural dari implementasi providers/google lama:

- TIDAK ada backlink ke host/registry (INV-3). Semua akses page lewat
  ``lease`` (SessionLease milik core) — fitur tidak tahu bentuk
  registry session.
- start MENG-CLAIM resident primary page: listener dipasang pada page
  yang sudah ada, TIDAK ada ctx.new_page() (INV-2) — satu jendela
  sejak awal, tidak ada jendela yang dibuat lalu dibuang.
- Semua jalur terminal melepas lease; rotasi page pengganti (saat
  page enrollment kotor) lewat ``lease.rotate_primary()`` yang membuat
  page baru DULU sebelum menutup — context tidak pernah kehabisan
  page (kebenaran Playwright: page terakhir tertutup = context mati).
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
    CAPTURING_STATES,
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
    __slots__ = ("profile", "sid", "lease", "expected_email", "deadline",
                 "state", "password", "email", "expire_task",
                 "navigate_task")

    def __init__(self, profile, sid, lease, expected_email, deadline):
        self.profile = profile
        self.sid = sid
        self.lease = lease          # SessionLease (core) — bukan registry
        self.expected_email = expected_email
        self.deadline = deadline
        self.state = STATE_ARMED
        self.password = None
        self.email = None
        self.expire_task = None
        self.navigate_task = None

    @property
    def page(self):
        """Primary page via lease — hidup atau raise, tidak pernah
        mengekspos page mati (INV-1)."""
        return self.lease.page


async def start(host, sess, sid, msg):
    """Claim resident primary page, arm listener, BARU navigasi.

    Urutan: lease di-claim -> listener dipasang pada page RESIDENT ->
    state armed didaftarkan -> navigasi berjalan sebagai task milik
    enrollment (start kembali segera; protokol v1 berurutan, goto yang
    ditunggu akan mengantri cancel di belakangnya).
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

    # Claim primary page: owner "camoprof.add_profile" eksklusif.
    # navigate/inspect lain akan kena SESSION_BUSY selama lease hidup.
    lease = host.session_host.claim_primary(sid, "camoprof.add_profile")
    page = lease.page  # resident page hidup — INV-1 & INV-2

    loop = asyncio.get_running_loop()
    enr = AddProfileEnrollment(
        profile, sid, lease, expected_email,
        loop.time() + ENROLLMENT_TIMEOUT_SEC)

    # Listener dipasang pada page resident SEBELUM navigasi apapun.
    await arm_listener(page, enr)

    host.add_profile_enrollments[profile] = enr
    enr.expire_task = asyncio.ensure_future(_expire_later(enr))
    enr.navigate_task = asyncio.ensure_future(_navigate_later(host, enr))
    return {"session": sid, "state": STATE_ARMED}


async def status(host, sid):
    enr = _find(host, sid)
    if enr is None:
        raise PyhostError("ENROLLMENT_NOT_FOUND", "enrollment: %r" % (sid,))
    if enr.state not in TERMINAL_STATES and enr.state != STATE_COMPLETE:
        await _advance(enr)
    return _snapshot(enr)


async def finish(host, sid):
    """Email/password TEPAT SATU kali setelah Complete."""
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
    await _teardown(enr, STATE_CONSUMED)
    return {"session": sid, "email": email, "password": password}


async def cancel(host, sid):
    """Teardown idempotent: listener ikut mati bersama rotasi page,
    secret dibuang, lease dilepas."""
    enr = _find(host, sid)
    if enr is None:
        return {"session": sid, "state": "none"}
    if enr.state not in TERMINAL_STATES:
        await _teardown(enr, STATE_CANCELLED)
    return {"session": sid, "state": enr.state}


def disarm_for_session(host, sid, profile):
    """Sinkron; dipanggil _drop_session (core lifecycle) saat session
    mati. Secret dibuang; page tidak disentuh (context sedang mati).

    Self-cancel guard: kalau browser mati DARI DALAM _navigate_later,
    task itu sendiri yang menjalankan _drop_session — jangan cancel
    diri sendiri (CancelledError di tengah __aexit__ context close
    akan meninggalkan session mati tetap terdaftar).
    """
    enr = host.add_profile_enrollments.get(profile)
    if enr is None or enr.sid != sid:
        return
    _end(enr, STATE_BROWSER_GONE)
    task = enr.navigate_task
    if task is not None and task is not asyncio.current_task():
        task.cancel()
    enr.lease = None


# ---- internals ---------------------------------------------------------


def _find(host, sid):
    for enr in host.add_profile_enrollments.values():
        if enr.sid == sid:
            return enr
    return None


async def _advance(enr):
    try:
        page = enr.lease.page
    except Exception:  # noqa: BLE001 - page mati = gone
        # Page enrollment ditutup manual (satu-satunya page = jendela):
        # session terbukti mati — hapus dari registry lewat lease (bukan
        # registry mentah) supaya open berikutnya tidak kena PROFILE_BUSY
        # palsu, lalu terminal browser_gone.
        lease = enr.lease
        enr.lease = None
        if lease is not None:
            lease.drop_session()
        _end(enr, STATE_BROWSER_GONE)
        return
    if asyncio.get_running_loop().time() >= enr.deadline:
        await _teardown(enr, STATE_EXPIRED)
        return

    url = page.url or ""
    gs = google_url_state(url)
    if gs == "active":
        email = await detect_google_email(page)
        if email:
            if enr.expected_email and email != enr.expected_email:
                enr.email = email
                await _teardown(enr, STATE_WRONG_ACCOUNT)
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
    url = ""
    if enr.lease is not None:
        try:
            url = enr.lease.page.url or ""
        except Exception:  # noqa: BLE001 - page mati: url kosong
            pass
    return {
        "session": enr.sid,
        "state": enr.state,
        "email": enr.email,
        "has_password": enr.password is not None,
        "challenge": enr.state == STATE_CHALLENGE,
        "url": url,
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


async def _teardown(enr, state):
    """Terminal + rotasi page bersih + lepas lease. Page enrollment
    (resident yang di-claim) kotor oleh listener — diputar ke page
    bersih; rotate_primary membuat page baru DULU sebelum menutup."""
    _end(enr, state)
    await _stop_navigation(enr)
    lease = enr.lease
    enr.lease = None
    if lease is not None:
        try:
            await lease.rotate_primary()
        except Exception as e:  # noqa: BLE001 - best effort
            log("rotasi page enrollment gagal: %s" % type(e).__name__)
        lease.release()


async def _navigate_later(host, enr):
    """Task latar: navigasikan primary page yang di-claim ke login
    Google. Kegagalan non-browser -> state failed (jujur lewat
    status); browser mati -> _drop_session (hook lifecycle core)."""
    try:
        await enr.lease.page.goto(
            SIGNIN_URL, wait_until="domcontentloaded", timeout=45000)
    except asyncio.CancelledError:
        raise
    except Exception as e:  # noqa: BLE001 - klasifikasi browser/jaringan
        if is_browser_closed_error(e):
            await host._drop_session(enr.sid, forget_on_failure=True)
            return
        log("navigasi enrollment gagal: %s: %s" % (type(e).__name__, e))
        _end(enr, STATE_FAILED)
        lease = enr.lease
        enr.lease = None
        if lease is not None:
            try:
                await lease.rotate_primary()
            except Exception:  # noqa: BLE001 - best effort
                pass
            lease.release()


async def _expire_later(enr):
    """Task latar: jatuhkan enrollment yang tidak selesai dalam
    10 menit — termasuk dari complete: secret tidak menunggu selamanya
    sebuah finish yang tidak datang."""
    try:
        delay = max(
            0.0, enr.deadline - asyncio.get_running_loop().time())
        await asyncio.sleep(delay)
        if enr.state not in TERMINAL_STATES:
            await _teardown(enr, STATE_EXPIRED)
    except asyncio.CancelledError:
        pass
    except Exception as e:  # noqa: BLE001 - task latar tak boleh mati diam
        log("expire enrollment gagal: %s: %s" % (type(e).__name__, e))
