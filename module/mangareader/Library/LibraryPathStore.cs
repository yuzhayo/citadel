using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace Module.Mangareader.Library;

public sealed record LibraryPathLoadResult(string? Path, string? Warning)
{
    public static LibraryPathLoadResult Empty { get; } = new(null, null);
}

public sealed record LibraryPathSaveResult(bool Saved, string? Warning);

/// <summary>
/// Owns every filesystem concern of the persisted library path: where it
/// lives (%LocalAppData%\Citadel\MangaReader\library-path.txt), how it is
/// normalized, loaded, saved atomically, and how damage is tolerated.
/// Living under LocalAppData keeps the value alive across app updates,
/// because nothing is written into the deployment/module folder.
///
/// The store never deletes a saved path just because the folder is
/// currently unavailable: availability is the scanner's business, not the
/// persistence's.
/// </summary>
public sealed class LibraryPathStore
{
    /// <summary>
    /// Process-wide gates keyed by storage path: every LibraryPathStore
    /// instance in the process that targets the same file serializes
    /// against the same gate, so multiple instances never interleave
    /// writes. Readers in other processes only ever observe complete
    /// content because replacement is an atomic move.
    /// </summary>
    private static readonly ConcurrentDictionary<string, object> SharedGates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Anything larger than this cannot be a folder path; a bigger file is
    /// treated as damaged instead of being trusted.
    /// </summary>
    public const int MaximumContentBytes = 32 * 1024;

    private readonly object _gate;
    private readonly string _storagePath;
    private readonly ILibraryPathFileIO _fileIO;

    public LibraryPathStore(string? storagePath = null, ILibraryPathFileIO? fileIO = null)
    {
        _storagePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "library-path.txt");
        _gate = SharedGates.GetOrAdd(Path.GetFullPath(_storagePath), static _ => new object());
        _fileIO = fileIO ?? LibraryPathFileIO.Instance;
    }

    public string StoragePath => _storagePath;

    /// <summary>
    /// Normalizes a library folder path for storage: trimmed, absolute,
    /// fully resolved, without a trailing directory separator. Returns
    /// null when the input is empty, whitespace, or not an absolute path.
    /// </summary>
    public static string? Normalize(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return null;
        var trimmed = rawPath.Trim();
        if (!Path.IsPathRooted(trimmed)) return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads the saved path. A missing file is an ordinary empty result,
    /// never an error. Empty, oversized, malformed, non-absolute or
    /// unreadable content is ignored safely and reported through a
    /// structured warning instead of an exception.
    /// </summary>
    public LibraryPathLoadResult Load()
    {
        lock (_gate)
        {
            string content;
            try
            {
                if (!File.Exists(_storagePath)) return LibraryPathLoadResult.Empty;

                var size = new FileInfo(_storagePath).Length;
                if (size > MaximumContentBytes)
                {
                    return new LibraryPathLoadResult(
                        null,
                        "The saved library path file is too large and was ignored.");
                }

                content = _fileIO.ReadAllText(_storagePath);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                return new LibraryPathLoadResult(
                    null,
                    $"The saved library path could not be read: {exception.Message}");
            }

            var trimmed = content.Trim();
            if (trimmed.Length == 0)
            {
                return new LibraryPathLoadResult(
                    null,
                    "The saved library path file is empty and was ignored.");
            }

            var normalized = Normalize(trimmed);
            if (normalized is null)
            {
                return new LibraryPathLoadResult(
                    null,
                    "The saved library path is not a valid absolute folder path and was ignored.");
            }

            return new LibraryPathLoadResult(normalized, null);
        }
    }

    /// <summary>
    /// Saves the path atomically: a unique temporary file in the same
    /// folder is written and then moved over the storage file, so readers
    /// only ever see complete content. The previous value survives any
    /// failure, a rejected (empty or relative) input leaves the file
    /// untouched, and the store is safe for multiple instances in the same
    /// process. The availability of the folder itself is not checked here.
    /// </summary>
    public LibraryPathSaveResult Save(string? rawPath)
    {
        var normalized = Normalize(rawPath);
        if (normalized is null)
        {
            return new LibraryPathSaveResult(
                false,
                "The library path is empty or not an absolute path; the saved value was left unchanged.");
        }

        lock (_gate)
        {
            var temporaryPath = $"{_storagePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
                _fileIO.WriteAllText(temporaryPath, normalized);
                _fileIO.Move(temporaryPath, _storagePath, overwrite: true);
                return new LibraryPathSaveResult(true, null);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                return new LibraryPathSaveResult(
                    false,
                    $"The library path could not be saved: {exception.Message}");
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
}

/// <summary>
/// Filesystem seam for the persistence store. Production uses the real
/// file APIs; tests inject failures without fragile disk tricks.
/// </summary>
public interface ILibraryPathFileIO
{
    string ReadAllText(string path);

    void WriteAllText(string path, string content);

    void Move(string sourcePath, string destinationPath, bool overwrite);
}

public sealed class LibraryPathFileIO : ILibraryPathFileIO
{
    public static LibraryPathFileIO Instance { get; } = new();

    private LibraryPathFileIO()
    {
    }

    public string ReadAllText(string path) => File.ReadAllText(path, Encoding.UTF8);

    public void WriteAllText(string path, string content) =>
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public void Move(string sourcePath, string destinationPath, bool overwrite) =>
        File.Move(sourcePath, destinationPath, overwrite);
}
