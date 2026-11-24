"""Focused regression tests for the Downloader-owned Camoufox adapter."""

import importlib.util
import os
import sys
import tempfile
import types
import unittest
from unittest import mock


REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PYHOST = os.path.join(REPO, "module", "sharedLogic", "pyhost")
BROWSER = os.path.join(
    REPO,
    "module",
    "mangareader",
    "Features",
    "Downloader",
    "mangareader_downloader",
    "browser.py",
)
CATALOG_BROWSER = os.path.join(
    REPO,
    "module",
    "mangareader",
    "Features",
    "Catalog",
    "mangareader_catalog",
    "browser.py",
)
sys.path.insert(0, PYHOST)


def _load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


BROWSER_MODULE = _load("mangareader_downloader_browser_tests", BROWSER)
CATALOG_BROWSER_MODULE = _load("mangareader_catalog_browser_tests", CATALOG_BROWSER)


class BrowserProfileIsolationTests(unittest.TestCase):
    def test_application_locations_get_different_stable_profile_namespaces(self):
        portable = BROWSER_MODULE._instance_key(r"C:\VSCODE\citadel\portable")
        installed = BROWSER_MODULE._instance_key(
            r"C:\Users\YUZHA\AppData\Local\Yuzhayo.Citadel\current")

        self.assertEqual(portable, BROWSER_MODULE._instance_key(r"C:\VSCODE\citadel\portable"))
        self.assertNotEqual(portable, installed)


class ComixPageReadinessTests(unittest.IsolatedAsyncioTestCase):
    def test_waf_challenge_is_identified_by_path_not_page_title(self):
        self.assertTrue(BROWSER_MODULE._is_waf_challenge(
            "https://comix.ws/@waf/challenge?return=%2Fbrowse"))
        self.assertFalse(BROWSER_MODULE._is_waf_challenge("https://comix.ws/browse"))

    def test_api_module_accepts_current_build_and_rejects_other_origins(self):
        url = "https://comix.ws/assets/build/current/dist/apicore-a1b2.js?v=1"
        self.assertEqual(url, BROWSER_MODULE._valid_api_module_url(
            "https://comix.ws/browse", url))
        for invalid in (
                "https://evil.example/apicore-a1b2.js",
                "http://comix.ws/apicore-a1b2.js",
                "https://comix.ws:8443/apicore-a1b2.js",
                "https://comix.ws/env-a1b2.js",
                "https://comix.ws/apicore-a1b2.js/other",
                None):
            with self.subTest(url=invalid):
                self.assertIsNone(BROWSER_MODULE._valid_api_module_url(
                    "https://comix.ws/browse", invalid))

    async def test_bridge_uses_same_origin_api_module_from_current_document(self):
        url = "https://comix.ws/assets/build/current/dist/apicore-a1b2.js"
        page = _BridgePage(url)
        session = {}

        await BROWSER_MODULE._ensure_bridge(page, session)

        self.assertIn('import("' + url + '")', page.injected)
        self.assertEqual(url, session["api_module_url"])

    async def test_bridge_reuses_module_after_document_replacement(self):
        url = "https://comix.ws/apicore-a1b2.js"
        page = _BridgePage(None)
        session = {"api_module_url": url}

        await BROWSER_MODULE._ensure_bridge(page, session)

        self.assertIn('import("' + url + '")', page.injected)

    async def test_bridge_rejects_cross_origin_module_url(self):
        page = _BridgePage("https://evil.example/apicore-a1b2.js")
        clock = types.SimpleNamespace(time=mock.Mock(side_effect=[0, 11]))

        with mock.patch.object(BROWSER_MODULE.asyncio, "get_running_loop", return_value=clock):
            with self.assertRaises(BROWSER_MODULE.PyhostError) as caught:
                await BROWSER_MODULE._ensure_bridge(page, {"api_module_url": page.module_url})

        self.assertEqual("API_CLIENT_UNAVAILABLE", caught.exception.code)
        self.assertEqual("", page.injected)

    async def test_headless_challenge_waits_in_same_page(self):
        page = _ChallengePage()

        async def finish_challenge(_delay):
            page.url = "https://comix.ws/browse"

        with mock.patch.object(BROWSER_MODULE.asyncio, "sleep", side_effect=finish_challenge):
            await BROWSER_MODULE._wait_for_application_page(page, 1234)

        self.assertEqual(1234, page.wait_timeout)
        self.assertEqual("domcontentloaded", page.load_state)


class ProxyLaunchTests(unittest.IsolatedAsyncioTestCase):
    async def test_queue_profile_is_isolated_and_invalid_identity_is_rejected(self):
        captured = []
        api = types.ModuleType("camoufox.async_api")

        class Context:
            pages = [types.SimpleNamespace(url="https://comix.ws/browse")]

        class Manager:
            async def __aenter__(self):
                return Context()

        def factory(**kwargs):
            captured.append(kwargs["user_data_dir"])
            return Manager()

        api.AsyncCamoufox = factory
        package = types.ModuleType("camoufox")
        package.async_api = api
        base = {"provider": "comix", "url": "https://comix.ws/browse", "headless": True}
        with tempfile.TemporaryDirectory() as root:
            with mock.patch.dict(os.environ, {"LOCALAPPDATA": root}), mock.patch.dict(
                    sys.modules, {"camoufox": package, "camoufox.async_api": api}), mock.patch.object(
                    BROWSER_MODULE, "_navigate_application_page", new=mock.AsyncMock()):
                await BROWSER_MODULE.cmd_open(_Host(), base)
                await BROWSER_MODULE.cmd_open(_Host(), dict(base, profile_identity="queue-" + "a" * 32))
                await BROWSER_MODULE.cmd_open(_Host(), dict(base, profile_identity="queue-" + "b" * 32))
                with self.assertRaises(BROWSER_MODULE.PyhostError):
                    await BROWSER_MODULE.cmd_open(_Host(), dict(base, profile_identity="../escape"))
        self.assertEqual(3, len(set(captured)))
        self.assertEqual(os.path.join(captured[0], "queue", "queue-" + "a" * 32), captured[1])

    async def test_downloader_and_catalog_forward_proxy_without_echoing_credentials(self):
        for browser in (BROWSER_MODULE, CATALOG_BROWSER_MODULE):
            with self.subTest(browser=browser.__name__):
                captured = {}

                class Page:
                    url = "https://comix.ws/browse"

                class Context:
                    pages = [Page()]

                class CamoufoxContext:
                    async def __aenter__(self):
                        return Context()

                    async def __aexit__(self, *_args):
                        return None

                def factory(**kwargs):
                    captured.update(kwargs)
                    return CamoufoxContext()

                api = types.ModuleType("camoufox.async_api")
                api.AsyncCamoufox = factory
                package = types.ModuleType("camoufox")
                package.async_api = api
                host = _Host()
                payload = {
                    "provider": "comix",
                    "url": "https://comix.ws/browse",
                    "headless": True,
                    "proxy": {
                        "server": "http://proxy.test:8080",
                        "username": "user",
                        "password": "secret",
                    },
                }

                with tempfile.TemporaryDirectory(prefix="CitadelMangaProxy-") as local_app_data:
                    with mock.patch.dict(os.environ, {"LOCALAPPDATA": local_app_data}):
                        with mock.patch.dict(sys.modules, {
                            "camoufox": package,
                            "camoufox.async_api": api,
                        }):
                            with mock.patch.object(
                                    browser,
                                    "_navigate_application_page",
                                    new=mock.AsyncMock()):
                                response = await browser.cmd_open(host, payload)

                self.assertEqual(payload["proxy"], captured["proxy"])
                self.assertNotIn("secret", repr(response))
                host.sessions.clear()


class _BridgePage:
    url = "https://comix.ws/browse"

    def __init__(self, module_url):
        self.module_url = module_url
        self.injected = ""
        self.present = False

    async def evaluate(self, script, *args):
        if "getElementById" in script:
            return self.present
        if "getEntriesByType" in script:
            return self.module_url
        return False

    async def add_script_tag(self, *, content):
        self.injected = content
        self.present = True


class _ChallengePage:
    url = "https://comix.ws/@waf/challenge"

    def __init__(self):
        self.wait_timeout = None
        self.load_state = None

    async def evaluate(self, script):
        return self.url.startswith("https://comix.ws/@waf/")

    async def wait_for_load_state(self, state, *, timeout):
        self.load_state = state
        self.wait_timeout = timeout


class _Host:
    def __init__(self):
        self.sessions = {}
        self.next_sid = 0

    def _profile_busy(self, profile):
        return any(item.get("profile") == profile for item in self.sessions.values())

    async def _drop_session(self, sid, forget_on_failure=False):
        self.sessions.pop(sid, None)


if __name__ == "__main__":
    unittest.main(verbosity=2)
