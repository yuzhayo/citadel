using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Level-B guard for the citadel-feature-modularity contract: the module-internal
/// boundaries that no project-level hook can see, because they live inside one
/// assembly. It fails the build on NEW coupling; the small allow-list below is
/// documented pre-existing debt, and shrinking it is the refactor goal.
/// Assertion 3 (parent references feature contracts only) is deliberately NOT
/// enforced yet: the parent still reaches feature internals (CITADEL-VIOLATIONS
/// C14) and that refactor is tracked separately.
/// </summary>
public sealed class LevelBGuardTests
{
    // Documented pre-existing cross-feature debt: the Catalog and CatalogMirror
    // hosts reference each other (CITADEL-VIOLATIONS C14 / the bidirectional host
    // edge). Anything beyond this list is new coupling and must fail.
    private static readonly HashSet<string> AcceptedCrossFeatureEdges =
    [
        "CatalogMirror->Catalog",
        "Catalog->CatalogMirror",
    ];

    // C12 status 2026-09-11: the four pure-data Comix types (ComixGenreMode,
    // ComixSortOption, ComixScrambleHeader, ComixScrambleHeaders) now live once in
    // shareLogic/Sources/ComixContracts.cs. These three remain per-provider by
    // design, not accident: ComixOptions embeds ComixContract's ordering constants
    // AND each provider's own captured rating default (Downloader = all four
    // ratings, Catalog = the site's first-browse two), and ComixBrowseQuery.Default
    // binds that default, so the three fork together. Merging them means extracting
    // the constant table and parameterizing the default — a design change needing
    // an owner decision. Until then they are the only accepted same-named types.
    private static readonly HashSet<string> AcceptedDuplicateTypeNames =
    [
        "ComixContract",
        "ComixOptions",
        "ComixBrowseQuery",
    ];

    private static string ModuleRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Citadel.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "module", "mangareader");
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
        || path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase);

    private static string? OwnFeature(string relativePath)
    {
        var parts = relativePath.Split('\\');
        return parts.Length > 1 && parts[0] == "Features" ? parts[1] : null;
    }

    [Fact]
    public void NoCrossFeatureImportBeyondAcceptedDebtAndNoSharedToFeatureImport()
    {
        var root = ModuleRoot();
        var usingRegex = new Regex(
            @"^using Module\.Mangareader\.Features\.([A-Za-z0-9_]+)",
            RegexOptions.Multiline);
        var crossFeature = new List<string>();
        var sharedToFeature = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file)) continue;
            var relative = Path.GetRelativePath(root, file);
            var own = OwnFeature(relative);
            var isShared = relative.StartsWith("shareLogic\\", StringComparison.OrdinalIgnoreCase);
            foreach (Match match in usingRegex.Matches(File.ReadAllText(file)))
            {
                var target = match.Groups[1].Value;
                if (isShared)
                {
                    sharedToFeature.Add($"{relative} -> {target}");
                }
                else if (own is not null && !string.Equals(own, target, StringComparison.Ordinal))
                {
                    crossFeature.Add($"{own}->{target} ({relative})");
                }
            }
        }

        Assert.True(
            sharedToFeature.Count == 0,
            "Level B assertion 4: module shared folder must not import a feature "
            + "(a shared->feature import creates a cycle): "
            + string.Join("; ", sharedToFeature));

        var unexpected = crossFeature
            .Where(edge => !AcceptedCrossFeatureEdges.Contains(edge.Split(" (")[0]))
            .ToList();
        Assert.True(
            unexpected.Count == 0,
            "Level B assertion 2: new feature->feature concrete import beyond the "
            + "accepted debt allow-list: " + string.Join("; ", unexpected));
    }

    [Fact]
    public void NoSameNamedPublicTypeInTwoFeatureFolders()
    {
        var root = ModuleRoot();
        var typeRegex = new Regex(
            @"^(?:public|internal)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*"
            + @"(?:class|record|struct|enum|interface)\s+([A-Za-z0-9_]+)",
            RegexOptions.Multiline);
        var ownerByType = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicates = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(root, "Features"), "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file)) continue;
            var feature = OwnFeature(Path.GetRelativePath(root, file));
            foreach (Match match in typeRegex.Matches(File.ReadAllText(file)))
            {
                var name = match.Groups[1].Value;
                if (ownerByType.TryGetValue(name, out var owner))
                {
                    if (!string.Equals(owner, feature, StringComparison.Ordinal)
                        && !AcceptedDuplicateTypeNames.Contains(name))
                    {
                        duplicates.Add($"{name} declared in {owner} and {feature}");
                    }
                }
                else
                {
                    ownerByType[name] = feature!;
                }
            }
        }

        // BUG-1 root: two same-named types in two feature namespaces let a
        // cross-feature using bind a catch to a type the thrower never throws.
        Assert.True(
            duplicates.Count == 0,
            "same-named public type in two feature folders (BUG-1 root): "
            + string.Join("; ", duplicates));
    }

    [Fact]
    public void FixedCouplingsStayRemoved()
    {
        var root = ModuleRoot();
        string Read(string relative) => File.ReadAllText(Path.Combine(root, relative));

        // BUG-1: the sync throttle catch was bound to a Downloader-namespace type
        // the Catalog provider never threw.
        Assert.DoesNotContain(
            "using Module.Mangareader.Features.Downloader",
            Read("Features\\CatalogMirror\\CatalogMirrorSyncFeature.cs"));

        // C13: the shared archive dispatcher used to import the Rar feature,
        // which imported the shared folder back (a cycle).
        Assert.DoesNotContain(
            "using Module.Mangareader.Features.Rar",
            Read("shareLogic\\Archive\\ArchivePageReader.cs"));
        Assert.DoesNotContain(
            "using Module.Mangareader.Features.Rar",
            Read("shareLogic\\Archive\\ArchiveReplacementTransaction.cs"));
    }
}
