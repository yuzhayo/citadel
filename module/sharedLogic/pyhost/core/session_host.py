"""SessionHost: registry session privat + lease primary-page.

Kontrak (tasks/plan.md keputusan 2 & 3):

- HANYA SessionHost membaca/menulis registry session. Kode fitur tidak
  memegang dict session mentah dan tidak menyimpan backlink ke host —
  satu-satunya pintu adalah ``lease``.
- Untuk setiap session terdaftar, ``primary page`` hidup dan punya
  TEPAT SATU owner. Command lain (navigate/inspect) saat owner aktif
  ditolak ``SESSION_BUSY`` — bukan beroperasi di page yang dimiliki
  atau sudah mati.
- Menutup page TERAKHIR sebuah context mematikan context di
  Playwright — karena itu rotasi page selalu membuat page pengganti
  DULU baru menutup page lama (``rotate_primary``).

Tidak ada satu kata Google/enrollment/Add Profile di file ini: core
adalah infrastruktur generik. Plugin-fitur memanggil API publik
(``claim_primary``/``lease``) tanpa tahu bentuk internal registry.
"""

from providers import PyhostError, log


class SessionLease:
    """Akses eksklusif ke primary page sebuah session, milik satu owner.

    Lease dibuat HANYA oleh SessionHost.claim_primary; setelah
    ``release``/session mati, semua operasi lease menolak keras
    (LEASE_RELEASED) — invalid ownership tidak bisa dioperasikan.
    """

    def __init__(self, host, sid):
        self._host = host
        self._sid = sid
        self._released = False

    @property
    def sid(self):
        return self._sid

    def _live(self):
        if self._released:
            raise PyhostError("LEASE_RELEASED", "lease sudah dilepas")
        sess = self._host.sessions.get(self._sid)
        if sess is None:
            self._released = True
            raise PyhostError("SESSION_NOT_FOUND", "session: %r" % self._sid)
        return sess

    @property
    def page(self):
        """Primary page hidup sesuai owner lease ini."""
        sess = self._live()
        page = sess.get("page")
        if page is None or _page_is_closed(page):
            raise PyhostError(
                "PAGE_DEAD",
                "primary page session %s tidak hidup" % self._sid)
        return page

    async def rotate_primary(self):
        """Ganti primary page dengan page bersih baru, aman dari
        kematian context: page baru dibuat DULU, ownership berpindah,
        baru page lama ditutup. Context tidak pernah kehabisan page."""
        sess = self._live()
        old = sess.get("page")
        new = await self._host.new_page_for(self._sid)
        sess["page"] = new
        if old is not None and old is not new:
            try:
                if not _page_is_closed(old):
                    await old.close()
            except Exception as e:  # noqa: BLE001 - best effort
                log("tutup page lama %s: %s" % (self._sid, type(e).__name__))
        return new

    async def set_primary(self, page):
        """Jadikan ``page`` primary (page hidup milik context session).
        Dipakai fitur yang meng-claim resident page: resident menjadi
        primary yang sah tanpa page baru dibuat."""
        sess = self._live()
        if page is None or _page_is_closed(page):
            raise PyhostError("PAGE_DEAD", "page pengganti tidak hidup")
        sess["page"] = page
        return page

    def drop_session(self):
        """Session ini terbukti mati (page/context-nya). Lepas owner +
        hapus dari registry, lalu tandai lease mati. Pembersihan async
        context tetap jalur tanggung jawab host (``_drop_session``)."""
        if not self._released:
            self._released = True
            self._host.drop_dead_session(self._sid)

    def release(self):
        """Lepas kepemilikan — idempotent. Session tetap terdaftar."""
        if not self._released:
            self._released = True
            self._host._release_owner(self._sid)

    def is_released(self):
        return self._released


def _page_is_closed(page):
    try:
        return bool(page.is_closed())
    except Exception:  # noqa: BLE001 - page mati dianggap tertutup
        return True


class SessionHost:
    """Pemilik tunggal registry session + peta owner primary page."""

    def __init__(self, sessions):
        # sessions: dict sid -> sess (tetap milik _Host untuk
        # compatibility command yang sudah ada; akses fitur HANYA
        # lewat SessionHost API).
        self.sessions = sessions
        self._owners = {}  # sid -> owner token (str)

    def owner_of(self, sid):
        return self._owners.get(sid)

    def claim_primary(self, sid, owner):
        """Ambil lease eksklusif. Owner lain aktif -> SESSION_BUSY."""
        if sid not in self.sessions:
            raise PyhostError("SESSION_NOT_FOUND", "session: %r" % (sid,))
        current = self._owners.get(sid)
        if current is not None and current != owner:
            raise PyhostError(
                "SESSION_BUSY",
                "session %s dipakai oleh %r" % (sid, current))
        self._owners[sid] = owner
        return SessionLease(self, sid)

    def _release_owner(self, sid):
        # Hanya dipanggil SessionLease.release (idempotent).
        self._owners.pop(sid, None)

    async def new_page_for(self, sid):
        sess = self.sessions.get(sid)
        if sess is None:
            raise PyhostError("SESSION_NOT_FOUND", "session: %r" % (sid,))
        ctx = sess.get("ctx")
        if ctx is None:
            raise PyhostError(
                "PAGE_DEAD", "session %s belum punya context" % sid)
        return await ctx.new_page()

    def guard_page_user(self, sid, who):
        """Halangi command non-owner memakai primary page session yang
        sedang dimiliki fitur (contoh: navigate saat enrollment aktif).
        Dipanggil command inti sebelum menyentuh sess["page"]."""
        owner = self._owners.get(sid)
        if owner is not None and owner != who:
            raise PyhostError(
                "SESSION_BUSY",
                "session %s dipakai oleh %r" % (sid, owner))

    def drop_dead_session(self, sid):
        """Halangi pengeksposan session dengan page/context mati (INV-1).

        Dipanggil oleh apapun yang baru saja membuktikan page/session
        mati (contoh: fitur mendeteksi jendela enrollment ditutup
        manual). Melepas owner dan MENGHAPUS session dari registry —
        pembersihan context async tetap tanggung jawab pemanggil via
        ``drop_session``-nya host; fungsi ini sinkron dan tidak gagal.
        """
        self._owners.pop(sid, None)
        self.sessions.pop(sid, None)
