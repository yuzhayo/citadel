using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace Module.Mangareader.Library.UpdateChecker;

/// <summary>
/// One confirmed update binding: a local title folder, the provider identity it
/// belongs to, and the single preferred source group its chapters are compared
/// against. Provider identity is authoritative; the display name, slug and
/// canonical URL are provenance, and the URL stays empty when the provider
/// contract handed over a relative slug, because resolving a route is the
/// adapter's job and not this store's.
///
/// The display name is stored rather than re-fetched on every check, so a binding
/// reloaded after a restart still names the title the way the provider names it
/// and the download queue never has to fall back to the local folder name.
///
/// Credentials, cookies, raw provider responses and temporary page or image URLs
/// are never part of this record.
/// </summary>
public sealed record SourceBinding(
    string ProviderId,
    string RemoteTitleId,
    string RemoteTitleHid,
    string RemoteTitleName,
    string Slug,
    string CanonicalTitleUrl,
    string LocalFolderName,
    string PreferredGroupId,
    string PreferredGroupName,
    DateTimeOffset LastCheckedUtc);

public sealed record BindingLoadResult(IReadOnlyList<SourceBinding> Bindings, string? Warning)
{
    public static BindingLoadResult Empty { get; } = new([], null);
}

public sealed record BindingSaveResult(bool Saved, string? Warning);

/// <summary>
/// Owns the durable update bindings, keyed one per local title folder. Same
/// storage folder, atomic-write shape and tolerant-read shape as Library's other
/// persistence: a malformed file is a local warning and is never rewritten by
/// being read, and a partial write can never be observed.
/// </summary>
public sealed class SourceBindingStore
{
    public const int SchemaVersion = 1;

    /// <summary>Bindings are identifiers and names; anything larger is not this file.</summary>
    public const int MaximumContentBytes = 4 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, object> SharedGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate;
    private readonly string _storagePath;

    public SourceBindingStore(string? storagePath = null)
    {
        _storagePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "update-bindings.json");
        _gate = SharedGates.GetOrAdd(Path.GetFullPath(_storagePath), static _ => new object());
    }

    public string StoragePath => _storagePath;

    public BindingLoadResult Load()
    {
        lock (_gate)
        {
            string content;
            try
            {
                if (!File.Exists(_storagePath)) return BindingLoadResult.Empty;

                if (new FileInfo(_storagePath).Length > MaximumContentBytes)
                {
                    return new BindingLoadResult(
                        [],
                        "The saved update binding file is too large and was ignored.");
                }

                content = File.ReadAllText(_storagePath);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                return new BindingLoadResult(
                    [],
                    $"The saved update bindings could not be read: {exception.Message}");
            }

            if (string.IsNullOrWhiteSpace(content)) return BindingLoadResult.Empty;

            BindingDocument? document;
            try
            {
                document = JsonSerializer.Deserialize<BindingDocument>(content);
            }
            catch (JsonException exception)
            {
                return new BindingLoadResult(
                    [],
                    $"The saved update binding file is malformed and was ignored: {exception.Message}");
            }

            if (document is null) return BindingLoadResult.Empty;

            if (document.Version != SchemaVersion)
            {
                return new BindingLoadResult(
                    [],
                    $"Update binding version {document.Version} is not recognized; the file was kept.");
            }

            return new BindingLoadResult(Sanitize(document.Bindings), null);
        }
    }

    /// <summary>The stored binding for one local title folder, or null.</summary>
    public SourceBinding? Find(string localFolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localFolderName);
        return Load().Bindings.FirstOrDefault(binding => string.Equals(
            binding.LocalFolderName,
            localFolderName,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Upserts one binding. A local folder holds exactly one binding, so
    /// confirming a different provider or group replaces the previous one rather
    /// than adding a second authority for the same title.
    /// </summary>
    public BindingSaveResult Save(SourceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!IsUsable(binding))
        {
            return new BindingSaveResult(false, "A binding needs a provider, a remote title, a folder and a group.");
        }

        return SaveAll(Replace(Load().Bindings, binding));
    }

    public BindingSaveResult SaveAll(IReadOnlyList<SourceBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var document = new BindingDocument
        {
            Version = SchemaVersion,
            Bindings = Sanitize(bindings).ToList(),
        };

        lock (_gate)
        {
            var temporaryPath = $"{_storagePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
                File.Move(temporaryPath, _storagePath, overwrite: true);
                return new BindingSaveResult(true, null);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                return new BindingSaveResult(
                    false,
                    $"The update bindings could not be saved: {exception.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static IReadOnlyList<SourceBinding> Replace(
        IReadOnlyList<SourceBinding> existing,
        SourceBinding binding)
    {
        var results = existing
            .Where(candidate => !string.Equals(
                candidate.LocalFolderName,
                binding.LocalFolderName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        results.Add(binding);
        return results;
    }

    private static bool IsUsable(SourceBinding binding) =>
        !string.IsNullOrWhiteSpace(binding.ProviderId)
        && !string.IsNullOrWhiteSpace(binding.RemoteTitleId)
        && !string.IsNullOrWhiteSpace(binding.LocalFolderName)
        && !string.IsNullOrWhiteSpace(binding.PreferredGroupId);

    /// <summary>
    /// Drops entries that cannot identify a binding and collapses duplicate
    /// folders, so a hand-edited file cannot leave two authorities for one title.
    /// </summary>
    private static IReadOnlyList<SourceBinding> Sanitize(IEnumerable<SourceBinding>? bindings)
    {
        if (bindings is null) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<SourceBinding>();
        foreach (var binding in bindings)
        {
            if (!IsUsable(binding)) continue;
            if (!seen.Add(binding.LocalFolderName)) continue;
            results.Add(binding);
        }

        return results;
    }

    private sealed class BindingDocument
    {
        public int Version { get; set; }

        public List<SourceBinding>? Bindings { get; set; }
    }
}
