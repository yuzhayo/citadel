r"""Phase 0 RED tests — bukti cacat struktural Add Profile (tasks/plan.md).

Kontrak final (tasks/plan.md) menuntut:
  INV-1  Setiap session terdaftar mengekspos TEPAT SATU primary page
         hidup dengan tepat satu owner — kapan pun dilihat.
  INV-2  Enrollment start TIDAK memanggil ctx.new_page(); ia meng-claim
         resident primary page (satu jendela sejak awal).
  INV-3  Fitur tidak memegang akses mutable ke registry session
         (tidak ada backlink host, tidak ada dict sess mentah).
  INV-4  Command lain (navigate/inspect) pada session dengan owner
         aktif ditolak SESSION_BUSY — bukan beroperasi pada page mati.
  INV-5  Launcher memanggil Add Profile lewat SATU kontrak feature;
         tidak membuka session sendiri untuk alur itu.

File ini membuktikan pelanggaran INV-1..INV-4 pada kode SEKARANG
(red), supaya refactor Phase 1+ punya target yang teruji. INV-5
(bentuk C#) dibuktikan lewat pencarian source di
test_add_profile_launcher_boundary (juga red sekarang).

Setelah refactor selesai, file ini dipertahankan sebagai GUARD: semua
test harus hijau. Menjalankan file ini berdiri sendiri:
  <venv python> -m unittest module.sharedLogic.tests.test_add_profile_invariants -v
"""

import asyncio
import json
import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PYHOST = os.path.join(ROOT, "pyhost", "pyhost.py")
sys.path.insert(0, os.path.join(ROOT, "pyhost"))

import importlib.util  # noqa: E402

SPEC = importlib.util.spec_from_file_location("citadel_pyhost_red", PYHOST)
PYHOST_MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PYHOST_MODULE)

CAMOPROF_LAUNCHER = os.path.join(
    ROOT, "..", "camoprof", "Launcher", "LauncherView.xaml.cs")


def _load(path):
    with open(path, encoding="utf-8") as handle:
        return handle.read()


# ---- fakes: platform-true (context mati saat page terakhir ditutup) --


class _Page:
    def __init__(self, ctx):
        self._ctx = ctx
        self.url = "about:blank"
        self.closed = False
        self.exposed = {}
        self.events = []

    async def expose_function(self, name, callback):
        self.exposed[name] = callback

    async def add_init_script(self, _script):
        pass

    async def goto(self, url, **_kwargs):
        # Konteks mati = setiap operasi page melempar, seperti Playwright
        # nyata. Tanpa ini navigate pada context mati "berhasil" palsu
        # dan pelanggaran registry tersembunyi.
        if self._ctx.dead:
            raise RuntimeError(
                "Target page, context or browser has been closed")
        self.url = url

    def is_closed(self):
        return self.closed

    async def close(self):
        self.closed = True
        if self in self._ctx.pages:
            self._ctx.pages.remove(self)
        if not self._ctx.pages:
            self._ctx.dead = True

    async def evaluate(self, _script):
        if self._ctx.dead:
            raise RuntimeError(
                "Target page, context or browser has been closed")
        return []


class _Context:
    def __init__(self):
        self.pages = []
        self.dead = False

    async def new_page(self):
        if self.dead:
            raise RuntimeError("context closed")
        page = _Page(self)
        self.pages.append(page)
        return page


class _NullCm:
    async def __aexit__(self, *_args):
        return None


def _host_with_session():
    host = PYHOST_MODULE._Host()
    ctx = _Context()
    resident = _Page(ctx)
    ctx.pages.append(resident)
    host.sessions["s1"] = {
        "profile": "probe", "cm": _NullCm(), "ctx": ctx,
        "page": resident, "dir": "unused", "headless": False,
    }
    return host, ctx, resident


async def _handle(host, cmd, **params):
    return await PYHOST_MODULE._handle(
        host, {"id": 1, "cmd": cmd, **params})


# ---- INV-1 / INV-2: satu primary page hidup, tanpa page kedua --------


class PrimaryPageInvariantTest(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.host, self.ctx, self.resident = _host_with_session()

    def tearDown(self):
        for enr in self.host.enrollments.values():
            if getattr(enr, "expire_task", None) is not None:
                enr.expire_task.cancel()
            if getattr(enr, "navigate_task", None) is not None:
                enr.navigate_task.cancel()

    async def test_enrollment_start_does_not_create_a_second_page(self):
        """INV-2: start meng-claim resident page — ctx.new_page() dilarang.
        Kode sekarang membuat page kedua lalu menutup resident — jumlah
        akhirnya 1, tapi identitasnya BUKAN resident: satu jendela
        dibuat dan dibuang untuk apa-apa. Identitas page yang hidup
        setelah start harus = resident sebelumnya → red sekarang."""
        resident_before = self.resident
        response = await _handle(
            self.host, "google.enrollment.start", session="s1")
        await asyncio.sleep(0)

        self.assertTrue(response.get("ok"), str(response))
        enrollment_page = self.host.enrollments["probe"].page
        self.assertIs(
            enrollment_page, resident_before,
            "enrollment page bukan resident page yang di-claim — start "
            "masih membuat page kedua via ctx.new_page() (melanggar "
            "INV-2): satu jendela dibuat lalu resident dibuang")
        self.assertFalse(
            resident_before.closed,
            "resident page tidak boleh ditutup oleh start — INV-2: "
            "claim, bukan ganti jendela")

    async def test_registered_session_exposes_live_primary_page(self):
        """INV-1: selama enrollment AKTIF, sess['page'] harus hidup.
        Kode sekarang menutup resident saat start → red."""
        response = await _handle(
            self.host, "google.enrollment.start", session="s1")
        await asyncio.sleep(0)
        self.assertTrue(response.get("ok"), str(response))

        session_page = self.host.sessions["s1"]["page"]
        live = not getattr(session_page, "closed", True)
        self.assertTrue(
            live,
            "sess['page'] menunjuk page yang sudah ditutup saat "
            "enrollment masih aktif — melanggar INV-1")

    async def test_navigate_during_active_enrollment_is_rejected_busy(self):
        """INV-4: command lain saat enrollment aktif → SESSION_BUSY,
        bukan beroperasi pada page mati. Kode sekarang: page resident
        sudah ditutup → navigate kena page mati → red."""
        await _handle(self.host, "google.enrollment.start", session="s1")
        await asyncio.sleep(0)

        response = await _handle(
            self.host, "session.navigate",
            session="s1", url="https://example.com/")

        if response.get("ok"):
            self.fail(
                "navigate diterima selama enrollment aktif tanpa "
                "SESSION_BUSY — INV-4 dilanggar; pemilik page tidak "
                "eksklusif. respons: " + str(response))
        self.assertEqual(
            response["error"]["code"], "SESSION_BUSY",
            "harusnya SESSION_BUSY, dapat: " + str(response))

    async def test_manual_window_close_releases_profile_ownership(self):
        """INV-1 + lifecycle: user menutup jendela enrollment dengan
        tangan → session HILANG dari registry (open berikutnya tidak
        kena PROFILE_BUSY palsu). Kode sekarang: status melaporkan
        browser_gone tapi session tetap terdaftar dan navigate pada
        context mati baru melempar belakangan → red."""
        await _handle(self.host, "google.enrollment.start", session="s1")
        await asyncio.sleep(0)
        enr = self.host.enrollments.get("probe")
        self.assertIsNotNone(enr)

        # User menutup jendela enrollment (bukan pyhost yang menutup).
        enrollment_page = enr.page
        await enrollment_page.close()

        status = await _handle(
            self.host, "google.enrollment.status", session="s1")
        self.assertEqual(
            status.get("state"), "browser_gone",
            "manual close jendela enrollment harus terdeteksi sebagai "
            "browser_gone, dapat: " + str(status))
        self.assertIsNone(enr.password)
        self.assertNotIn(
            "s1", self.host.sessions,
            "session tetap terdaftar setelah jendela enrollment ditutup "
            "manual — PROFILE_BUSY palsu pada open berikutnya; "
            "melanggar INV-1")


# ---- INV-3: fitur tidak memegang registry mutable --------------------


class FeatureBoundaryTest(unittest.TestCase):
    def test_enrollment_module_holds_no_registry_backlink(self):
        """INV-3: kode fitur (providers/google/enrollment.py) tidak boleh
        menyimpan referensi ke _Host/registry (enr.host, host.sessions).
        Kode sekarang menyimpan enr.host → red."""
        source = _load(os.path.join(
            ROOT, "pyhost", "providers", "google", "enrollment.py"))
        self.assertNotIn(
            "self.host", source,
            "enrollment menyimpan backlink ke registry host — melanggar "
            "INV-3; akses registry harus lewat lease, bukan referensi "
            "mutable")


class LauncherBoundaryTest(unittest.TestCase):
    def test_launcher_add_profile_opens_no_session_directly(self):
        """INV-5 (bentuk C#): Add Profile tidak boleh membuka session
        sendiri — hanya memanggil kontrak feature. Launcher sekarang
        memanggil _sessions.OpenAsync di AddProfileButton_Click → red."""
        source = _load(CAMOPROF_LAUNCHER)
        start = source.index("AddProfileButton_Click")
        end = source.index("private async void RefreshButton_Click")
        add_profile_body = source[start:end]
        self.assertNotIn(
            "_sessions.OpenAsync", add_profile_body,
            "Launcher membuka session browser langsung di alur Add "
            "Profile — melanggar INV-5; kepemilikan pembukaan session "
            "harus berada di dalam feature")


if __name__ == "__main__":
    unittest.main(verbosity=2)
