r"""Protocol-level tests for pyhost (stdlib, no pytest dependency).

Run with the runtime venv:
  <runtime>\.venv\Scripts\python.exe -m unittest module.sharedLogic.tests.test_pyhost -v

These do NOT open a browser — they cover the NDJSON contract, name/loop
guards and lifecycle. Live browser flow is exercised by the smoke driver
referenced in pyhost/README.md.
"""

import json
import asyncio
import importlib.util
import os
import subprocess
import sys
import tempfile
import types
import unittest
from unittest import mock

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # sharedLogic/
PYHOST = os.path.join(ROOT, "pyhost", "pyhost.py")

# pyhost.py mengimpor paket `providers` yang tinggal di sebelahnya
# (repo maupun payload ter-deploy); sebagai script sys.path[0] sudah
# benar, tapi loader ini mengeksekusi file langsung — jadi daftarkan
# foldernya secara eksplisit.
sys.path.insert(0, os.path.join(ROOT, "pyhost"))

SPEC = importlib.util.spec_from_file_location("citadel_pyhost_tests", PYHOST)
PYHOST_MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PYHOST_MODULE)


def _venv_python():
    return os.path.join(
        os.environ["LOCALAPPDATA"], "Citadel", "runtime", ".venv",
        "Scripts", "python.exe")


def _close_process_pipes(proc):
    for stream in (proc.stdin, proc.stdout, proc.stderr):
        if stream is not None and not stream.closed:
            stream.close()


class PyHostProtocolTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.credenz = tempfile.TemporaryDirectory(prefix="CitadelPyhostTests-")
        cls.proc = subprocess.Popen(
            [_venv_python(), "-u", PYHOST],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, encoding="utf-8",
            env={**os.environ, "CITADEL_CREDENZ": cls.credenz.name})
        cls._id = 0

    @classmethod
    def tearDownClass(cls):
        if cls.proc.poll() is None:
            try:
                cls.call("shutdown")
            except Exception:
                pass
            try:
                cls.proc.stdin.close()
            except Exception:
                pass
        try:
            cls.proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            cls.proc.kill()
        cls.proc.stderr.read()
        _close_process_pipes(cls.proc)
        cls.credenz.cleanup()

    @classmethod
    def call(cls, command, **params):
        cls._id += 1
        request = {"id": cls._id, "cmd": command, **params}
        cls.proc.stdin.write(json.dumps(request) + "\n")
        cls.proc.stdin.flush()
        line = cls.proc.stdout.readline()
        cls.assertTrue(line, "expected a response line (EOF?)")
        return json.loads(line)

    def test_ping(self):
        r = self.call("ping")
        self.assertTrue(r["ok"])
        self.assertEqual(r["protocol"], 1)
        python_version = [int(part) for part in r["python"].split(".")[0:2]]
        self.assertEqual(python_version[0], 3)
        self.assertGreaterEqual(python_version[1], 12)
        self.assertEqual(r["camoufox"], "0.5.5")
        self.assertTrue(r["playwright"].startswith("1.51."))
        self.assertTrue(r["credenz_ready"])

    def test_unknown_command(self):
        r = self.call("no.such.command")
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "UNKNOWN_COMMAND")

    def test_bad_json(self):
        self.proc.stdin.write("this is not json\n")
        self.proc.stdin.flush()
        r = json.loads(self.proc.stdout.readline())
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "BAD_JSON")

    def test_array_request_rejected(self):
        self.proc.stdin.write("[]\n")
        self.proc.stdin.flush()
        r = json.loads(self.proc.stdout.readline())
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "BAD_JSON")

    def test_profile_name_guards(self):
        for bad in ("../evil", ".", "..", "a/b", "a\\b", "", "sp ace"):
            r = self.call("session.open", profile=bad)
            self.assertFalse(r["ok"], bad)
            self.assertEqual(r["error"]["code"], "BAD_PROFILE_NAME", bad)

    def test_path_escape_guard(self):
        # Exercise the actual realpath/commonpath branch. A valid-looking name
        # may still resolve outside through a junction/symlink; mocking the two
        # canonical paths keeps the test portable without admin symlink rights.
        host = PYHOST_MODULE._Host()
        host.credenz = r"C:\vault"
        with mock.patch.object(
                PYHOST_MODULE.os.path,
                "realpath",
                side_effect=[r"C:\vault\google\profiles", r"C:\outside"]):
            with self.assertRaises(PYHOST_MODULE._PyhostError) as caught:
                host._profile_dir("looks-safe")
        self.assertEqual(caught.exception.code, "PATH_ESCAPE")

    def test_missing_session(self):
        r = self.call("session.close", session="does-not-exist")
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "SESSION_NOT_FOUND")


class PyHostCancellationTest(unittest.IsolatedAsyncioTestCase):
    @staticmethod
    def _fake_camoufox(factory):
        api = types.ModuleType("camoufox.async_api")
        api.AsyncCamoufox = factory
        package = types.ModuleType("camoufox")
        package.async_api = api
        return {"camoufox": package, "camoufox.async_api": api}

    async def test_open_timeout_closes_and_forgets_partial_session(self):
        state = {"exit_called": False}

        class SlowEnterContext:
            async def __aenter__(self):
                await asyncio.sleep(1)

            async def __aexit__(self, *_args):
                state["exit_called"] = True

        with tempfile.TemporaryDirectory(prefix="CitadelOpenTimeout-") as root:
            host = PYHOST_MODULE._Host()
            host.credenz = root
            modules = self._fake_camoufox(lambda **_kwargs: SlowEnterContext())
            with mock.patch.dict(sys.modules, modules):
                response = await PYHOST_MODULE._handle(host, {
                    "id": 1,
                    "cmd": "session.open",
                    "profile": "probe",
                    "timeout": 0.01,
                })

        self.assertEqual(response["error"]["code"], "TIMEOUT")
        self.assertTrue(state["exit_called"])
        self.assertEqual(host.sessions, {})

    async def test_close_timeout_keeps_session_for_retry(self):
        state = {"delay": True, "completed": False}

        class SlowExitContext:
            async def __aexit__(self, *_args):
                if state["delay"]:
                    await asyncio.sleep(1)
                state["completed"] = True

        host = PYHOST_MODULE._Host()
        host.sessions["s1"] = {
            "profile": "probe",
            "cm": SlowExitContext(),
            "ctx": object(),
            "page": object(),
            "dir": "unused",
        }
        response = await PYHOST_MODULE._handle(host, {
            "id": 1,
            "cmd": "session.close",
            "session": "s1",
            "timeout": 0.01,
        })

        self.assertEqual(response["error"]["code"], "TIMEOUT")
        self.assertIn("s1", host.sessions)
        self.assertFalse(state["completed"])

        state["delay"] = False
        self.assertTrue(await host._drop_session("s1"))
        self.assertNotIn("s1", host.sessions)

    async def test_close_failure_keeps_session_for_retry(self):
        class FailingExitContext:
            async def __aexit__(self, *_args):
                raise RuntimeError("close failed")

        host = PYHOST_MODULE._Host()
        host.sessions["s1"] = {
            "profile": "probe",
            "cm": FailingExitContext(),
            "ctx": object(),
            "page": object(),
            "dir": "unused",
        }
        response = await PYHOST_MODULE._handle(host, {
            "id": 1,
            "cmd": "session.close",
            "session": "s1",
        })

        self.assertEqual(response["error"]["code"], "BROWSER_CLOSE_FAILED")
        self.assertIn("s1", host.sessions)
        host.sessions.clear()

    async def test_verify_network_failure_keeps_session(self):
        class Context:
            async def __aexit__(self, *_args):
                return None

        class NetworkFailurePage:
            async def goto(self, *_args, **_kwargs):
                raise RuntimeError("net::ERR_NAME_NOT_RESOLVED")

        host = PYHOST_MODULE._Host()
        host.sessions["s1"] = {
            "profile": "probe",
            "cm": Context(),
            "ctx": object(),
            "page": NetworkFailurePage(),
            "dir": "unused",
        }
        response = await PYHOST_MODULE._handle(host, {
            "id": 1,
            "cmd": "session.verify",
            "session": "s1",
        })

        self.assertEqual(response["error"]["code"], "VERIFY_FAILED")
        self.assertIn("s1", host.sessions)
        await host._drop_session("s1")

    async def test_navigate_accepts_http_url_and_keeps_session(self):
        class Page:
            url = "about:blank"

            async def goto(self, url, **_kwargs):
                self.url = url

        host = PYHOST_MODULE._Host()
        host.sessions["s1"] = {
            "profile": "probe",
            "cm": object(),
            "ctx": object(),
            "page": Page(),
            "dir": "unused",
        }
        response = await PYHOST_MODULE._handle(host, {
            "id": 1,
            "cmd": "session.navigate",
            "session": "s1",
            "url": "https://github.com/",
        })

        self.assertTrue(response["ok"])
        self.assertEqual(response["url"], "https://github.com/")
        self.assertIn("s1", host.sessions)
        host.sessions.clear()

    async def test_navigate_rejects_non_http_url(self):
        host = PYHOST_MODULE._Host()
        host.sessions["s1"] = {
            "profile": "probe",
            "cm": object(),
            "ctx": object(),
            "page": object(),
            "dir": "unused",
        }
        response = await PYHOST_MODULE._handle(host, {
            "id": 1,
            "cmd": "session.navigate",
            "session": "s1",
            "url": "file:///C:/secret.txt",
        })

        self.assertEqual(response["error"]["code"], "BAD_URL")
        self.assertIn("s1", host.sessions)
        host.sessions.clear()

    async def test_verify_closed_browser_forgets_session(self):
        class Context:
            async def __aexit__(self, *_args):
                return None

        class ClosedPage:
            async def goto(self, *_args, **_kwargs):
                raise RuntimeError(
                    "Target page, context or browser has been closed")

        host = PYHOST_MODULE._Host()
        host.sessions["s1"] = {
            "profile": "probe",
            "cm": Context(),
            "ctx": object(),
            "page": ClosedPage(),
            "dir": "unused",
        }
        response = await PYHOST_MODULE._handle(host, {
            "id": 1,
            "cmd": "session.verify",
            "session": "s1",
        })

        self.assertEqual(response["error"]["code"], "BROWSER_GONE")
        self.assertNotIn("s1", host.sessions)

    async def test_verify_requires_exact_myaccount_hostname(self):
        class Context:
            async def __aexit__(self, *_args):
                return None

        class LookalikePage:
            url = "https://example.invalid/?next=myaccount.google.com"

            async def goto(self, *_args, **_kwargs):
                return None

            async def wait_for_timeout(self, _milliseconds):
                return None

        host = PYHOST_MODULE._Host()
        host.sessions["s1"] = {
            "profile": "probe",
            "cm": Context(),
            "ctx": object(),
            "page": LookalikePage(),
            "dir": "unused",
        }
        response = await PYHOST_MODULE._handle(host, {
            "id": 1,
            "cmd": "session.verify",
            "session": "s1",
        })

        self.assertTrue(response["ok"])
        self.assertFalse(response["alive"])
        await host._drop_session("s1")

    async def test_open_forwards_headless_mode(self):
        captured = {}

        class Page:
            async def goto(self, *_args, **_kwargs):
                return None

        class Context:
            pages = [Page()]

            async def __aexit__(self, *_args):
                return None

        class CamoufoxContext:
            async def __aenter__(self):
                return Context()

            async def __aexit__(self, *_args):
                return None

        def factory(**kwargs):
            captured.update(kwargs)
            return CamoufoxContext()

        with tempfile.TemporaryDirectory(prefix="CitadelHeadless-") as root:
            host = PYHOST_MODULE._Host()
            host.credenz = root
            with mock.patch.dict(sys.modules, self._fake_camoufox(factory)):
                response = await PYHOST_MODULE._handle(host, {
                    "id": 1,
                    "cmd": "session.open",
                    "profile": "probe",
                    "headless": True,
                })
            self.assertTrue(response["ok"])
            self.assertTrue(response["headless"])
            self.assertTrue(captured["headless"])
            await host._drop_session(response["session"])

    async def test_google_inspect_detects_email(self):
        class Context:
            async def __aexit__(self, *_args):
                return None

            async def storage_state(self, **_kwargs):
                return None

        class Page:
            url = "https://myaccount.google.com/"

            async def goto(self, *_args, **_kwargs):
                return None

            async def wait_for_timeout(self, _milliseconds):
                return None

            async def evaluate(self, _script):
                return ["Google Account: Example (User.Name@gmail.com)"]

        host = PYHOST_MODULE._Host()
        host.sessions["s1"] = {
            "profile": "probe",
            "cm": Context(),
            "ctx": Context(),
            "page": Page(),
            "dir": "unused",
            "headless": True,
        }
        response = await PYHOST_MODULE._handle(host, {
            "id": 1,
            "cmd": "google.inspect",
            "session": "s1",
        })

        self.assertTrue(response["ok"])
        self.assertEqual(response["state"], "active")
        self.assertEqual(response["email"], "user.name@gmail.com")
        await host._drop_session("s1")

    async def test_google_inspect_signed_out_has_no_identity(self):
        class Context:
            async def __aexit__(self, *_args):
                return None

        class Page:
            url = "https://accounts.google.com/v3/signin/identifier"

            async def goto(self, *_args, **_kwargs):
                return None

            async def wait_for_timeout(self, _milliseconds):
                return None

        host = PYHOST_MODULE._Host()
        host.sessions["s1"] = {
            "profile": "probe",
            "cm": Context(),
            "ctx": object(),
            "page": Page(),
            "dir": "unused",
            "headless": True,
        }
        response = await PYHOST_MODULE._handle(host, {
            "id": 1,
            "cmd": "google.inspect",
            "session": "s1",
        })

        self.assertTrue(response["ok"])
        self.assertEqual(response["state"], "signed_out")
        self.assertIsNone(response["email"])
        await host._drop_session("s1")

    async def test_google_relogin_refuses_headless_session(self):
        host = PYHOST_MODULE._Host()
        host.sessions["s1"] = {
            "profile": "probe",
            "cm": object(),
            "ctx": object(),
            "page": object(),
            "dir": "unused",
            "headless": True,
        }
        response = await PYHOST_MODULE._handle(host, {
            "id": 1,
            "cmd": "google.relogin",
            "session": "s1",
            "email": "user@gmail.com",
            "password": "secret-not-echoed",
        })

        self.assertEqual(response["error"]["code"], "HEADLESS_RELOGIN")
        self.assertNotIn("secret-not-echoed", json.dumps(response))
        host.sessions.clear()

    async def test_google_relogin_returns_action_required_for_challenge(self):
        class Locator:
            def __init__(self, visible=True):
                self.first = self
                self.visible = visible

            async def is_visible(self, **_kwargs):
                return self.visible

            async def fill(self, _value):
                return None

            async def click(self):
                return None

            async def press(self, _key):
                return None

        class Page:
            url = "https://accounts.google.com/signin/challenge/selection"

            async def goto(self, *_args, **_kwargs):
                return None

            def locator(self, _selector):
                return Locator()

            async def wait_for_selector(self, *_args, **_kwargs):
                raise RuntimeError("challenge shown before password")

        host = PYHOST_MODULE._Host()
        host.sessions["s1"] = {
            "profile": "probe",
            "cm": object(),
            "ctx": object(),
            "page": Page(),
            "dir": "unused",
            "headless": False,
        }
        response = await PYHOST_MODULE._handle(host, {
            "id": 1,
            "cmd": "google.relogin",
            "session": "s1",
            "email": "user@gmail.com",
            "password": "secret-not-echoed",
        })

        self.assertTrue(response["ok"])
        self.assertEqual(response["state"], "action_required")
        self.assertNotIn("secret-not-echoed", json.dumps(response))
        host.sessions.clear()

    async def test_google_relogin_active_returns_matching_email(self):
        class Locator:
            def __init__(self):
                self.first = self

            async def is_visible(self, **_kwargs):
                return True

            async def fill(self, _value):
                return None

            async def click(self):
                return None

            async def press(self, _key):
                return None

        class Context:
            async def storage_state(self, **_kwargs):
                return None

        class Page:
            url = "https://accounts.google.com/signin/v2/challenge/pwd"

            async def goto(self, *_args, **_kwargs):
                return None

            def locator(self, _selector):
                return Locator()

            async def wait_for_selector(self, *_args, **_kwargs):
                return None

            async def wait_for_timeout(self, _milliseconds):
                self.url = "https://myaccount.google.com/"

            async def evaluate(self, _script):
                return ["Google Account: User (user@gmail.com)"]

        host = PYHOST_MODULE._Host()
        host.sessions["s1"] = {
            "profile": "probe",
            "cm": object(),
            "ctx": Context(),
            "page": Page(),
            "dir": "unused",
            "headless": False,
        }
        response = await PYHOST_MODULE._handle(host, {
            "id": 1,
            "cmd": "google.relogin",
            "session": "s1",
            "email": "user@gmail.com",
            "password": "secret-not-echoed",
        })

        self.assertTrue(response["ok"])
        self.assertEqual(response["state"], "active")
        self.assertEqual(response["email"], "user@gmail.com")
        self.assertNotIn("secret-not-echoed", json.dumps(response))
        host.sessions.clear()


class PyHostLifecycleTest(unittest.TestCase):
    """shutdown and EOF kill the host — each gets its own process so the
    shared protocol host above stays alive until tearDownClass."""

    @staticmethod
    def _spawn():
        credenz = tempfile.TemporaryDirectory(prefix="CitadelPyhostLifecycle-")
        proc = subprocess.Popen(
            [_venv_python(), "-u", PYHOST],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, encoding="utf-8",
            env={**os.environ, "CITADEL_CREDENZ": credenz.name})
        return proc, credenz

    def test_shutdown_exits_cleanly(self):
        proc, credenz = self._spawn()
        try:
            proc.stdin.write('{"id":1,"cmd":"shutdown"}\n')
            proc.stdin.flush()
            response = json.loads(proc.stdout.readline())
            self.assertTrue(response["ok"])
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()
                self.fail("pyhost did not exit after shutdown")
            self.assertEqual(proc.returncode, 0)
        finally:
            _close_process_pipes(proc)
            credenz.cleanup()

    def test_stdin_eof_exits_cleanly(self):
        # The orphan guard: parent died → pipe closed → host cleans up and
        # exits on its own. A pending request must be failed, not hung.
        proc, credenz = self._spawn()
        try:
            proc.stdin.write('{"id":1,"cmd":"ping"}\n')
            proc.stdin.flush()
            response = json.loads(proc.stdout.readline())
            self.assertTrue(response["ok"])
            proc.stdin.close()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()
                self.fail("pyhost did not exit on stdin EOF")
            self.assertEqual(proc.returncode, 0)
        finally:
            _close_process_pipes(proc)
            credenz.cleanup()


class _FakeEnrollmentPage:
    """Page enrollment: merekam urutan arm/navigate dan callback expose.

    ``goto_gate`` membuat goto menggantung sampai gate diset — untuk
    membuktikan bahwa start/cancel tidak pernah menunggu navigasi.
    ``goto_error`` membuat goto melempar — untuk uji kegagalan navigasi.
    """

    def __init__(self, ctx, events, goto_gate=None, goto_error=None):
        self._ctx = ctx
        self.events = events
        self._goto_gate = goto_gate
        self._goto_error = goto_error
        self.url = "about:blank"
        self.closed = False
        self.exposed = {}
        self.init_scripts = []
        self.evaluate_result = []

    async def expose_function(self, name, callback):
        self.events.append("expose")
        self.exposed[name] = callback

    async def add_init_script(self, script):
        self.events.append("init")
        self.init_scripts.append(script)

    async def goto(self, url, **_kwargs):
        self.events.append("goto")
        if self._goto_error is not None:
            raise self._goto_error
        if self._goto_gate is not None:
            await self._goto_gate.wait()
        self.url = url

    def is_closed(self):
        return self.closed

    async def close(self):
        self.closed = True
        if self in self._ctx.pages:
            self._ctx.pages.remove(self)

    async def evaluate(self, _script):
        return self.evaluate_result


class _FakeEnrollmentContext:
    def __init__(self, goto_gate=None, goto_error=None):
        self.events = []
        self.pages = []
        self.replacement_pages = 0
        self._goto_gate = goto_gate
        self._goto_error = goto_error

    async def new_page(self):
        # Halaman pengganti (teardown) juga dihitung agar test bisa
        # membedakan "page bersih" dari page enrollment.
        self.replacement_pages += 1
        page = _FakeEnrollmentPage(
            self, self.events, self._goto_gate, self._goto_error)
        self.pages.append(page)
        return page


class _NullContextManager:
    async def __aexit__(self, *_args):
        return None


class _YieldingContextManager:
    """Context close yang benar-benar yield — sebuah cancel yang masuk
    saat __aexit__ akan terlihat sebagai CancelledError, meniru context
    close async yang sesungguhnya (browser dying)."""

    async def __aexit__(self, *_args):
        await asyncio.sleep(0)
        return None


class PyHostEnrollmentTest(unittest.IsolatedAsyncioTestCase):
    """google.enrollment — urutan arm, capture tervalidasi, state
    machine, finish satu kali, dan cleanup per jalur kematian session."""

    SIGNIN = "https://accounts.google.com/signin/v2/identifier"
    MYACCOUNT = "https://myaccount.google.com/"

    def _make_host(self, headless=False, goto_gate=None, goto_error=None):
        host = PYHOST_MODULE._Host()
        ctx = _FakeEnrollmentContext(
            goto_gate=goto_gate, goto_error=goto_error)
        # Page resident "netral" seperti yang dibuat session.open —
        # enrollment start harus menutupnya demi satu jendela.
        resident = _FakeEnrollmentPage(ctx, ctx.events)
        ctx.pages.append(resident)
        host.sessions["s1"] = {
            "profile": "probe",
            "cm": _NullContextManager(),
            "ctx": ctx,
            "page": resident,
            "dir": "unused",
            "headless": headless,
        }
        return host, ctx

    async def _start(self, host, **params):
        return await PYHOST_MODULE._handle(host, {
            "id": 1, "cmd": "google.enrollment.start", "session": "s1",
            **params})

    async def _status(self, host):
        return await PYHOST_MODULE._handle(host, {
            "id": 2, "cmd": "google.enrollment.status", "session": "s1"})

    async def _finish(self, host):
        return await PYHOST_MODULE._handle(host, {
            "id": 3, "cmd": "google.enrollment.finish", "session": "s1"})

    async def _cancel(self, host):
        return await PYHOST_MODULE._handle(host, {
            "id": 4, "cmd": "google.enrollment.cancel", "session": "s1"})

    def _cancel_pending_tasks(self, host):
        for enr in host.enrollments.values():
            if enr.expire_task is not None:
                enr.expire_task.cancel()

    async def _type_password(self, host, value):
        """Simulasikan event input dari field password Google."""
        enr = host.enrollments["probe"]
        callback = next(iter(enr.page.exposed.values()))
        await callback(value)

    async def test_start_leaves_exactly_one_visible_window(self):
        # Regression: ctx.new_page() di browser headed adalah jendela
        # baru — membiarkan page resident netral terbuka membuat user
        # melihat "CamoFox dibuka 2x". Start harus menutup resident;
        # teardown memulihkan page hidup DAN referensi sess["page"].
        host, ctx = self._make_host()
        resident = host.sessions["s1"]["page"]

        await self._start(host)
        await asyncio.sleep(0)  # biarkan task navigasi berjalan

        self.assertTrue(resident.closed)
        alive = [page for page in ctx.pages if not page.closed]
        self.assertEqual(len(alive), 1)
        self.assertIs(host.enrollments["probe"].page, alive[0])

        await self._cancel(host)
        alive = [page for page in ctx.pages if not page.closed]
        self.assertEqual(len(alive), 1)
        # Perintah berikutnya (navigate/inspect) mengenai page hidup.
        self.assertIs(host.sessions["s1"]["page"], alive[0])
        self.assertFalse(alive[0].closed)

    async def test_navigation_failure_restores_resident_page(self):
        # Jalur failed juga harus memulihkan page resident: tanpa itu,
        # context kehabisan page hidup setelah resident ditutup.
        host, ctx = self._make_host(
            goto_error=RuntimeError("net::ERR_NAME_NOT_RESOLVED"))
        resident = host.sessions["s1"]["page"]

        await self._start(host)
        await asyncio.sleep(0)

        self.assertTrue(resident.closed)
        after = await self._status(host)
        self.assertEqual(after["state"], "failed")
        alive = [page for page in ctx.pages if not page.closed]
        self.assertEqual(len(alive), 1)
        self.assertIs(host.sessions["s1"]["page"], alive[0])

    async def test_start_arms_listener_before_navigation(self):
        host, ctx = self._make_host()
        response = await self._start(host)
        self.assertTrue(response["ok"])
        self.assertEqual(response["state"], "armed")
        page = host.enrollments["probe"].page
        # Navigasi berjalan sebagai task latar — beri satu yield agar
        # task itu sempat dieksekusi sebelum urutan diperiksa.
        await asyncio.sleep(0)
        # Kontrak urutan: listener hidup SEBELUM halaman login dibuka.
        self.assertEqual(page.events, ["expose", "init", "goto"])
        self.assertEqual(len(page.exposed), 1)
        self.assertIn("accounts.google.com", "".join(page.init_scripts))
        self.assertIn('input[type=\\"password\\"]',
                      "".join(page.init_scripts))
        self._cancel_pending_tasks(host)

    async def test_start_returns_while_navigation_pending(self):
        # goto sengaja digantung: start tetap harus kembali segera
        # setelah armed (protokol v1 berurutan — kalau start menunggu
        # goto, cancel akan mengantri di belakangnya).
        gate = asyncio.Event()
        host, _ctx = self._make_host(goto_gate=gate)
        response = await self._start(host)
        self.assertTrue(response["ok"])
        self.assertEqual(response["state"], "armed")
        # Satu yield: task navigasi mulai (merekam goto) lalu menggantung
        # di gate — membuktikan respons start tidak menunggu selesainya.
        await asyncio.sleep(0)
        enr = host.enrollments["probe"]
        self.assertIn("goto", enr.page.events)
        gate.set()
        self._cancel_pending_tasks(host)

    async def test_cancel_during_pending_navigation_is_immediate(self):
        # Regression (audit #1): Cancel saat goto masih berjalan tidak
        # boleh menunggu navigasi selesai.
        gate = asyncio.Event()
        host, _ctx = self._make_host(goto_gate=gate)
        await self._start(host)
        enr = host.enrollments["probe"]
        enrollment_page = enr.page
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")

        cancelled = await self._cancel(host)
        self.assertTrue(cancelled["ok"])
        self.assertEqual(cancelled["state"], "cancelled")
        self.assertTrue(enrollment_page.closed)
        self.assertIsNone(enr.password)

        gate.set()
        after = await self._status(host)
        self.assertEqual(after["state"], "cancelled")
        self.assertNotIn("secret-not-echoed", json.dumps(after))

    async def test_navigation_failure_ends_enrollment_failed(self):
        host, _ctx = self._make_host(
            goto_error=RuntimeError("net::ERR_NAME_NOT_RESOLVED"))
        response = await self._start(host)
        self.assertTrue(response["ok"])
        self.assertEqual(response["state"], "armed")
        # Beri task navigasi kesempatan gagal, lalu periksa statusnya.
        await asyncio.sleep(0)
        after = await self._status(host)
        self.assertEqual(after["state"], "failed")
        self.assertFalse(after["has_password"])
        self.assertTrue(host.enrollments["probe"].page is None)

    async def test_browser_gone_during_navigation_drops_session(self):
        # Regression (self-cancel): browser mati DARI DALAM task navigasi —
        # _navigate_later sendiri yang memanggil _drop_session. Disarm tidak
        # boleh mencancel task yang sedang berjalan itu; kalau iya,
        # CancelledError masuk di tengah __aexit__ (context close yang
        # benar-benar async), _drop_session sengaja tidak menelannya, dan
        # session mati tetap terdaftar — status running palsu / PROFILE_BUSY.
        host, _ctx = self._make_host(
            goto_error=RuntimeError(
                "Target page, context or browser has been closed"))
        host.sessions["s1"]["cm"] = _YieldingContextManager()

        response = await self._start(host)
        self.assertTrue(response["ok"])
        enr = host.enrollments["probe"]
        task = enr.navigate_task
        self.assertIsNotNone(task)
        await task                      # deterministik: tunggu jalur mati selesai
        self.assertFalse(task.cancelled())

        self.assertEqual(enr.state, "browser_gone")
        self.assertIsNone(enr.password)
        # Kunci reproduksi audit: session s1 harus HILANG dari registry.
        self.assertNotIn("s1", host.sessions)

    async def test_clearing_field_clears_captured_candidate(self):
        # Regression (audit #2): user mengetik, menghapus seluruh isi
        # field, lalu login via passkey — kandidat lama tidak boleh
        # ikut tersimpan.
        host, _ctx = self._make_host()
        await self._start(host)
        enr = host.enrollments["probe"]
        await asyncio.sleep(0)
        enr.page.url = self.SIGNIN
        await self._type_password(host, "typed-then-cleared")
        self.assertTrue((await self._status(host))["has_password"])
        await self._type_password(host, "")
        self.assertFalse((await self._status(host))["has_password"])

        enr.page.url = self.MYACCOUNT
        enr.page.evaluate_result = ["Account: User (user@gmail.com)"]
        response = await self._status(host)
        self.assertEqual(response["state"], "complete")
        self.assertFalse(response["has_password"])
        finished = await self._finish(host)
        self.assertTrue(finished["ok"])
        self.assertIsNone(finished["password"])
        self.assertNotIn("typed-then-cleared", json.dumps(finished))

    async def test_start_refuses_headless_session(self):
        host, _ctx = self._make_host(headless=True)
        response = await self._start(host)
        self.assertEqual(response["error"]["code"], "HEADLESS_ENROLLMENT")
        self.assertEqual(host.enrollments, {})

    async def test_start_refuses_second_active_enrollment(self):
        host, _ctx = self._make_host()
        await self._start(host)
        response = await self._start(host)
        self.assertEqual(response["error"]["code"], "ENROLLMENT_ACTIVE")
        self._cancel_pending_tasks(host)

    async def test_start_rejects_malformed_expected_email(self):
        host, _ctx = self._make_host()
        response = await self._start(host, expected_email="not-an-email")
        self.assertEqual(response["error"]["code"], "BAD_CREDENTIAL_INPUT")
        self.assertEqual(host.enrollments, {})

    async def test_capture_ignores_lookalike_origin(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = host.enrollments["probe"]
        # Validasi sisi Python: host harus TEPAT accounts.google.com.
        for url in ("https://accounts.google.com.evil.example/signin",
                    "https://example.com/signin",
                    "https://myaccount.google.com/"):
            enr.page.url = url
            await self._type_password(host, "secret-not-echoed")
        response = await self._status(host)
        self.assertTrue(response["ok"])
        self.assertFalse(response["has_password"])
        self.assertNotIn("secret-not-echoed", json.dumps(response))
        self._cancel_pending_tasks(host)

    async def test_capture_keeps_last_value(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = host.enrollments["probe"]
        enr.page.url = self.SIGNIN
        await self._type_password(host, "first-typed")
        await self._type_password(host, "second-typed")
        enr.page.url = self.MYACCOUNT
        enr.page.evaluate_result = ["Account: User (user@gmail.com)"]
        response = await self._status(host)
        self.assertEqual(response["state"], "complete")
        finished = await self._finish(host)
        self.assertTrue(finished["ok"])
        self.assertEqual(finished["email"], "user@gmail.com")
        # Ketikan ulang mengganti nilai lama.
        self.assertEqual(finished["password"], "second-typed")

    async def test_challenge_waits_for_user(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = host.enrollments["probe"]
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")
        enr.page.url = "https://accounts.google.com/signin/challenge/totp"
        response = await self._status(host)
        self.assertEqual(response["state"], "challenge")
        self.assertTrue(response["challenge"])
        self.assertTrue(response["has_password"])
        self.assertNotIn("secret-not-echoed", json.dumps(response))

    async def test_finish_is_one_shot_and_cleans_up(self):
        host, ctx = self._make_host()
        await self._start(host)
        enr = host.enrollments["probe"]
        enrollment_page = enr.page
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")
        enr.page.url = self.MYACCOUNT
        enr.page.evaluate_result = ["Account: User (user@gmail.com)"]
        await self._status(host)

        finished = await self._finish(host)
        self.assertTrue(finished["ok"])
        self.assertEqual(finished["password"], "secret-not-echoed")
        self.assertTrue(enrollment_page.closed)
        self.assertIsNone(enr.page)
        self.assertIsNone(enr.password)

        again = await self._finish(host)
        self.assertEqual(again["error"]["code"], "ENROLLMENT_CONSUMED")
        self.assertNotIn("secret-not-echoed", json.dumps(again))

        after = await self._status(host)
        self.assertEqual(after["state"], "consumed")
        self.assertFalse(after["has_password"])
        self.assertNotIn("secret-not-echoed", json.dumps(after))
        # Enrollment page diganti page bersih pada context yang sama.
        self.assertEqual(len(ctx.pages), 1)
        self.assertFalse(ctx.pages[0].closed)

    async def test_wrong_account_is_terminal_refusal(self):
        host, _ctx = self._make_host()
        await self._start(host, expected_email="expected@gmail.com")
        enr = host.enrollments["probe"]
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")
        enr.page.url = self.MYACCOUNT
        enr.page.evaluate_result = ["Account: User (actual@gmail.com)"]
        response = await self._status(host)
        self.assertEqual(response["state"], "wrong_account")
        self.assertEqual(response["email"], "actual@gmail.com")

        finished = await self._finish(host)
        self.assertEqual(finished["error"]["code"], "WRONG_ACCOUNT")
        self.assertNotIn("secret-not-echoed", json.dumps(finished))
        self.assertIsNone(enr.password)

    async def test_passkey_completes_without_password(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = host.enrollments["probe"]
        # Login tanpa password (passkey/QR) — tidak ada capture.
        enr.page.url = self.MYACCOUNT
        enr.page.evaluate_result = ["Account: User (user@gmail.com)"]
        response = await self._status(host)
        self.assertEqual(response["state"], "complete")
        self.assertFalse(response["has_password"])

        finished = await self._finish(host)
        self.assertTrue(finished["ok"])
        self.assertEqual(finished["email"], "user@gmail.com")
        self.assertIsNone(finished["password"])

    async def test_cancel_is_idempotent_and_disarms(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = host.enrollments["probe"]
        enrollment_page = enr.page
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")

        first = await self._cancel(host)
        self.assertTrue(first["ok"])
        self.assertEqual(first["state"], "cancelled")
        self.assertTrue(enrollment_page.closed)
        self.assertIsNone(enr.page)
        self.assertIsNone(enr.password)

        second = await self._cancel(host)
        self.assertTrue(second["ok"])
        self.assertEqual(second["state"], "cancelled")

        # Setelah disarm, page sudah ditutup dan listener ikut mati —
        # simulasikan event terlambat lewat callback yang masih dipegang.
        late_callback = next(iter(enrollment_page.exposed.values()))
        await late_callback("late-secret")
        after = await self._status(host)
        self.assertEqual(after["state"], "cancelled")
        self.assertFalse(after["has_password"])
        self.assertNotIn("late-secret", json.dumps(after))

    async def test_session_close_disarms_enrollment(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = host.enrollments["probe"]
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")

        closed = await PYHOST_MODULE._handle(host, {
            "id": 5, "cmd": "session.close", "session": "s1"})
        self.assertTrue(closed["ok"])

        after = await self._status(host)
        self.assertEqual(after["state"], "browser_gone")
        self.assertFalse(after["has_password"])
        self.assertNotIn("secret-not-echoed", json.dumps(after))
        self.assertIsNone(enr.password)
        self.assertIsNone(enr.page)

    async def test_drop_session_disarms_enrollment(self):
        # Jalur kematian browser manual / launch failure / shutdown:
        # semuanya lewat _drop_session.
        host, _ctx = self._make_host()
        await self._start(host)
        enr = host.enrollments["probe"]
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")

        await host._drop_session("s1")
        self.assertEqual(enr.state, "browser_gone")
        self.assertIsNone(enr.password)

    async def test_expired_deadline_drops_secret(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = host.enrollments["probe"]
        enrollment_page = enr.page
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")
        enr.deadline = asyncio.get_running_loop().time() - 1

        response = await self._status(host)
        self.assertEqual(response["state"], "expired")
        self.assertTrue(enrollment_page.closed)
        self.assertIsNone(enr.page)
        self.assertIsNone(enr.password)
        self.assertNotIn("secret-not-echoed", json.dumps(response))

    async def test_expire_task_expires_without_polling(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = host.enrollments["probe"]
        enr.deadline = asyncio.get_running_loop().time() - 1
        await PYHOST_MODULE.enrollment._expire_later(enr)
        self.assertEqual(enr.state, "expired")
        self.assertIsNone(enr.password)

    async def test_status_missing_enrollment(self):
        host, _ctx = self._make_host()
        response = await self._status(host)
        self.assertEqual(response["error"]["code"], "ENROLLMENT_NOT_FOUND")

    async def test_finish_refused_before_complete(self):
        host, _ctx = self._make_host()
        await self._start(host)
        response = await self._finish(host)
        self.assertEqual(response["error"]["code"],
                         "ENROLLMENT_NOT_COMPLETE")
        self._cancel_pending_tasks(host)


if __name__ == "__main__":
    unittest.main(verbosity=2)
