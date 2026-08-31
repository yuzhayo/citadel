using System.IO;
using System.Text.RegularExpressions;
using CitadelBridge;

namespace Module.Camoprof.SharedLogic;

/// <summary>
/// CamoProf's profile filesystem boundary. Both Launcher and Editor use the
/// same validated root and deterministic ordering.
/// </summary>
internal sealed class ProfileCatalog
{
    private static readonly Regex ProfileName = new(
        "^[A-Za-z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _root = CredenzPath.ProfilesRoot();

    public bool IsValidName(string name)
        => ProfileName.IsMatch(name) && name is not "." and not "..";

    public bool Exists(string name)
        => IsValidName(name) && Directory.Exists(Path.Combine(_root, name));

    public Task<IReadOnlyList<ProfileEntry>> ScanAsync(
        bool includeSize,
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<ProfileEntry>>(
            () => Scan(includeSize, cancellationToken),
            cancellationToken);

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!IsValidName(name))
        {
            throw new InvalidOperationException("nama profile tidak sah");
        }

        var target = ResolveSafeTarget(name);
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }
            },
            cancellationToken);
    }

    private List<ProfileEntry> Scan(bool includeSize, CancellationToken cancellationToken)
    {
        var rows = new List<ProfileEntry>();
        if (!Directory.Exists(_root))
        {
            return rows;
        }

        foreach (var directory in Directory
                     .EnumerateDirectories(_root)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(directory);
            rows.Add(new ProfileEntry(
                info.Name,
                info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                includeSize ? FormatSize(DirectorySize(directory)) : string.Empty));
        }

        return rows;
    }

    private string ResolveSafeTarget(string name)
    {
        var root = Path.GetFullPath(_root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(root, name));
        if (!target.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("penghapusan ditolak: path keluar dari root profile");
        }

        return target;
    }

    private static long DirectorySize(string directory)
    {
        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch (Exception)
        {
            return -1; // a live browser can lock files; size is cosmetic
        }
    }

    private static string FormatSize(long bytes)
        => bytes < 0 ? "—"
            : bytes < 1L << 20 ? (bytes / 1024.0).ToString("0.#") + " KB"
            : bytes < 1L << 30 ? (bytes / 1048576.0).ToString("0.#") + " MB"
            : (bytes / 1073741824.0).ToString("0.##") + " GB";
}
