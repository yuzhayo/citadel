using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Module.Camoprof.Tests;

/// <summary>
/// Level-B guard for the citadel-feature-modularity contract, camoprof edition:
/// the module-internal boundaries no project-level hook can see, because they
/// live inside one assembly. Fails the build on NEW coupling; the allow-list is
/// documented pre-existing debt and shrinking it is the goal.
/// Assertion 3 (parent references feature contracts only) is deliberately not
/// enforced: the camoprof parent still hard-wires its subsystems (remaining C14),
/// and that refactor is tracked separately.
/// </summary>
public sealed class CamoprofLevelBGuardTests
{
    // Documented pre-existing edge: ProfileActions consumes AddProfile's public
    // contract record (AddProfileRequest) to hand a request back through the
    // parent. Contract-only, not internals, so it is accepted; anything beyond
    // this list is new coupling and must fail.
    private static readonly HashSet<string> AcceptedCrossFeatureEdges =
    [
        "ProfileActions->AddProfile",
    ];

    // Baseline scan 2026-09-11 found no same-named public type in two feature
    // folders, so the list stays empty: any new one is the BUG-1 hazard class.
    private static readonly HashSet<string> AcceptedDuplicateTypeNames = [];

    private static string ModuleRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Citadel.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "module", "camoprof");
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
            @"^using Module\.Camoprof\.Features\.([A-Za-z0-9_]+)",
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
            "Level B assertion 4: module shared folder must not import a feature: "
            + string.Join("; ", sharedToFeature));

        var unexpected = crossFeature
            .Where(edge => !AcceptedCrossFeatureEdges.Contains(edge.Split(" (")[0]))
            .ToList();
        Assert.True(
            unexpected.Count == 0,
            "Level B assertion 2: new feature->feature import beyond accepted debt: "
            + string.Join("; ", unexpected));
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

        Assert.True(
            duplicates.Count == 0,
            "same-named public type in two feature folders (BUG-1 hazard class): "
            + string.Join("; ", duplicates));
    }
}
