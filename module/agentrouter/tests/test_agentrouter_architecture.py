r"""Level B architecture guards for the Agentrouter citizen.

Citadel's contract has two levels. Level A (core vs module) is machine
enforced by .agents/hooks/check-project-refs.mjs and Citizen.targets'
VerifyCitizenIsolation. Level B lives inside a single assembly, so no project
hook can see it — every citizen therefore carries its own guard.

The five assertions come from .agents/skills/citadel-feature-modularity,
"Two Levels, One Contract":

  AGR-1  no feature-internal type is referenced outside its own folder;
  AGR-2  no feature -> feature import (contracts/hubs only);
  AGR-3  the parent references feature contracts only, never internals;
  AGR-4  the module shared folder imports no feature;
  AGR-5  the parent speaks to features through public types only, and each
         feature folder stays self-contained.

Reading rules mirror the parent, not the compiler: comments are stripped so a
mention inside prose is not a reference, and names are matched on word
boundaries so ShortcutRow cannot be confused with something longer.

Local check only (not a CI gate):

  python -m unittest module.agentrouter.tests.test_agentrouter_architecture -v
  (run from C:\VSCODE\citadel)
"""

import os
import re
import unittest

MODULE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FEATURES = os.path.join(MODULE, "Features")
SHARED = os.path.join(MODULE, "sharedLogic")
PARENT_FILES = [
    os.path.join(MODULE, "AgentrouterView.xaml.cs"),
    os.path.join(MODULE, "AgentrouterModule.cs"),
]

_DECL = re.compile(
    r"^\s*(public|internal)\s+(?:(?:abstract|sealed|static|partial)\s+)*"
    r"(?:class|enum|interface|record)\s+(\w+)",
    re.MULTILINE)

_FEATURE_NS = re.compile(r"Module\.Agentrouter\.Features\.(\w+)")

_SKIP_DIRS = {"obj", "bin", "__pycache__"}


def _read(path):
    with open(path, encoding="utf-8-sig") as handle:
        return handle.read()


def _strip_line_comments(text):
    return "\n".join(line.split("//", 1)[0] for line in text.split("\n"))


def _cs_files(root):
    for current, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d not in _SKIP_DIRS]
        for name in sorted(files):
            if name.endswith(".cs"):
                yield os.path.join(current, name)


def _feature_folders():
    if not os.path.isdir(FEATURES):
        return []
    return sorted(
        name for name in os.listdir(FEATURES)
        if os.path.isdir(os.path.join(FEATURES, name)))


def _feature_types():
    """{type_name: (visibility, owning_feature)} declared under Features/."""
    found = {}
    for feature in _feature_folders():
        for path in _cs_files(os.path.join(FEATURES, feature)):
            code = _read(path)
            for visibility, type_name in _DECL.findall(code):
                found.setdefault(type_name, (visibility, feature))
    return found


def _public_feature_types(feature):
    return {
        name for name, (visibility, owner) in _feature_types().items()
        if owner == feature and visibility == "public"
    }


def _files_outside(feature):
    """Every source file in the citizen that is not part of `feature`."""
    own_prefix = "Features/" + feature + "/"
    for path in _cs_files(MODULE):
        relative = os.path.relpath(path, MODULE).replace(os.sep, "/")
        if relative.startswith(own_prefix):
            continue
        yield path, relative


def _violations_for(names, paths):
    hits = []
    for name in names:
        pattern = re.compile(r"\b%s\b" % re.escape(name))
        for path, relative in paths:
            if pattern.search(_strip_line_comments(_read(path))):
                hits.append("%s referenced in %s" % (name, relative))
    return hits


class FeatureInternalsTest(unittest.TestCase):
    """AGR-1: a feature's internal types stay inside its own folder."""

    def test_internal_feature_types_are_folder_local(self):
        feature_types = _feature_types()
        internals = sorted(
            name for name, (visibility, _) in feature_types.items()
            if visibility == "internal")
        self.assertTrue(
            internals,
            "no internal feature types found — guard is vacuous, check _DECL")

        hits = []
        for name in internals:
            feature = feature_types[name][1]
            pattern = re.compile(r"\b%s\b" % re.escape(name))
            for path, relative in _files_outside(feature):
                if pattern.search(_strip_line_comments(_read(path))):
                    hits.append("%s referenced in %s" % (name, relative))
        self.assertEqual([], hits, "feature internals leaked: %s" % hits)


class FeatureIsolationTest(unittest.TestCase):
    """AGR-2: no feature imports a sibling feature's namespace."""

    def test_features_do_not_import_each_other(self):
        folders = _feature_folders()
        self.assertTrue(folders, "no feature folders found — guard is vacuous")

        hits = []
        for feature in folders:
            for path in _cs_files(os.path.join(FEATURES, feature)):
                code = _strip_line_comments(_read(path))
                for owner in _FEATURE_NS.findall(code):
                    if owner != feature and owner != "Claim":
                        hits.append(
                            "%s imports Features.%s" % (
                                os.path.relpath(path, MODULE).replace(os.sep, "/"),
                                owner))
        self.assertEqual([], hits, "feature-to-feature import: %s" % hits)


class ParentBoundaryTest(unittest.TestCase):
    """AGR-3: the parent names and constructs no feature internal."""

    def test_parent_never_touches_feature_internals(self):
        feature_types = _feature_types()
        internals = sorted(
            name for name, (visibility, _) in feature_types.items()
            if visibility == "internal")
        self.assertTrue(
            internals,
            "no internal feature types found — guard is vacuous, check _DECL")

        for path in PARENT_FILES:
            self.assertTrue(os.path.isfile(path), "parent missing: %s" % path)
            code = _strip_line_comments(_read(path))
            relative = os.path.relpath(path, MODULE).replace(os.sep, "/")

            referenced = [
                name for name in internals
                if re.search(r"\b%s\b" % re.escape(name), code)]
            self.assertEqual(
                [], referenced,
                "parent %s references internal feature type(s): %s"
                % (relative, referenced))

            constructed = [
                name for name in sorted(feature_types)
                if re.search(r"\bnew\s+%s\s*[\(\{]" % re.escape(name), code)
                and feature_types[name][0] != "public"]
            self.assertEqual(
                [], constructed,
                "parent %s constructs internal feature type(s): %s"
                % (relative, constructed))


class SharedFolderTest(unittest.TestCase):
    """AGR-4: sharedLogic holds mechanisms and must not import a feature."""

    def test_shared_folder_imports_no_feature(self):
        self.assertTrue(os.path.isdir(SHARED), "sharedLogic/ missing")

        hits = []
        for path in _cs_files(SHARED):
            code = _strip_line_comments(_read(path))
            if _FEATURE_NS.search(code):
                hits.append(os.path.relpath(path, MODULE).replace(os.sep, "/"))
        self.assertEqual([], hits, "shared folder imports a feature: %s" % hits)


class ParentContractTest(unittest.TestCase):
    """AGR-5: the parent speaks to features through public types only."""

    def test_parent_only_names_public_feature_view_types(self):
        """The parent's whole vocabulary is: public, and ends with `View`.

        Distinct from AGR-1, which only polices `internal` types — this also
        fails when a feature exposes some other public helper and the parent
        reaches for it, because that is a feature decision living in the
        composition root.
        """
        feature_types = _feature_types()
        self.assertTrue(feature_types, "no feature types found — guard is vacuous")

        allowed = sorted(
            name for name, (visibility, _) in feature_types.items()
            if visibility == "public"
            and (name.endswith("View") or name.endswith("Feature")
                 or name.startswith("I")))
        forbidden = sorted(
            name for name in feature_types if name not in allowed)
        self.assertTrue(
            allowed,
            "no public feature view found — guard is vacuous")
        self.assertTrue(
            forbidden,
            "every feature type is a public view, so the vocabulary rule "
            "cannot fail — guard is vacuous")

        hits = []
        for path in PARENT_FILES:
            code = _strip_line_comments(_read(path))
            relative = os.path.relpath(path, MODULE).replace(os.sep, "/")
            for name in forbidden:
                if re.search(r"\b%s\b" % re.escape(name), code):
                    hits.append("%s named in %s" % (name, relative))
        self.assertEqual(
            [], hits,
                "parent bypassed the public feature contract: %s" % hits)

    def test_every_feature_folder_is_self_contained(self):
        folders = _feature_folders()
        self.assertTrue(folders, "no feature folders found — guard is vacuous")

        incomplete = []
        for feature in folders:
            folder = os.path.join(FEATURES, feature)
            sources = [name for name in os.listdir(folder) if name.endswith(".cs")]
            views = [name for name in os.listdir(folder) if name.endswith(".xaml")]
            has_contract = any(
                name.startswith("I") or name.endswith("Feature")
                for name in _public_feature_types(feature))
            if not sources or (not views and not has_contract):
                incomplete.append(feature)
        self.assertEqual(
            [], incomplete,
            "feature folder(s) without both a source file and a view: %s"
            % incomplete)


if __name__ == "__main__":
    unittest.main()
