using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CitadelBridge;

namespace Module.Agentrouter.SharedLogic;

/// <summary>
/// Agentrouter's shortcut store: which CamoProf profiles this screen points at.
///
/// Two responsibilities, deliberately separated from presentation:
///   ScanAvailable  — read-only view of CamoProf's profile vault
///   Load/Add/Remove — this screen's own store
///
/// Scanning shares <see cref="CredenzPath"/> (a mechanism every citizen
/// compiles in); it never reaches into CamoProf's ProfileCatalog, which stays
/// private to that screen.
/// </summary>
internal sealed class ShortcutCatalog
{
    private const int Schema = 1;
    private const string StoreFileName = "shortcuts.json";

    private const string CitizenFolder = "agentrouter";

    private static readonly Regex ProfileIdPattern = new(
        "^[A-Za-z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _storePath;
    private readonly object _sync = new();

    public ShortcutCatalog(string? storePath = null)
        => _storePath = storePath ?? DefaultStorePath();

    /// <summary>
    /// Keep shortcuts in Agentrouter's own durable data folder, separate from
    /// CamoProf's Credenz vault and the deployed module folder.
    /// A malformed store fails soft to empty, matching the shared sidecars.
    /// </summary>
    public static string DefaultStorePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            CitizenFolder,
            StoreFileName);

    /// <summary>Profile folder names currently present in CamoProf's vault.</summary>
    public IReadOnlyList<string> ScanAvailable()
    {
        var root = CredenzPath.ProfilesRoot();
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory
            .EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Where(IsSafeProfileId)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<ShortcutEntry> Load()
    {
        lock (_sync)
        {
            return LoadUnlocked();
        }
    }

    public void Add(string profileId)
    {
        if (!IsSafeProfileId(profileId))
        {
            throw new InvalidOperationException("nama profile tidak sah");
        }

        lock (_sync)
        {
            var entries = LoadUnlocked().ToList();
            if (entries.Any(entry => SameId(entry.ProfileId, profileId)))
            {
                return;
            }

            entries.Add(new ShortcutEntry(profileId, DateTimeOffset.UtcNow));
            SaveUnlocked(entries);
        }
    }

    /// <returns>True when a shortcut was removed; false when none matched.</returns>
    public bool Remove(string profileId)
    {
        lock (_sync)
        {
            var entries = LoadUnlocked().ToList();
            var kept = entries.Where(entry => !SameId(entry.ProfileId, profileId)).ToList();
            if (kept.Count == entries.Count)
            {
                return false;
            }

            SaveUnlocked(kept);
            return true;
        }
    }

    public void SetProxy(string profileId, string selectedProxy)
    {
        if (!IsSafeProfileId(profileId))
        {
            throw new InvalidOperationException("nama profile tidak sah");
        }

        ArgumentNullException.ThrowIfNull(selectedProxy);
        lock (_sync)
        {
            var entries = LoadUnlocked().ToList();
            var index = entries.FindIndex(entry => SameId(entry.ProfileId, profileId));
            if (index < 0)
            {
                return;
            }

            entries[index] = entries[index] with { SelectedProxy = selectedProxy };
            SaveUnlocked(entries);
        }
    }

    private static bool SameId(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A folder name is a path segment here, so it is validated as one: the
    /// character allow-list plus an explicit rejection of the two traversal
    /// names the allow-list would otherwise admit.
    /// </summary>
    private static bool IsSafeProfileId(string? value)
        => !string.IsNullOrWhiteSpace(value)
        && ProfileIdPattern.IsMatch(value)
        && value is not "." and not "..";

    private IReadOnlyList<ShortcutEntry> LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return [];
            }

            var document = JsonSerializer.Deserialize<ShortcutDocument>(
                File.ReadAllText(_storePath),
                JsonOptions);
            if (document is null || document.Schema != Schema)
            {
                return [];
            }

            return (document.Profiles ?? [])
                .Where(entry => IsSafeProfileId(entry.ProfileId))
                .OrderBy(entry => entry.ProfileId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void SaveUnlocked(IReadOnlyList<ShortcutEntry> entries)
    {
        var document = new ShortcutDocument(
            Schema,
            entries
                .OrderBy(entry => entry.ProfileId, StringComparer.OrdinalIgnoreCase)
                .ToArray());

        var folder = Path.GetDirectoryName(_storePath)!;
        Directory.CreateDirectory(folder);

        // Write beside the target, then swap: a reader never observes a
        // half-written store, and an interrupted save leaves the old one intact.
        var staging = _storePath + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(staging, _storePath, overwrite: true);
    }

    private sealed record ShortcutDocument(int Schema, ShortcutEntry[]? Profiles);
}
