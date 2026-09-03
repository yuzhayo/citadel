r"""Architecture guards for the Add Profile ownership contract (tasks/plan.md).

Dependency direction and containment, asserted by searching source —
cheap, deterministic, and immune to refactors that rename symbols:

  ARCH-1  shared pyhost core contains no feature semantics (no Google,
          enrollment, password, Add Profile concepts — docstrings that
          EXPLAIN the ban are whitelisted explicitly);
  ARCH-2  the feature plugin never touches the mutable session registry
          (no host.sessions / enr.host / sess[...] access);
  ARCH-3  shared C# transport (PyHost.cs) names only the plugin's
          commands — no feature logic beyond typed wrappers;
  ARCH-4  Launcher opens no browser session for Add Profile: the
          AddProfileButton flow must call the feature only.

Run:
  <venv python> -m unittest module.sharedLogic.tests.test_add_profile_architecture -v
"""

import os
import re
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

PYHOST_CORE = os.path.join(ROOT, "pyhost", "pyhost.py")
PLUGIN_DIR = os.path.join(
    ROOT, "..", "camoprof", "Features", "AddProfile", "camoprof_add_profile")
PYHOST_CS = os.path.join(ROOT, "cs", "PyHost.cs")
LAUNCHER_CS = os.path.join(
    ROOT, "..", "camoprof", "Launcher", "LauncherView.xaml.cs")

# Kata yang menandakan semantik fitur. Docstring core yang MENJELASKAN
# larangan ini (mengutip nama terlarang untuk melarangnya) di-whitelist.
_FEATURE_SEMANTICS = re.compile(
    r"accounts\.google\.com|Passwd|password|enrollment|add_profile|"
    r"Add Profile|expected_email", re.IGNORECASE)
_WHITELIST_PATTERNS = (
    "tidak ada semantik", "tidak tahu nama command", "fitur Add Profile",
    "camoprof.add_profile", "CITADEL_PYHOST_PLUGINS", "feature-free",
    "fitur (plugin)", "plugin fitur", "fitur mendaftarkan namespace",
    "Add Profile fitur", "contoh: navigate saat",
    "contoh: fitur mendeteksi", "Tidak ada satu kata",
    "fitur tidak boleh hidup di sini",
)


def _sources(directory):
    for name in os.listdir(directory):
        if not name.endswith(".py"):
            continue
        path = os.path.join(directory, name)
        with open(path, encoding="utf-8") as handle:
            yield name, handle.read()


def _stripped_of_whitelist(text):
    lines = []
    for line in text.split("\n"):
        if any(pattern in line for pattern in _WHITELIST_PATTERNS):
            continue
        lines.append(line)
    return "\n".join(lines)


class CorePurityTest(unittest.TestCase):
    def test_pyhost_core_has_no_feature_semantics(self):
        """ARCH-1: pyhost.py (protokol + registry + helper generik)
        hanya infrastruktur — tanpa semantik fitur."""
        with open(PYHOST_CORE, encoding="utf-8") as handle:
            code = _stripped_of_whitelist(handle.read())
        match = _FEATURE_SEMANTICS.search(code)
        self.assertIsNone(
            match,
            "pyhost.py mengandung semantik fitur: %r"
            % (match and match.group(0),))


class PluginBoundaryTest(unittest.TestCase):
    def test_plugin_never_touches_session_registry(self):
        """ARCH-2: plugin tidak memegang registry mentah (host.sessions)
        dan tidak menyimpan backlink host. Membaca dict sess PARAMETER
        (idiom google.inspect) tetap boleh."""
        offenders = []
        for name, source in _sources(PLUGIN_DIR):
            for pattern in (r"host\.sessions", r"self\.host",
                            r"enr\.host", r"host\.registry"):
                if re.search(pattern, source):
                    offenders.append("%s: %s" % (name, pattern))
        self.assertEqual(
            offenders, [],
            "plugin menyentuh registry session secara langsung: "
            + "; ".join(offenders))

    def test_plugin_declares_its_command_namespace(self):
        """Namespace command plugin terdaftar sebagai miliknya."""
        path = os.path.join(PLUGIN_DIR, "plugin.py")
        with open(path, encoding="utf-8") as handle:
            source = handle.read()
        for command in ("camoprof.add_profile.start",
                        "camoprof.add_profile.status",
                        "camoprof.add_profile.finish",
                        "camoprof.add_profile.cancel"):
            self.assertIn(command, source)


class SharedTransportTest(unittest.TestCase):
    def test_pyhost_cs_names_only_plugin_commands(self):
        """ARCH-3: shared C# transport hanya tahu nama command plugin —
        tanpa logika fitur di luar wrapper terketik."""
        with open(PYHOST_CS, encoding="utf-8") as handle:
            source = handle.read()
        for command in ("camoprof.add_profile.start",
                        "camoprof.add_profile.status",
                        "camoprof.add_profile.finish",
                        "camoprof.add_profile.cancel"):
            self.assertIn(command, source)
        # Tidak ada logika state fitur di transport bersama.
        self.assertNotIn("password_observed", source)
        self.assertNotIn("has_password", source)


class LauncherOneWayTest(unittest.TestCase):
    def test_launcher_add_profile_calls_feature_only(self):
        """ARCH-4: alur Add Profile di Launcher tidak membuka session,
        tidak memanggil koordinator/pyhost — hanya kontrak feature."""
        with open(LAUNCHER_CS, encoding="utf-8") as handle:
            source = handle.read()
        start = source.index("AddProfileButton_Click")
        end = source.index("private async void RefreshButton_Click")
        body = source[start:end]
        self.assertIn("RunAddProfileAsync", body)
        self.assertNotIn(
            "_sessions.OpenAsync", body,
            "Launcher membuka session di alur Add Profile — harus lewat "
            "feature")


if __name__ == "__main__":
    unittest.main(verbosity=2)
