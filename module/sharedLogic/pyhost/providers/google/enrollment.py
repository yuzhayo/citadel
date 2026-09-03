"""google.enrollment — pendaftaran sekali-ketik kredensial Google.

Alur (urutan dijamin): resident session headed dibuka -> enrollment
page dibuat di context yang sama -> listener dipasang -> state Armed
terdaftar -> BARU page dinavigasi ke halaman login Google. User
mengetik email/password langsung di Google; listener page hanya
menerima nilai dari field password Google yang tervalidasi (host
tepat accounts.google.com, input[type=password]) — divalidasi di
event time (JS) dan sekali lagi di sisi Python sebelum disimpan.

Password hidup di satu variabel Python, ditimpa saat diketik ulang,
dibuang saat consume/teardown; tidak pernah masuk log, status,
exception, atau file. ``finish`` mengembalikannya tepat satu kali;
setelah itu state Consumed dan secret sudah dibuang.

State machine::

    armed -> password_observed -> waiting_for_google | challenge
          -> complete -> consumed
    (state non-terminal mana pun)
          -> cancelled | expired | browser_gone | wrong_account | failed

``start`` kembali SEGERA setelah state Armed terdaftar — navigasi ke
halaman login berjalan sebagai task milik enrollment, bukan bagian dari
respons. Ini bukan kosmetik: protokol v1 memproses request secara
berurutan, jadi kalau goto ditunggu di dalam start, sebuah cancel yang
datang saat navigasi berjalan akan mengantri di belakangnya dan
penutupan dialog bisa menggantung sampai timeout goto (45 detik).
Teardown membatalkan dan MENUNGGU task navigasi itu sebelum menutup
page. Kegagalan navigasi (non-browser) mengakhiri enrollment dengan
state ``failed`` — jujur dilaporkan lewat ``status``.

Mengosongkan field password (event input dengan nilai kosong) membuang
kandidat yang tertangkap — nilai lama/parsial tidak pernah bisa menjadi
credential relog hanya karena user sempat mengetik lalu menghapus.

SATU JENDELA: di browser headed, ``ctx.new_page()`` adalah jendela baru.
``start`` menutup page resident netral begitu enrollment page terpasang,
sehingga user melihat tepat satu jendela Camofox selama enrollment;
teardown membuka page bersih sebagai pengganti dan memperbaiki referensi
``sess["page"]`` (cookies/session hidup di context, bukan di page —
menutup page resident tidak menghilangkan apa pun).

Cleanup mengikuti lifecycle session: ``_drop_session`` di pyhost.py
memanggil ``disarm_for_session`` — browser ditutup manual,
session.close, kegagalan launch, shutdown, dan stdin EOF semuanya
lewat jalur itu, jadi tidak ada jalur yang meninggalkan secret di
memori setelah session-nya mati.
"""

import asyncio
from urllib.parse import urlparse

from providers import PyhostError, log
from providers.google import (
    EMAIL_RE,
    SIGNIN_URL,
    detect_google_email,
    google_url_state,
    is_browser_closed_error,
)

ENROLLMENT_TIMEOUT_SEC = 600.0
_MAX_PASSWORD_LENGTH = 1024
_EXPOSED_NAME = "__pyhostEnrollmentInput"

_CAPTURING_STATES = frozenset(
    ("armed", "password_observed", "waiting_for_google", "challenge"))
_TERMINAL_STATES = frozenset(
    ("consumed", "cancelled", "expired", "browser_gone", "wrong_account",
     "failed"))

# Dipasang via add_init_script sehingga bertahan lintas navigasi
# multi-langkah Google. Validasi origin dan field terjadi di event
# time (JS) DAN sekali lagi di sisi Python (callback) sebelum nilai
# diterima. Field teraudit Google bernama "Passwd"; input password
# tanpa nama tetap diterima HANYA pada host yang tepat. Nilai KOSONG
# ikut diteruskan: mengosongkan field harus membuang kandidat lama.
_INIT_SCRIPT = """
(() => {
  const report = (event) => {
    try {
      const el = event.target;
      if (!el || el.nodeType !== 1) return;
      if (location.hostname !== "accounts.google.com") return;
      if (!el.matches("input[type=\\"password\\"]")) return;
      const value = el.value;
      if (typeof value !== "string") return;
      window.__pyhostEnrollmentInput(value);
    } catch (_) {
      // listener tidak boleh mengganggu halaman
    }
  };
  document.addEventListener("input", report, true);
})();
"""


class _Enrollment:
    __slots__ = ("profile", "sid", "ctx", "page", "expected_email",
                 "deadline", "state", "password", "email", "expire_task",
                 "navigate_task", "host", "took_over_window")

    def __init__(self, profile, sid, ctx, expected_email, deadline):
        self.profile = profile
        self.sid = sid
        self.ctx = ctx
        self.page = None
        self.expected_email = expected_email
        self.deadline = deadline
        self.state = "armed"
        self.password = None
        self.email = None
        self.expire_task = None
        self.navigate_task = None
        self.host = None
        self.took_over_window = False


# ---- commands ---------------------------------------------------------


async def start(host, sess, sid, msg):
    """Buka enrollment: validasi session, pasang listener, BARU navigasi."""
    if sess.get("headless"):
        raise PyhostError("HEADLESS_ENROLLMENT",
                          "enrollment harus memakai browser headed")

    profile = sess["profile"]
    existing = host.enrollments.get(profile)
    if existing is not None and existing.state not in _TERMINAL_STATES:
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
    enr = _Enrollment(profile, sid, sess["ctx"], expected_email,
                      loop.time() + ENROLLMENT_TIMEOUT_SEC)
    enr.host = host
    try:
        page = await sess["ctx"].new_page()
        enr.page = page
        await page.expose_function(_EXPOSED_NAME, _make_input_callback(enr))
        await page.add_init_script(_INIT_SCRIPT)
    except Exception as e:  # noqa: BLE001 - gagal memasang listener
        log("pasang listener enrollment gagal: %s: %s"
            % (type(e).__name__, e))
        await _close_page_quietly(enr)
        raise PyhostError("ENROLLMENT_START_FAILED",
                          "listener enrollment tidak terpasang")

    # Daftarkan SEBELUM navigasi: jika goto mati-mati, cleanup punya
    # pegangan (pola yang sama dengan session.open).
    host.enrollments[profile] = enr
    enr.expire_task = asyncio.ensure_future(_expire_later(enr))
    # Navigasi berjalan sebagai task milik enrollment — start kembali
    # SEGERA setelah armed. Kalau goto ditunggu di sini, sebuah cancel
    # yang datang saat navigasi berjalan akan mengantri di belakangnya
    # (protokol v1 berurutan) dan penutupan dialog bisa menggantung
    # sampai 45 detik. Teardown membatalkan task ini.
    enr.navigate_task = asyncio.ensure_future(_navigate_later(host, enr))

    # SATU JENDELA: tutup page resident (jendela awal yang dinavigasi
    # session.open ke halaman netral) — di browser headed, ctx.new_page()
    # adalah jendela baru, dan membiarkan keduanya terbuka terlihat
    # seperti "CamoFox dibuka 2x". Enrollment page menjadi satu-satunya
    # yang terlihat; teardown membuka page bersih sebagai pengganti dan
    # memperbaiki referensi sess["page"] (lihat _restore_resident_page).
    resident = sess.get("page")
    if resident is not None and resident is not enr.page:
        try:
            if not resident.is_closed():
                await resident.close()
            enr.took_over_window = True
        except Exception as e:  # noqa: BLE001 - best effort
            log("tutup page resident gagal: %s" % type(e).__name__)

    return {"session": sid, "state": "armed"}


async def status(host, sid):
    """State tanpa plaintext; sekaligus memajukan state machine."""
    enr = _find(host, sid)
    if enr is None:
        raise PyhostError("ENROLLMENT_NOT_FOUND", "enrollment: %r" % (sid,))
    if enr.state != "complete" and enr.state not in _TERMINAL_STATES:
        await _advance(enr)
    return _snapshot(enr)


async def finish(host, sid):
    """Kembalikan email/password TEPAT SATU kali setelah Complete."""
    enr = _find(host, sid)
    if enr is None:
        raise PyhostError("ENROLLMENT_NOT_FOUND", "enrollment: %r" % (sid,))
    if enr.state == "consumed":
        raise PyhostError("ENROLLMENT_CONSUMED",
                          "password enrollment sudah diambil; satu kali saja")
    if enr.state == "wrong_account":
        raise PyhostError("WRONG_ACCOUNT",
                          "email aktif berbeda dari expected_email")
    if enr.state != "complete":
        raise PyhostError("ENROLLMENT_NOT_COMPLETE",
                          "finish ditolak pada state %r" % (enr.state,))
    email = enr.email
    password = enr.password
    await _teardown(enr, "consumed")
    return {"session": sid, "email": email, "password": password}


async def cancel(host, sid):
    """Teardown idempotent: listener, secret, task, enrollment page."""
    enr = _find(host, sid)
    if enr is None:
        return {"session": sid, "state": "none"}
    if enr.state not in _TERMINAL_STATES:
        await _teardown(enr, "cancelled")
    return {"session": sid, "state": enr.state}


def disarm_for_session(host, sid, profile):
    """Sinkron; dipanggil dari _drop_session saat session mati.

    Secret dibuang dan task dihentikan; page sengaja TIDAK ditutup —
    context-nya sedang mati, menutup page hanya bisa melempar.

    Jalur browser-gone-dari-dalam-navigasi: ``_navigate_later`` adalah
    pemanggil ``_drop_session`` — task navigasi TIDAK boleh mencancel
    dirinya sendiri, kalau tidak CancelledError masuk di tengah
    ``__aexit__`` context close, ``_drop_session`` sengaja tidak
    menelannya, dan session yang sudah mati tetap terdaftar
    (status running palsu / PROFILE_BUSY).
    """
    enr = host.enrollments.get(profile)
    if enr is None or enr.sid != sid:
        return
    _end(enr, "browser_gone")
    task = enr.navigate_task
    if task is not None and task is not asyncio.current_task():
        task.cancel()  # best effort; browser sedang mati
    enr.page = None
    enr.ctx = None


# ---- internals ---------------------------------------------------------


def _find(host, sid):
    for enr in host.enrollments.values():
        if enr.sid == sid:
            return enr
    return None


def _make_input_callback(enr):
    async def on_password_input(value):
        try:
            if enr.state not in _CAPTURING_STATES:
                return
            if not isinstance(value, str):
                return
            page = enr.page
            url = (page.url or "") if page is not None else ""
            host_name = (urlparse(url).hostname or "").lower()
            if host_name != "accounts.google.com":
                return
            if value:
                if len(value) > _MAX_PASSWORD_LENGTH:
                    return
                enr.password = value
                if enr.state == "armed":
                    enr.state = "password_observed"
            else:
                # Field dikosongkan: kandidat lama/parsial dibuang, agar
                # tidak pernah tersimpan sebagai credential relog hanya
                # karena user sempat mengetik lalu menghapus.
                enr.password = None
                if enr.state == "password_observed":
                    enr.state = "armed"
        except Exception:  # noqa: BLE001 - callback tidak boleh melempar
            pass
    return on_password_input


async def _advance(enr):
    page = enr.page
    if page is None:
        _end(enr, "browser_gone")
        return
    try:
        closed = page.is_closed()
    except Exception:  # noqa: BLE001 - page mati dianggap tertutup
        closed = True
    if closed:
        _end(enr, "browser_gone")
        enr.page = None
        return
    if asyncio.get_running_loop().time() >= enr.deadline:
        await _teardown(enr, "expired")
        return

    url = page.url or ""
    gs = google_url_state(url)
    if gs == "active":
        email = await detect_google_email(page)
        if email:
            if enr.expected_email and email != enr.expected_email:
                enr.email = email
                await _teardown(enr, "wrong_account")
            else:
                enr.email = email
                enr.state = "complete"
        return
    if gs == "signed_out":
        path = urlparse(url).path.lower()
        if "/challenge" in path:
            enr.state = "challenge"
        else:
            enr.state = "password_observed" if enr.password else "armed"
        return
    # Host lain: perantara redirect — tunggu Google membuktikan akun.
    enr.state = "waiting_for_google" if enr.password else "armed"


def _snapshot(enr):
    return {
        "session": enr.sid,
        "state": enr.state,
        "email": enr.email,
        "has_password": enr.password is not None,
        "challenge": enr.state == "challenge",
        "url": (enr.page.url or "") if enr.page is not None else "",
    }


def _end(enr, state):
    """Transisi terminal: buang secret, hentikan expire task.

    Task navigasi TIDAK disentuh di sini — hanya ``_teardown`` (yang
    menunggunya selesai) dan ``disarm_for_session`` (best-effort,
    browser sedang mati) yang boleh membatalkannya. ``_navigate_later``
    sendiri tidak boleh menunggu task-nya sendiri.
    """
    if enr.state in _TERMINAL_STATES:
        return  # sudah terminal; jangan timpa sejarah state
    enr.state = state
    enr.password = None
    if enr.expire_task is not None:
        enr.expire_task.cancel()
        enr.expire_task = None


async def _stop_navigation(enr):
    """Batalkan task navigasi yang masih berjalan dan TUNGGU dia berhenti
    sebelum page ditutup — menutup page di bawah goto yang hidup hanya
    menghasilkan error race yang harus ditelan."""
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
    """Transisi terminal + hentikan navigasi + tutup enrollment page;
    ganti page bersih pada context yang sama bila itu page terakhir
    (browser tetap hidup)."""
    _end(enr, state)
    await _stop_navigation(enr)
    page, enr.page = enr.page, None
    if page is not None:
        try:
            if not page.is_closed():
                await page.close()
        except Exception as e:  # noqa: BLE001 - best effort
            log("tutup enrollment page gagal: %s" % type(e).__name__)
    ctx = enr.ctx
    if ctx is not None:
        try:
            if not ctx.pages:
                await ctx.new_page()
        except Exception as e:  # noqa: BLE001 - best effort
            log("ganti page bersih gagal: %s" % type(e).__name__)
    await _restore_resident_page(enr)


async def _restore_resident_page(enr):
    """Pemulihan setelah enrollment page ditutup, ketika start menutup
    page resident demi satu jendela: pastikan context punya page hidup
    dan perbaiki referensi ``sess["page"]`` agar perintah berikutnya
    (navigate/inspect/relogin) tidak mengenai page yang sudah mati."""
    if not enr.took_over_window or enr.ctx is None:
        return
    try:
        pages = list(enr.ctx.pages)
        if not pages:
            pages = [await enr.ctx.new_page()]
        host = enr.host
        if host is not None:
            sess = host.sessions.get(enr.sid)
            if sess is not None:
                sess["page"] = pages[0]
    except Exception as e:  # noqa: BLE001 - best effort
        log("pemulihan page resident gagal: %s" % type(e).__name__)


async def _navigate_later(host, enr):
    """Task latar: buka halaman login Google pada enrollment page.

    Dipisah dari ``start`` agar respons tidak menunggu goto. Kegagalan
    non-browser mengakhiri enrollment dengan state ``failed`` (jujur
    dilaporkan lewat ``status``); browser mati diteruskan ke
    ``_drop_session`` yang hook-nya akan disarm enrollment ini.
    """
    try:
        await enr.page.goto(SIGNIN_URL, wait_until="domcontentloaded",
                            timeout=45000)
    except asyncio.CancelledError:
        raise  # teardown yang membatalkan; biarkan terpropagasi
    except Exception as e:  # noqa: BLE001 - klasifikasi browser/jaringan
        if is_browser_closed_error(e):
            await host._drop_session(enr.sid, forget_on_failure=True)
            return
        log("navigasi enrollment gagal: %s: %s" % (type(e).__name__, e))
        _end(enr, "failed")
        await _close_page_quietly(enr)
        await _restore_resident_page(enr)


async def _close_page_quietly(enr):
    page, enr.page = enr.page, None
    if page is None:
        return
    try:
        await page.close()
    except Exception:  # noqa: BLE001 - best effort
        pass


async def _expire_later(enr):
    """Task latar: jatuhkan enrollment yang tidak selesai dalam 10 menit.

    Berlaku juga untuk state complete — secret tidak boleh menunggu
    selamanya sebuah finish yang tidak pernah datang.
    """
    try:
        delay = max(
            0.0, enr.deadline - asyncio.get_running_loop().time())
        await asyncio.sleep(delay)
        if enr.state not in _TERMINAL_STATES:
            await _teardown(enr, "expired")
    except asyncio.CancelledError:
        pass
    except Exception as e:  # noqa: BLE001 - task latar tak boleh mati diam
        log("expire enrollment gagal: %s: %s" % (type(e).__name__, e))
