r"""Behavior tests for the camoprof_add_profile pyhost plugin.

Behavior suite for the thin puzzle-block contract (tasks/plan.md):
- start creates a fresh enrollment page (proven pattern), arms the
  listener on it BEFORE navigation, swaps sess["page"] via the generic
  host helper, then closes the resident page — one visible window;
- teardown creates the clean replacement page FIRST, swaps the
  reference, then closes the enrollment page (listener dies with it);
- no lease/claim machinery anywhere; host/sess are function params.

Run:
  <venv python> -m unittest module.sharedLogic.tests.test_add_profile_plugin -v
"""

import asyncio
import json
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

SPEC = importlib.util.spec_from_file_location("citadel_pyhost_plugin", PYHOST)
PYHOST_MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PYHOST_MODULE)


class _Page:
    """Platform-true: operasi page pada context mati melempar; menutup
    page TERAKHIR mematikan context."""

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
        if self._ctx.dead:
            raise RuntimeError(
                "Target page, context or browser has been closed")
        if self._goto_gate is not None:
            await self._goto_gate.wait()
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
        return self.evaluate_result


class _Context:
    def __init__(self, goto_gate=None, goto_error=None):
        self.events = []
        self.pages = []
        self.dead = False
        self._goto_gate = goto_gate
        self._goto_error = goto_error

    async def new_page(self):
        if self.dead:
            raise RuntimeError("context closed")
        page = _Page(self, self.events, self._goto_gate, self._goto_error)
        self.pages.append(page)
        return page


class _NullCm:
    async def __aexit__(self, *_args):
        return None


class _YieldingCm:
    async def __aexit__(self, *_args):
        await asyncio.sleep(0)
        return None


class AddProfilePluginTest(unittest.IsolatedAsyncioTestCase):
    """camoprof.add_profile.* — urutan arm, capture tervalidasi, state
    machine, finish satu kali, cleanup per jalur kematian session."""

    SIGNIN = "https://accounts.google.com/signin/v2/identifier"
    MYACCOUNT = "https://myaccount.google.com/"

    def _make_host(self, headless=False, goto_gate=None, goto_error=None):
        host = PYHOST_MODULE._Host()
        ctx = _Context(goto_gate=goto_gate, goto_error=goto_error)
        # Resident page mewarisi gate/error goto yang sama dengan page
        # buatan context — start meng-CLAIM resident, jadi goto-nya yang
        # jalan dan harus bisa gagal/menggantung seperti page mana pun.
        resident = _Page(ctx, ctx.events, goto_gate, goto_error)
        ctx.pages.append(resident)
        host.sessions["s1"] = {
            "profile": "probe",
            "cm": _NullCm(),
            "ctx": ctx,
            "page": resident,
            "dir": "unused",
            "headless": headless,
        }
        return host, ctx

    async def _handle(self, host, cmd, **params):
        return await PYHOST_MODULE._handle(
            host, {"id": 1, "cmd": cmd, **params})

    async def _start(self, host, **params):
        return await self._handle(
            host, "camoprof.add_profile.start", session="s1", **params)

    async def _status(self, host):
        return await self._handle(
            host, "camoprof.add_profile.status", session="s1")

    async def _finish(self, host):
        return await self._handle(
            host, "camoprof.add_profile.finish", session="s1")

    async def _cancel(self, host):
        return await self._handle(
            host, "camoprof.add_profile.cancel", session="s1")

    def _enrollments(self, host):
        return host.add_profile_enrollments

    async def _type_password(self, host, value):
        enr = self._enrollments(host)["probe"]
        callback = next(iter(enr.page.exposed.values()))
        await callback(value)

    def _cancel_pending_tasks(self, host):
        for enr in self._enrollments(host).values():
            if getattr(enr, "expire_task", None) is not None:
                enr.expire_task.cancel()
            if getattr(enr, "navigate_task", None) is not None:
                enr.navigate_task.cancel()
            if getattr(enr, "close_task", None) is not None:
                enr.close_task.cancel()

    async def _await_close(self, host, profile="probe"):
        """Terminal kini cepat: kematian browser jalan di latar —
        tunggu task latar itu sebelum memeriksa context mati."""
        enr = self._enrollments(host).get(profile)
        task = getattr(enr, "close_task", None) if enr else None
        if task is not None:
            await task

    # ---- satu jendela + arm order ------------------------------------

    async def test_start_swaps_to_fresh_enrollment_page(self):
        host, ctx = self._make_host()
        resident = host.sessions["s1"]["page"]

        response = await self._start(host)
        await asyncio.sleep(0)

        self.assertTrue(response["ok"], str(response))
        self.assertEqual(response["state"], "armed")
        # Satu jendela: resident ditutup SETELAH page enrollment hidup,
        # referensi session langsung menunjuk page hidup baru.
        self.assertTrue(resident.closed)
        enr = self._enrollments(host)["probe"]
        self.assertIsNot(enr.page, resident)
        self.assertFalse(enr.page.closed)
        self.assertIs(host.sessions["s1"]["page"], enr.page)
        self._cancel_pending_tasks(host)

    async def test_start_arms_listener_before_navigation(self):
        host, _ctx = self._make_host()
        response = await self._start(host)
        self.assertTrue(response["ok"])
        self.assertEqual(response["state"], "armed")
        page = self._enrollments(host)["probe"].page
        await asyncio.sleep(0)
        # Kontrak urutan: listener hidup SEBELUM navigasi jalan.
        self.assertEqual(page.events, ["expose", "init", "goto"])
        self.assertEqual(len(page.exposed), 1)
        self.assertIn("accounts.google.com", "".join(page.init_scripts))
        self.assertIn('input[type=\\"password\\"]',
                      "".join(page.init_scripts))
        self._cancel_pending_tasks(host)

    async def test_start_returns_while_navigation_pending(self):
        gate = asyncio.Event()
        host, _ctx = self._make_host(goto_gate=gate)
        response = await self._start(host)
        self.assertTrue(response["ok"])
        self.assertEqual(response["state"], "armed")
        await asyncio.sleep(0)
        enr = self._enrollments(host)["probe"]
        self.assertIn("goto", enr.page.events)
        gate.set()
        self._cancel_pending_tasks(host)

    async def test_teardown_ends_the_browser(self):
        host, ctx = self._make_host()

        await self._start(host)
        enr = self._enrollments(host)["probe"]
        enrollment_page = enr.page
        await self._cancel(host)

        # Registry dilepas SINKRON (respons cancel tidak menunggu
        # browser) — kematian browser selesai di task latar.
        self.assertNotIn(
            "s1", host.sessions,
            "session milik flow harus dilepas saat terminal")
        await self._await_close(host)
        self.assertTrue(enrollment_page.closed)
        self.assertTrue(ctx.dead, "context harus mati bersama page-nya")

    async def test_navigation_failure_ends_the_browser(self):
        host, ctx = self._make_host(
            goto_error=RuntimeError("net::ERR_NAME_NOT_RESOLVED"))

        response = await self._start(host)
        self.assertTrue(response["ok"])
        enr = self._enrollments(host)["probe"]
        await enr.navigate_task  # deterministik: tunggu kegagalan

        after = await self._status(host)
        self.assertEqual(after["state"], "failed")
        self.assertFalse(after["has_password"])
        self.assertNotIn("s1", host.sessions)
        await self._await_close(host)
        self.assertTrue(ctx.dead)

    # ---- cancel / navigation races ------------------------------------

    async def test_cancel_during_pending_navigation_is_immediate(self):
        gate = asyncio.Event()
        host, _ctx = self._make_host(goto_gate=gate)
        await self._start(host)
        enr = self._enrollments(host)["probe"]
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")

        cancelled = await self._cancel(host)
        self.assertTrue(cancelled["ok"])
        self.assertEqual(cancelled["state"], "cancelled")
        self.assertIsNone(enr.password)

        gate.set()
        after = await self._status(host)
        self.assertEqual(after["state"], "cancelled")
        self.assertNotIn("secret-not-echoed", json.dumps(after))

    async def test_browser_gone_during_navigation_drops_session(self):
        host, _ctx = self._make_host(
            goto_error=RuntimeError(
                "Target page, context or browser has been closed"))
        host.sessions["s1"]["cm"] = _YieldingCm()

        response = await self._start(host)
        self.assertTrue(response["ok"])
        enr = self._enrollments(host)["probe"]
        task = enr.navigate_task
        self.assertIsNotNone(task)
        await task  # deterministik: tunggu jalur mati selesai
        self.assertFalse(task.cancelled())

        self.assertEqual(enr.state, "browser_gone")
        self.assertIsNone(enr.password)
        self.assertNotIn("s1", host.sessions)

    # ---- capture validation -------------------------------------------

    async def test_capture_ignores_lookalike_origin(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = self._enrollments(host)["probe"]
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
        enr = self._enrollments(host)["probe"]
        enr.page.url = self.SIGNIN
        await self._type_password(host, "first-typed")
        await self._type_password(host, "second-typed")
        enr.page.url = self.MYACCOUNT
        enr.page.evaluate_result = \
            ["Account: User (user@gmail.com)"]
        response = await self._status(host)
        self.assertEqual(response["state"], "complete")
        finished = await self._finish(host)
        self.assertTrue(finished["ok"])
        self.assertEqual(finished["email"], "user@gmail.com")
        self.assertEqual(finished["password"], "second-typed")

    async def test_clearing_field_clears_captured_candidate(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = self._enrollments(host)["probe"]
        await asyncio.sleep(0)
        enr.page.url = self.SIGNIN
        await self._type_password(host, "typed-then-cleared")
        self.assertTrue((await self._status(host))["has_password"])
        await self._type_password(host, "")
        self.assertFalse((await self._status(host))["has_password"])

        enr.page.url = self.MYACCOUNT
        enr.page.evaluate_result = \
            ["Account: User (user@gmail.com)"]
        response = await self._status(host)
        self.assertEqual(response["state"], "complete")
        self.assertFalse(response["has_password"])
        finished = await self._finish(host)
        self.assertTrue(finished["ok"])
        self.assertIsNone(finished["password"])
        self.assertNotIn("typed-then-cleared", json.dumps(finished))

    async def test_challenge_waits_for_user(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = self._enrollments(host)["probe"]
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")
        enr.page.url = \
            "https://accounts.google.com/signin/challenge/totp"
        response = await self._status(host)
        self.assertEqual(response["state"], "challenge")
        self.assertTrue(response["challenge"])
        self.assertTrue(response["has_password"])
        self.assertNotIn("secret-not-echoed", json.dumps(response))

    # ---- guards --------------------------------------------------------

    async def test_start_refuses_headless_session(self):
        host, _ctx = self._make_host(headless=True)
        response = await self._start(host)
        self.assertEqual(response["error"]["code"], "HEADLESS_ENROLLMENT")
        self.assertEqual(self._enrollments(host), {})

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
        self.assertEqual(self._enrollments(host), {})

    async def test_navigate_during_enrollment_hits_live_page(self):
        # Kontrak tipis (bukan menara): navigate saat enrollment aktif
        # beroperasi pada page enrollment yang HIDUP — tidak crash, tidak
        # page mati. Dialog modal memblokir UI lain, jadi tidak ada guard
        # owner; perilaku jujur.
        host, _ctx = self._make_host()
        await self._start(host)
        response = await self._handle(
            host, "session.navigate", session="s1",
            url="https://example.com/")
        self.assertTrue(response["ok"], str(response))
        self.assertEqual(host.sessions["s1"]["page"].url,
                         "https://example.com/")
        self._cancel_pending_tasks(host)

    # ---- finish / outcomes ---------------------------------------------

    async def test_finish_is_one_shot_and_cleans_up(self):
        host, ctx = self._make_host()
        await self._start(host)
        enr = self._enrollments(host)["probe"]
        enrollment_page = enr.page
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")
        enr.page.url = self.MYACCOUNT
        enr.page.evaluate_result = \
            ["Account: User (user@gmail.com)"]
        await self._status(host)

        finished = await self._finish(host)
        self.assertTrue(finished["ok"])
        self.assertEqual(finished["password"], "secret-not-echoed")
        # Respons finish tidak menunggu browser — page ditutup di latar.
        self.assertIsNone(enr.page)
        self.assertIsNone(enr.password)
        await self._await_close(host)
        self.assertTrue(enrollment_page.closed)

        again = await self._finish(host)
        self.assertEqual(again["error"]["code"], "ENROLLMENT_CONSUMED")
        self.assertNotIn("secret-not-echoed", json.dumps(again))

        after = await self._status(host)
        self.assertEqual(after["state"], "consumed")
        self.assertFalse(after["has_password"])
        self.assertNotIn("secret-not-echoed", json.dumps(after))
        # Flow END: registry sudah lepas; browser mati di latar.
        self.assertNotIn("s1", host.sessions)
        await self._await_close(host)
        self.assertTrue(ctx.dead)

    async def test_wrong_account_is_terminal_refusal(self):
        host, _ctx = self._make_host()
        await self._start(host, expected_email="expected@gmail.com")
        enr = self._enrollments(host)["probe"]
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")
        enr.page.url = self.MYACCOUNT
        enr.page.evaluate_result = \
            ["Account: User (actual@gmail.com)"]
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
        enr = self._enrollments(host)["probe"]
        enr.page.url = self.MYACCOUNT
        enr.page.evaluate_result = \
            ["Account: User (user@gmail.com)"]
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
        enr = self._enrollments(host)["probe"]
        enrollment_page = enr.page
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")

        first = await self._cancel(host)
        self.assertTrue(first["ok"])
        self.assertEqual(first["state"], "cancelled")
        self.assertIsNone(enr.page)
        self.assertIsNone(enr.password)
        await self._await_close(host)
        self.assertTrue(enrollment_page.closed,
                        "page enrollment ditutup — listener mati")

        second = await self._cancel(host)
        self.assertTrue(second["ok"])
        self.assertEqual(second["state"], "cancelled")

        # Setelah disarm, listener ikut mati bersama page-nya — event
        # terlambat lewat callback yang masih dipegang tetap diabaikan.
        late_callback = next(iter(enrollment_page.exposed.values()))
        await late_callback("late-secret")
        after = await self._status(host)
        self.assertEqual(after["state"], "cancelled")
        self.assertFalse(after["has_password"])
        self.assertNotIn("late-secret", json.dumps(after))

    # ---- session-death lifecycle ----------------------------------------

    async def test_session_close_disarms_enrollment(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = self._enrollments(host)["probe"]
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")

        closed = await self._handle(
            host, "session.close", session="s1")
        self.assertTrue(closed["ok"])

        after = await self._status(host)
        self.assertEqual(after["state"], "browser_gone")
        self.assertFalse(after["has_password"])
        self.assertNotIn("secret-not-echoed", json.dumps(after))
        self.assertIsNone(enr.password)

    async def test_drop_session_disarms_enrollment(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = self._enrollments(host)["probe"]
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")

        await host._drop_session("s1")
        self.assertEqual(enr.state, "browser_gone")
        self.assertIsNone(enr.password)

    async def test_manual_window_close_drops_session(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = self._enrollments(host)["probe"]
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")

        await enr.page.close()  # user menutup jendela

        after = await self._status(host)
        self.assertEqual(after["state"], "browser_gone")
        self.assertNotIn("s1", host.sessions)
        self.assertIsNone(enr.password)

    async def test_expired_deadline_drops_secret(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = self._enrollments(host)["probe"]
        enr.page.url = self.SIGNIN
        await self._type_password(host, "secret-not-echoed")
        enr.deadline = asyncio.get_running_loop().time() - 1

        response = await self._status(host)
        self.assertEqual(response["state"], "expired")
        self.assertIsNone(enr.page)
        self.assertIsNone(enr.password)
        self.assertNotIn("secret-not-echoed", json.dumps(response))

    async def test_expire_task_expires_without_polling(self):
        host, _ctx = self._make_host()
        await self._start(host)
        enr = self._enrollments(host)["probe"]
        enr.deadline = asyncio.get_running_loop().time() - 1
        from camoprof_add_profile import enrollment as plugin_enrollment
        await plugin_enrollment._expire_later(host, enr)
        self.assertEqual(enr.state, "expired")
        self.assertIsNone(enr.password)
        await self._await_close(host)

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
