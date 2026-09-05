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

from providers.google import detect_google_email

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

    async def test_google_email_detection_does_not_wait_for_a_locator(self):
        class Page:
            async def evaluate(self, _script):
                return []

            def locator(self, _selector):
                raise AssertionError("email detection must not use waiting locators")

        self.assertIsNone(await detect_google_email(Page()))

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
        # Perilaku Playwright NYATA yang ditemukan live smoke: menutup
        # page TERAKHIR mematikan context — new_page setelah itu melempar.
        if not self._ctx.pages:
            self._ctx.dead = True

    async def evaluate(self, _script):
        return self.evaluate_result


class _FakeEnrollmentContext:
    def __init__(self, goto_gate=None, goto_error=None):
        self.events = []
        self.pages = []
        self.replacement_pages = 0
        self.dead = False
        self._goto_gate = goto_gate
        self._goto_error = goto_error

    async def new_page(self):
        if self.dead:
            raise RuntimeError("context closed")
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


if __name__ == "__main__":
    unittest.main(verbosity=2)
