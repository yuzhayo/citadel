r"""Architecture guards for the Yuzvid ownership refactor (docs/work/yuzvid-phase23/REFACTOR_PLAN.md).

The parent shell (YuzvidView) is a dumb router: it may only speak the public
Browser contract. Feature internals stay inside Features/ and are assembled
by the composition root (YuzvidModule).

  YUZ-1  parent code-behind references no `internal` type declared under
         Features/ (LocalProxyServer, YuzvidProxyPoolAdapter, BrowserDnsMode…).
         Public contracts (IYuzvidBrowserController, BrowserState,
         YuzvidDnsMode, BrowserRuntimeSnapshot) and public views are allowed
         by name — instantiation is covered by YUZ-2.
  YUZ-2  parent code-behind instantiates no feature-declared type
         (`new <FeatureType>(`) — construction belongs to YuzvidModule.
  YUZ-3  the composition root still performs the R2 cutover exactly once:
         CreateView builds YuzvidBrowserController, calls Activate(), and
         registers disposal on the retained lifetime.

Local check only (not a CI gate — see REFACTOR_PLAN/PLAN §0):

  python -m unittest module.yuzvid.tests.test_yuzvid_architecture -v
  (run from C:\VSCODE\citadel)
"""

import os
import re
import unittest

MODULE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FEATURES = os.path.join(MODULE, "Features")
PARENT_CS = os.path.join(MODULE, "YuzvidView.xaml.cs")
MODULE_CS = os.path.join(MODULE, "YuzvidModule.cs")

_DECL = re.compile(
    r"^\s*(public|internal)\s+(?:(?:abstract|sealed|static|partial)\s+)*"
    r"(?:class|enum|interface|record)\s+(\w+)",
    re.MULTILINE)


def _read(path):
    with open(path, encoding="utf-8-sig") as handle:
        return handle.read()


def _strip_line_comments(text):
    return "\n".join(
        line.split("//", 1)[0] for line in text.split("\n"))


def _feature_types():
    """(name -> 'public'|'internal') for every type declared under Features/."""
    found = {}
    for root, _, files in os.walk(FEATURES):
        for name in sorted(files):
            if not name.endswith(".cs"):
                continue
            code = _read(os.path.join(root, name))
            for visibility, type_name in _DECL.findall(code):
                found.setdefault(type_name, visibility)
    return found


class ParentBoundaryTest(unittest.TestCase):
    def test_parent_references_no_internal_feature_type(self):
        """YUZ-1: YuzvidView.xaml.cs never names an internal Features/ type."""
        feature_types = _feature_types()
        internals = sorted(
            name for name, vis in feature_types.items() if vis == "internal")
        self.assertTrue(
            internals,
            "no internal feature types found — guard is vacuous, check _DECL")
        parent = _strip_line_comments(_read(PARENT_CS))
        violations = [
            name for name in internals
            if re.search(r"\b%s\b" % re.escape(name), parent)]
        self.assertEqual(
            [], violations,
            "parent references internal feature type(s): %s" % violations)

    def test_parent_instantiates_no_feature_type(self):
        """YUZ-2: construction lives in YuzvidModule, not the shell."""
        feature_types = _feature_types()
        parent = _strip_line_comments(_read(PARENT_CS))
        violations = [
            name for name in sorted(feature_types)
            if re.search(r"\bnew\s+%s\s*[\(\{]" % re.escape(name), parent)]
        self.assertEqual(
            [], violations,
            "parent instantiates feature type(s): %s" % violations)


class CompositionRootTest(unittest.TestCase):
    def test_module_performs_single_cutover(self):
        """YUZ-3: CreateView builds, activates once, and owns disposal."""
        code = _strip_line_comments(_read(MODULE_CS))
        for snippet in (
            "new YuzvidBrowserController()",
            ".Activate()",
            "lifetime.Add(",
            "new YuzvidView(",
        ):
            self.assertIn(
                snippet, code,
                "YuzvidModule.CreateView lost cutover step: %s" % snippet)
        self.assertEqual(
            code.count(".Activate()"), 1,
            "Activate must run exactly once per CreateView")


if __name__ == "__main__":
    unittest.main()
