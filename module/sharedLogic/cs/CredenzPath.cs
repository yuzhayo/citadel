using System.IO;

namespace CitadelBridge;

/// <summary>
/// Where the identity vault lives (plan L3): C# resolves it, always, and
/// hands the absolute path to Python via CITADEL_CREDENZ. Python never
/// computes a path itself.
///
/// Resolution order:
///   1. CITADEL_CREDENZ env override (absolute) — the host's own escape.
///   2. Dev: the repo's module/credenz, found by walking up from the shell
///      output directory and requiring the folder to be writable.
///   3. Installed: %LocalAppData%\Citadel\Credenz.
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

        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && cursor is not null; depth++)
        {
            var candidate = Path.Combine(cursor.FullName, "module", "credenz");
            if (Directory.Exists(candidate) && IsWritable(candidate))
            {
                return candidate;
            }

            cursor = cursor.Parent;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "Credenz");
    }

    public static string ProfilesRoot()
        => Path.Combine(Resolve(), "google", "profiles");

    public static string GoogleAccountsRoot()
        => Path.Combine(Resolve(), "google", "accounts");

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".write-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
