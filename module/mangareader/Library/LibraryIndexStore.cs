using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Module.Mangareader.Library;

/// <summary>
/// Owns every filesystem concern of the persisted library index: where the
/// per-root documents live (%LocalAppData%\Citadel\MangaReader\index\),
/// how they are loaded tolerantly and saved atomically. Same shape, same
/// storage folder, and same injectable file seam as the existing Library
/// path and grouping stores, so a failure can be tested without fragile
/// disk tricks.
///
/// One normalized root maps to exactly one file, so two library locations
/// never share (or clobber) an index. A damaged file, an unknown schema, or
/// a document that belongs to another root is ignored safely and left on
/// disk; the caller rebuilds. A failed or cancelled save leaves the previous
/// document untouched, so the Library never goes suddenly empty.
/// </summary>
public sealed class LibraryIndexStore
{
    public const int SchemaVersion = 1;

    /// <summary>
    /// Index entries are small (no chapter lists). 3.000 titles fit in
    /// roughly one megabyte; anything far larger cannot be this document,
    /// so it is treated as damaged rather than trusted.
    /// </summary>
    public const int MaximumContentBytes = 8 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, object> SharedGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate;
    private readonly string _storageDirectory;
    private readonly ILibraryPathFileIO _fileIO;

    public LibraryIndexStore(string? storageDirectory = null, ILibraryPathFileIO? fileIO = null)
    {
        _storageDirectory = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "index");
        _gate = SharedGates.GetOrAdd(
            Path.GetFullPath(_storageDirectory), static _ => new object());
        _fileIO = fileIO ?? LibraryPathFileIO.Instance;
    }

    /// <summary>
    /// Loads the index for a library root. A missing file is an ordinary
    /// empty result; oversized, unreadable, malformed, wrong-version, or
    /// other-root content is ignored safely and reported through a
    /// structured warning, and the file itself is left untouched rather
    /// than rewritten by a read.
    /// </summary>
    public LibraryIndexLoadResult Load(string? rawRoot)
    {
        var root = LibraryPathStore.Normalize(rawRoot);
        if (root is null)
        {
            return new LibraryIndexLoadResult(
                null,
                default,
                [],
                "The library path is empty or not an absolute path; no index was loaded.");
        }

        var storagePath = StoragePathFor(root);
        lock (_gate)
        {
            string content;
            try
            {
                if (!File.Exists(storagePath)) return LibraryIndexLoadResult.Empty;

                var size = new FileInfo(storagePath).Length;
                if (size > MaximumContentBytes)
                {
                    return new LibraryIndexLoadResult(
                        null,
                        default,
                        [],
                        "The saved library index is too large and was ignored.");
                }

                content = _fileIO.ReadAllText(storagePath);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                return new LibraryIndexLoadResult(
                    null,
                    default,
                    [],
                    $"The saved library index could not be read: {exception.Message}");
            }

            if (string.IsNullOrWhiteSpace(content)) return LibraryIndexLoadResult.Empty;

            LibraryIndexDocument? document;
            try
            {
                document = JsonSerializer.Deserialize<LibraryIndexDocument>(content);
            }
            catch (JsonException exception)
            {
                return new LibraryIndexLoadResult(
                    null,
                    default,
                    [],
                    $"The saved library index is malformed and was ignored: {exception.Message}");
            }

            if (document is null) return LibraryIndexLoadResult.Empty;

            if (document.Version != SchemaVersion)
            {
                return new LibraryIndexLoadResult(
                    null,
                    default,
                    [],
                    $"Library index version {document.Version} is not recognized; the file was kept.");
            }

            if (!string.Equals(document.LibraryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                return new LibraryIndexLoadResult(
                    null,
                    default,
                    [],
                    "The saved library index belongs to another library folder and was ignored.");
            }

            return new LibraryIndexLoadResult(
                root,
                document.UpdatedUtc,
                Sanitize(document.Entries),
                null);
        }
    }

    /// <summary>
    /// Saves the index for a library root atomically: a unique temporary
    /// file in the same folder is written and then moved over the storage
    /// file, so a reader only ever sees complete content and the previous
    /// document survives any failure. A rejected (empty or relative) root
    /// leaves every file untouched.
    /// </summary>
    public LibraryIndexSaveResult Save(string? rawRoot, IReadOnlyList<LibraryIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var root = LibraryPathStore.Normalize(rawRoot);
        if (root is null)
        {
            return new LibraryIndexSaveResult(
                false,
                "The library path is empty or not an absolute path; no index was saved.");
        }

        var document = new LibraryIndexDocument
        {
            Version = SchemaVersion,
            LibraryRoot = root,
            UpdatedUtc = DateTime.UtcNow,
            Entries = Sanitize(entries).ToList(),
        };

        lock (_gate)
        {
            var storagePath = StoragePathFor(root);
            var temporaryPath = $"{storagePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(_storageDirectory);
                _fileIO.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
                _fileIO.Move(temporaryPath, storagePath, overwrite: true);
                return new LibraryIndexSaveResult(true, null);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                return new LibraryIndexSaveResult(
                    false,
                    $"The library index could not be saved: {exception.Message}");
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Deterministic file name for a normalized root: a human-readable
    /// folder-name prefix plus a hash of the full root, so two different
    /// roots can never collide and the file stays debuggable.
    /// </summary>
    internal static string FileNameForRoot(string normalizedRoot)
    {
        var folderName = Path.GetFileName(normalizedRoot);
        var builder = new StringBuilder(folderName.Length);
        foreach (var ch in folderName)
        {
            builder.Append(
                ch == Path.DirectorySeparatorChar
                || ch == Path.AltDirectorySeparatorChar
                || Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
        }
        var safe = builder.ToString().Trim().Trim('.');
        if (safe.Length == 0) safe = "library";
        if (safe.Length > 48) safe = safe[..48];

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot.ToUpperInvariant()));
        var shortHash = Convert.ToHexString(hash)[..16].ToLowerInvariant();
        return $"{safe}.{shortHash}.library-index.json";
    }

    private string StoragePathFor(string normalizedRoot) =>
        Path.Combine(_storageDirectory, FileNameForRoot(normalizedRoot));

    /// <summary>
    /// Drops entries that cannot identify a title, canonicalizes folder paths
    /// to absolute form, and collapses duplicates (first wins), so a
    /// hand-edited file can neither crash path consumers with a relative or
    /// malformed path nor produce two cards that fight over one folder.
    /// Counts below zero are clamped.
    /// </summary>
    internal static IReadOnlyList<LibraryIndexEntry> Sanitize(IEnumerable<LibraryIndexEntry>? entries)
    {
        if (entries is null) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<LibraryIndexEntry>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.TitleFolderName)) continue;
            if (string.IsNullOrWhiteSpace(entry.FolderPath)) continue;

            var trimmed = entry.FolderPath.Trim();
            if (!Path.IsPathRooted(trimmed)) continue;

            string normalized;
            try
            {
                normalized = Path.GetFullPath(trimmed);
            }
            catch (Exception exception) when (exception is ArgumentException
                or NotSupportedException)
            {
                continue;
            }

            if (!seen.Add(normalized)) continue;

            var clean = entry;
            if (!string.Equals(clean.FolderPath, normalized, StringComparison.Ordinal))
            {
                clean = clean with { FolderPath = normalized };
            }

            results.Add(clean.ChapterCount < 0 ? clean with { ChapterCount = 0 } : clean);
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

    private sealed class LibraryIndexDocument
    {
        public int Version { get; set; }

        public string? LibraryRoot { get; set; }

        public DateTime UpdatedUtc { get; set; }

        public List<LibraryIndexEntry>? Entries { get; set; }
    }
}
