"""Focused regression tests for the Downloader-owned Camoufox adapter."""

import importlib.util
import os
import sys
import unittest


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
sys.path.insert(0, PYHOST)

SPEC = importlib.util.spec_from_file_location("mangareader_downloader_browser_tests", BROWSER)
BROWSER_MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BROWSER_MODULE)


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

    async def test_bridge_uses_same_origin_env_module_from_current_document(self):
        page = _BridgePage("https://comix.ws/env-a1b2.js")

        await BROWSER_MODULE._ensure_bridge(page)

        self.assertIn('import("https://comix.ws/env-a1b2.js")', page.injected)

    async def test_bridge_rejects_cross_origin_module_url(self):
        page = _BridgePage("https://evil.example/env-a1b2.js")

        with self.assertRaises(BROWSER_MODULE.PyhostError) as caught:
            await BROWSER_MODULE._ensure_bridge(page)

        self.assertEqual("API_CLIENT_UNAVAILABLE", caught.exception.code)


class _BridgePage:
    url = "https://comix.ws/browse"

    def __init__(self, env_url):
        self.env_url = env_url
        self.injected = ""
        self.present = False

    async def evaluate(self, script):
        if "getElementById" in script:
            return self.present
        return self.env_url

    async def add_script_tag(self, *, content):
        self.injected = content
        self.present = True


if __name__ == "__main__":
    unittest.main(verbosity=2)
