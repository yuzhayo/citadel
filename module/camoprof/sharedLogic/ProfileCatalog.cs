using System.IO;
using System.Text.RegularExpressions;
using CitadelBridge;
using Module.Camoprof.Providers.Google;

namespace Module.Camoprof.SharedLogic;

/// <summary>
/// CamoProf's profile filesystem boundary. Launcher uses one validated root
/// for scan and safe deletion.
/// </summary>
internal sealed class ProfileCatalog
{
    private static readonly Regex ProfileName = new(
        "^[A-Za-z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _root = CredenzPath.ProfilesRoot();
    private readonly GoogleCredentialStore _credentials;

    public ProfileCatalog(GoogleCredentialStore credentials)
        => _credentials = credentials;

    private static bool IsValidName(string name)
        => ProfileName.IsMatch(name) && name is not "." and not "..";

    public Task<IReadOnlyList<ProfileEntry>> ScanAsync(
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<ProfileEntry>>(
            () => Scan(cancellationToken),
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

    private List<ProfileEntry> Scan(CancellationToken cancellationToken)
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
            var account = _credentials.TryLoad(info.Name);
            rows.Add(new ProfileEntry(
                info.Name,
                account?.Email ?? info.Name,
                account?.Email,
                account is not null));
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
}
