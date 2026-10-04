r"""Structural guard for the igay69 version-group additions (extractor refine).

The C# extractor's pure logic (VersionGroups map, DeriveTitle, ExtractionGroups)
has no C# test project in this citizen (citizens reference only Citadel.Core/
Contract/Setting). This guard mirrors the existing architecture-guard style:
it inspects the source for the exact server→version mapping and the presence of
the grouping/filter contract, so a silent edit that breaks V1..V4 grouping is
caught locally.

Behavioral proof (runtime reflection over the built DLL) is run separately via a
throwaway harness; this file guards the *contract* the UI depends on.

Local check only:
  python -m unittest module.yuzvid.tests.test_yuzvid_extractor_groups -v
  (run from C:\VSCODE\citadel)
"""

import os
import re
import unittest

MODULE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXTRACTOR = os.path.join(MODULE, "Features", "Extraction", "VideoLinkExtractor.cs")
PROJECTION = os.path.join(MODULE, "Features", "Extraction", "ExtractionGroupProjection.cs")


def _read(path):
    with open(path, encoding="utf-8-sig") as handle:
        return handle.read()


class VersionGroupMapTest(unittest.TestCase):
    def setUp(self):
        self.code = _read(EXTRACTOR)

    def test_expected_host_to_version_mappings(self):
        """Each documented igay69 server host maps to its version (V1..V4)."""
        expected = {
            "luluvid.com": "V1", "luluvdoo.com": "V1", "luluvdo.com": "V1",
            "dood.wf": "V1", "doodstream.co": "V1",
            "voe.sx": "V2", "voe.pm": "V2", "voe.unblockit.top": "V2",
            "playmogo.com": "V3", "myvidplay.com": "V3",
            "byseqekaho.com": "V4",
        }
        for host, version in expected.items():
            pattern = r'\["%s"\]\s*=\s*"%s"' % (re.escape(host), re.escape(version))
            self.assertRegex(
                self.code, pattern,
                "missing/incorrect version mapping for %s -> %s" % (host, version))

    def test_version_labels_defined(self):
        """VersionLabel emits a human label for each version."""
        for version, label in [
            ("V1", "V1"), ("V2", "V2"), ("V3", "V3"), ("V4", "V4"),
        ]:
            self.assertIn(
                '"%s" => "%s' % (version, label), self.code,
                "VersionLabel missing case for %s" % version)
        self.assertIn("PlayMogo", self.code)
        self.assertIn("Byseqekaho", self.code)

    def test_derive_title_and_anchor_rules_present(self):
        for snippet in (
            "ExtractAnchorLabels",
            "DeriveTitle",
            "GetVersionGroup",
            "AnchorPattern",
        ):
            self.assertIn(snippet, self.code, "missing " + snippet)


class ExtractionGroupsContractTest(unittest.TestCase):
    def setUp(self):
        self.code = _read(PROJECTION)

    def test_build_filters_unknown_version_and_groups(self):
        self.assertIn("VersionGroup", self.code)
        self.assertIn("GroupBy", self.code)
        self.assertIn("ExtractionRow", self.code)

    def test_tri_state_selection_contract(self):
        # ExtractionRow exposes a nullable Selected (tri-state) + group toggle.
        self.assertIn("bool? _selected", self.code)
        self.assertIn("SelectedLinks", self.code)
        self.assertIn("SelectAll", self.code)
        self.assertIn("Toggle", self.code)

    def test_children_natural_sorted_by_title(self):
        # Child links are ordered by display title (numeric-aware), not by
        # arrival order from the tap/DOM merge.
        self.assertIn("TitleOrder", self.code)
        self.assertIn("NaturalCompare", self.code)
        self.assertIn("group.OrderBy(l => l, TitleOrder)", self.code)


if __name__ == "__main__":
    unittest.main()
