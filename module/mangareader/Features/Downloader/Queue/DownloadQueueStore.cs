using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

namespace Module.Mangareader.Features.Downloader.Queue;

/// <summary>A queue file that could not be written. Callers must not proceed.</summary>
public class QueuePersistenceException(string message) : IOException(message);

/// <summary>
/// The on-disk file is a schema this build does not understand. It is preserved
/// and reported rather than overwritten, because overwriting is the only way
/// this store could destroy job state it cannot interpret.
/// </summary>
public sealed class QueueVersionConflictException(int foundVersion)
    : QueuePersistenceException(
        "queue.json memakai schema version " + foundVersion
        + " yang tidak dikenali build ini; file dipertahankan.")
{
    public int FoundVersion { get; } = foundVersion;
}

public sealed record QueueLoadResult(
    IReadOnlyList<DownloadJobRecord> Jobs,
    string? Warning,
    int? UnsupportedVersion)
{
    public static QueueLoadResult Empty { get; } = new([], null, null);
}

/// <summary>
/// Small shared JSON discipline for the Downloader's own state files: bounded
/// fail-soft reads and atomic same-folder replacement. Two real consumers use
/// it (the queue and the source index), so it lives beside them instead of
/// being duplicated in each.
/// </summary>
internal static class DownloaderJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Process-wide gates keyed by storage path, so two instances in the same
    /// process never interleave writes to one file.
    /// </summary>
    private static readonly ConcurrentDictionary<string, object> SharedGates =
        new(StringComparer.OrdinalIgnoreCase);

    public static object GateFor(string path) =>
        SharedGates.GetOrAdd(Path.GetFullPath(path), static _ => new object());

    public static string DefaultRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "downloads");

    /// <summary>
    /// Reads and deserializes, treating a missing, oversized, malformed or
    /// unreadable file as an empty result with a structured warning. A read
    /// failure is never allowed to look like "the queue is empty and safe to
    /// replace" — the caller decides, and version handling stays explicit.
    /// </summary>
    public static TState? ReadBounded<TState>(string path, long maximumBytes, out string? warning)
        where TState : class
    {
        warning = null;
        if (!File.Exists(path)) return null;

        string content;
        try
        {
            var size = new FileInfo(path).Length;
            if (size > maximumBytes)
            {
                warning = $"File state terlalu besar ({size} byte) dan diabaikan.";
                return null;
            }

            content = File.ReadAllText(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            warning = $"File state tidak dapat dibaca: {exception.Message}";
            return null;
        }

        if (string.IsNullOrWhiteSpace(content)) return null;

        try
        {
            return JsonSerializer.Deserialize<TState>(content, Options);
        }
        catch (JsonException exception)
        {
            warning = $"File state rusak dan diabaikan: {exception.Message}";
            return null;
        }
    }

    /// <summary>
    /// Writes a unique temporary file in the destination folder and moves it
    /// over the target, so a reader in another process only ever sees complete
    /// content and a failed write leaves the previous value intact.
    /// </summary>
    public static void WriteAtomic(string path, object state)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(state, Options),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new QueuePersistenceException(
                $"State tidak dapat disimpan ke {path}: {exception.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// The sole durable owner of job identity, order and state. Everything else in
/// the Downloader reads a snapshot from here; nothing else writes it.
/// </summary>
public sealed class DownloadQueueStore
{
    public const int SchemaVersion = 1;
    private const long MaximumFileBytes = 8 * 1024 * 1024;

    private readonly object _gate;
    private readonly string _path;

    public DownloadQueueStore(string? directory = null)
    {
        _path = Path.Combine(directory ?? DownloaderJson.DefaultRoot(), "queue.json");
        _gate = DownloaderJson.GateFor(_path);
    }

    public string FilePath => _path;

    public QueueLoadResult Load()
    {
        lock (_gate)
        {
            var document = DownloaderJson.ReadBounded<QueueDocument>(
                _path,
                MaximumFileBytes,
                out var warning);
            if (document is null)
            {
                return new QueueLoadResult([], warning, null);
            }

            if (document.Version != SchemaVersion)
            {
                return new QueueLoadResult(
                    [],
                    warning ?? $"queue.json schema version {document.Version} tidak dikenali; dipertahankan.",
                    document.Version);
            }

            return new QueueLoadResult(
                document.Jobs ?? [],
                warning,
                null);
        }
    }

    /// <summary>
    /// Commits the whole queue atomically. A failure throws, because a state
    /// transition that cannot be written down would leave on-disk staging
    /// ambiguously owned.
    /// </summary>
    public void Save(IReadOnlyList<DownloadJobRecord> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        lock (_gate)
        {
            // Never replace a file this build cannot interpret.
            var existing = DownloaderJson.ReadBounded<QueueDocument>(
                _path,
                MaximumFileBytes,
                out _);
            if (existing is not null && existing.Version != SchemaVersion)
            {
                throw new QueueVersionConflictException(existing.Version);
            }

            DownloaderJson.WriteAtomic(
                _path,
                new QueueDocument { Version = SchemaVersion, Jobs = [.. jobs] });
        }
    }

    private sealed class QueueDocument
    {
        public int Version { get; set; }

        public List<DownloadJobRecord>? Jobs { get; set; }
    }
}
