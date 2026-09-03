r"""Invariant tests untuk kontrak Add Profile puzzle-block (tasks/plan.md).

Kontrak final (tasks/plan.md — puzzle-block):
  INV-1  Setiap session terdaftar selalu mengekspos primary page HIDUP
         (referensi ditukar lewat host.set_primary_page saat swap).
  INV-2  SATU jendela terlihat: setelah start, tepat satu page hidup di
         context; listener armed SEBELUM navigasi page itu.
  INV-3  Fitur tidak menyimpan backlink host (host/sess hanya parameter
         fungsi) dan tidak menyentuh registry di luar helper generik.
  INV-4  Secret mati bersama session: manual close jendela enrollment →
         session hilang dari registry (tidak ada PROFILE_BUSY palsu).
  INV-5  Launcher memanggil Add Profile lewat SATU kontrak feature;
         tidak membuka session sendiri untuk alur itu.

File ini GUARD permanen: semua harus hijau. Menjalankan sendiri:
  <venv python> -m unittest module.sharedLogic.tests.test_add_profile_invariants -v
"""

import asyncio
import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PYHOST = os.path.join(ROOT, "pyhost", "pyhost.py")
sys.path.insert(0, os.path.join(ROOT, "pyhost"))
sys.path.insert(0, os.path.join(
    ROOT, "..", "camoprof", "Features", "AddProfile"))

import importlib.util  # noqa: E402

os.environ.setdefault("CITADEL_PYHOST_PLUGINS", "camoprof_add_profile")

SPEC = importlib.util.spec_from_file_location("citadel_pyhost_inv", PYHOST)
PYHOST_MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PYHOST_MODULE)

CAMOPROF_LAUNCHER = os.path.join(
    ROOT, "..", "camoprof", "Launcher", "LauncherView.xaml.cs")
PLUGIN_ENROLLMENT = os.path.join(
    ROOT, "..", "camoprof", "Features", "AddProfile",
    "camoprof_add_profile", "enrollment.py")


def _load(path):
    with open(path, encoding="utf-8") as handle:
        return handle.read()


class _Page:
    def __init__(self, ctx):
        self._ctx = ctx
        self.url = "about:blank"
        self.closed = False
        self.exposed = {}

    async def expose_function(self, name, callback):
        self.exposed[name] = callback

    async def add_init_script(self, _script):
        pass

    async def goto(self, url, **_kwargs):
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


class OneWindowInvariantTest(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.host, self.ctx, self.resident = _host_with_session()

    def tearDown(self):
        for enr in list(getattr(
                self.host, "add_profile_enrollments", {}).values()):
            if getattr(enr, "expire_task", None) is not None:
                enr.expire_task.cancel()
            if getattr(enr, "navigate_task", None) is not None:
                enr.navigate_task.cancel()

    async def test_exactly_one_live_window_during_enrollment(self):
        """INV-2: setelah start, tepat SATU page hidup — resident
        ditutup setelah page enrollment hidup; tidak ada jendela
        ganda, tidak ada page dibuat-lalu-dibuang."""
        response = await _handle(
            self.host, "camoprof.add_profile.start", session="s1")
        await asyncio.sleep(0)

        self.assertTrue(response.get("ok"), str(response))
        self.assertTrue(self.resident.closed,
                        "resident harus ditutup demi satu jendela")
        alive = [page for page in self.ctx.pages if not page.closed]
        self.assertEqual(
            len(alive), 1,
            "setelah start harus tepat satu page hidup, ada %d"
            % len(alive))
        enr = self.host.add_profile_enrollments["probe"]
        self.assertIs(enr.page, alive[0])

    async def test_registered_session_exposes_live_primary_page(self):
        """INV-1: selama enrollment AKTIF, sess['page'] harus hidup."""
        response = await _handle(
            self.host, "camoprof.add_profile.start", session="s1")
        await asyncio.sleep(0)
        self.assertTrue(response.get("ok"), str(response))

        session_page = self.host.sessions["s1"]["page"]
        live = not getattr(session_page, "closed", True)
        self.assertTrue(
            live,
            "sess['page'] menunjuk page mati saat enrollment aktif — "
            "melanggar INV-1")

    async def test_teardown_ends_the_flow_browser(self):
        """INV-1 bentuk akhirnya: teardown menutup page enrollment,
        context mati, dan session dijatuhkan — tidak pernah ada session
        terdaftar dengan page mati, dan tidak ada page pengganti."""
        await _handle(
            self.host, "camoprof.add_profile.start", session="s1")
        await asyncio.sleep(0)
        await _handle(
            self.host, "camoprof.add_profile.cancel", session="s1")

        self.assertNotIn(
            "s1", self.host.sessions,
            "session milik flow harus dijatuhkan saat teardown")
        self.assertTrue(self.ctx.dead,
                        "context harus mati — flow berakhir, browser tutup")

    async def test_manual_window_close_drops_session(self):
        """INV-4: user menutup jendela enrollment manual → session
        HILANG dari registry (tidak ada PROFILE_BUSY palsu) dan secret
        dibuang."""
        await _handle(
            self.host, "camoprof.add_profile.start", session="s1")
        await asyncio.sleep(0)
        enr = self.host.add_profile_enrollments["probe"]

        await enr.page.close()  # user menutup jendela

        status = await _handle(
            self.host, "camoprof.add_profile.status", session="s1")
        self.assertEqual(
            status.get("state"), "browser_gone",
            "manual close harus terdeteksi browser_gone, dapat: "
            + str(status))
        self.assertIsNone(enr.password)
        self.assertNotIn(
            "s1", self.host.sessions,
            "session tetap terdaftar setelah jendela ditutup manual — "
            "PROFILE_BUSY palsu; melanggar INV-4")


class FeatureBoundaryTest(unittest.TestCase):
    def test_enrollment_module_holds_no_host_backlink(self):
        """INV-3: kode fitur tidak menyimpan backlink host
        (self.host/enr.host) — host hanya parameter fungsi."""
        source = _load(PLUGIN_ENROLLMENT)
        self.assertNotIn("self.host", source)
        self.assertNotIn("enr.host", source)

    def test_plugin_does_not_touch_session_registry(self):
        """INV-3: plugin tidak menulis registry session di luar helper
        generik (tidak ada host.sessions di commands/plugin)."""
        base = os.path.dirname(PLUGIN_ENROLLMENT)
        for name in ("commands.py", "plugin.py"):
            source = _load(os.path.join(base, name))
            self.assertNotIn(
                "host.sessions", source,
                "%s menyentuh registry session langsung" % name)


class LauncherBoundaryTest(unittest.TestCase):
    def test_launcher_add_profile_opens_no_session_directly(self):
        """INV-5: Add Profile di Launcher hanya memanggil kontrak
        feature — tidak membuka session sendiri."""
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
