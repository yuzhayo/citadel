using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace Module.Mangareader.Library.Grouping;

/// <summary>
/// One stored group. Membership is the title folder name, which is the identity
/// Library already gives a title, so a membership survives a re-scan and a
/// missing folder stays recorded instead of being silently dropped.
/// </summary>
public sealed record LibraryGroup(string Id, string Name, IReadOnlyList<string> TitleFolderNames);

public sealed record GroupingLoadResult(IReadOnlyList<LibraryGroup> Groups, string? Warning)
{
    public static GroupingLoadResult Empty { get; } = new([], null);
}

public sealed record GroupingSaveResult(bool Saved, string? Warning);

/// <summary>
/// Owns every filesystem concern of the persisted group definitions: where they
/// live (%LocalAppData%\Citadel\MangaReader\library-groups.json), how they are
/// loaded tolerantly and saved atomically. Same shape and same storage folder as
/// the existing Library path store, and the same injectable file seam, so a
/// failure can be tested without fragile disk tricks.
///
/// The store never drops a membership because its folder is currently missing:
/// availability belongs to the scanner, not to persistence.
/// </summary>
public sealed class GroupingStore
{
    public const int SchemaVersion = 1;

    /// <summary>
    /// Group definitions are names and folder names. Anything larger cannot be
    /// this document, so it is treated as damaged rather than trusted.
    /// </summary>
    public const int MaximumContentBytes = 1024 * 1024;

    private static readonly ConcurrentDictionary<string, object> SharedGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate;
    private readonly string _storagePath;
    private readonly ILibraryPathFileIO _fileIO;

    public GroupingStore(string? storagePath = null, ILibraryPathFileIO? fileIO = null)
    {
        _storagePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "library-groups.json");
        _gate = SharedGates.GetOrAdd(Path.GetFullPath(_storagePath), static _ => new object());
        _fileIO = fileIO ?? LibraryPathFileIO.Instance;
    }

    public string StoragePath => _storagePath;

    /// <summary>
    /// Loads the stored groups. A missing file is an ordinary empty result;
    /// oversized, unreadable, malformed or wrong-version content is ignored
    /// safely and reported through a structured warning, and the file itself is
    /// left untouched rather than rewritten by a read.
    /// </summary>
    public GroupingLoadResult Load()
    {
        lock (_gate)
        {
            string content;
            try
            {
                if (!File.Exists(_storagePath)) return GroupingLoadResult.Empty;

                var size = new FileInfo(_storagePath).Length;
                if (size > MaximumContentBytes)
                {
                    return new GroupingLoadResult(
                        [],
                        "The saved group file is too large and was ignored.");
                }

                content = _fileIO.ReadAllText(_storagePath);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                return new GroupingLoadResult(
                    [],
                    $"The saved groups could not be read: {exception.Message}");
            }

            if (string.IsNullOrWhiteSpace(content)) return GroupingLoadResult.Empty;

            GroupDocument? document;
            try
            {
                document = JsonSerializer.Deserialize<GroupDocument>(content);
            }
            catch (JsonException exception)
            {
                return new GroupingLoadResult(
                    [],
                    $"The saved group file is malformed and was ignored: {exception.Message}");
            }

            if (document is null) return GroupingLoadResult.Empty;

            if (document.Version != SchemaVersion)
            {
                return new GroupingLoadResult(
                    [],
                    $"Group file version {document.Version} is not recognized; the file was kept.");
            }

            return new GroupingLoadResult(Sanitize(document.Groups), null);
        }
    }

    /// <summary>
    /// Saves every group atomically: a unique temporary file in the same folder
    /// is written and then moved over the storage file, so a reader only ever
    /// sees complete content and the previous value survives any failure.
    /// </summary>
    public GroupingSaveResult Save(IReadOnlyList<LibraryGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var document = new GroupDocument { Version = SchemaVersion, Groups = Sanitize(groups).ToList() };

        lock (_gate)
        {
            var temporaryPath = $"{_storagePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
                _fileIO.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
                _fileIO.Move(temporaryPath, _storagePath, overwrite: true);
                return new GroupingSaveResult(true, null);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                return new GroupingSaveResult(
                    false,
                    $"The groups could not be saved: {exception.Message}");
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Drops entries that cannot identify a group and collapses duplicate ids
    /// and duplicate memberships, so a hand-edited file cannot produce two tabs
    /// that write to each other.
    /// </summary>
    private static IReadOnlyList<LibraryGroup> Sanitize(IEnumerable<LibraryGroup>? groups)
    {
        if (groups is null) return [];

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<LibraryGroup>();
        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.Id) || string.IsNullOrWhiteSpace(group.Name))
            {
                continue;
            }

            if (!seenIds.Add(group.Id)) continue;

            var titles = new List<string>();
            var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folderName in group.TitleFolderNames ?? [])
            {
                if (string.IsNullOrWhiteSpace(folderName)) continue;
                if (seenTitles.Add(folderName)) titles.Add(folderName);
            }

            results.Add(new LibraryGroup(group.Id, group.Name.Trim(), titles));
        }

        return results;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
        }
    }

    private sealed class GroupDocument
    {
        public int Version { get; set; }

        public List<LibraryGroup>? Groups { get; set; }
    }
}
