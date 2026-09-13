using System.IO;

namespace CitadelBridge;

/// <summary>
/// Where the identity vault lives (plan L3): C# resolves it, always, and
/// hands the absolute path to Python via CITADEL_CREDENZ. Python never
/// computes a path itself.
///
/// Resolution order:
///   1. CITADEL_CREDENZ env override (absolute) — the host's own escape.
///   2. %LocalAppData%\Citadel\Credenz for both development and installed
///      application runs.
///
/// A pre-unification development vault at module/credenz is copied into the
/// primary vault once per file when it is discovered. Existing primary files
/// always win, so migration cannot overwrite newer installed credentials.
/// </summary>
public static class CredenzPath
{
    public static string Resolve()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("CITADEL_CREDENZ");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)
            && Path.IsPathRooted(fromEnvironment))
        {
            return fromEnvironment;
        }

        var primary = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "Credenz");
        MigrateLegacyDevelopmentVault(primary);
        return primary;
    }

    public static string ProfilesRoot()
        => Path.Combine(Resolve(), "google", "profiles");

    public static string GoogleAccountsRoot()
        => Path.Combine(Resolve(), "google", "accounts");

    private static void MigrateLegacyDevelopmentVault(string primary)
    {
        var legacy = FindLegacyDevelopmentVault();
        if (legacy is null || string.Equals(legacy, primary, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            foreach (var source in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(legacy, source);
                var destination = Path.Combine(primary, relative);
                if (File.Exists(destination)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? FindLegacyDevelopmentVault()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && cursor is not null; depth++)
        {
            var candidate = Path.Combine(cursor.FullName, "module", "credenz");
            if (Directory.Exists(candidate)) return candidate;
            cursor = cursor.Parent;
        }
        return null;
    }
}
