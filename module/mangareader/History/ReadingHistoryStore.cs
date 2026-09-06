using System.IO;
using System.Text.Json;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.History;

public sealed record ReadingHistoryEntry(
    string Title,
    string TitleFolderPath,
    string ChapterTitle,
    string ChapterFilePath,
    DateTimeOffset LastOpenedUtc,
    bool Pinned = false);

/// <summary>
/// The single durable History owner. Recording, pinning and clearing all write
/// through this one store and this one file, so Clear History and Pinned History
/// stay independent commands without becoming competing persistence writers.
///
/// Pinned entries sit above ordinary ones, survive clearing, and do not count
/// toward the ordinary retention limit.
/// </summary>
public sealed class ReadingHistoryStore
{
    /// <summary>Ordinary recent-history retention. Pinned entries are exempt.</summary>
    public const int MaximumUnpinnedEntries = 15;

    private readonly object _gate = new();
    private readonly string _path;

    public ReadingHistoryStore(string? storagePath = null)
    {
        _path = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "history.json");
    }

    public string StoragePath => _path;

    public void Record(MangaTitle title, ChapterInfo chapter)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(chapter);

        lock (_gate)
        {
            var entries = ReadCore().ToList();
            var existing = entries.FirstOrDefault(entry => string.Equals(
                entry.TitleFolderPath,
                title.FolderPath,
                StringComparison.OrdinalIgnoreCase));

            entries.RemoveAll(entry => string.Equals(
                entry.TitleFolderPath,
                title.FolderPath,
                StringComparison.OrdinalIgnoreCase));
            entries.Add(new ReadingHistoryEntry(
                title.Title,
                title.FolderPath,
                chapter.Title,
                chapter.FilePath,
                DateTimeOffset.UtcNow,
                // Re-opening a title updates its position, never its pin.
                existing?.Pinned ?? false));

            WriteCore(ApplyRetention(entries));
        }
    }

    /// <summary>
    /// Pins or unpins one entry. Unpinning returns the entry to ordinary
    /// retention instead of deleting it, so it disappears only if ordinary
    /// retention naturally evicts it.
    /// </summary>
    public bool SetPinned(string titleFolderPath, bool pinned)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titleFolderPath);

        lock (_gate)
        {
            var entries = ReadCore().ToList();
            var index = entries.FindIndex(entry => string.Equals(
                entry.TitleFolderPath,
                titleFolderPath,
                StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;

            entries[index] = entries[index] with { Pinned = pinned };
            WriteCore(ApplyRetention(entries));
            return true;
        }
    }

    /// <summary>
    /// Removes ordinary history only. Pinned entries and every manga file are
    /// left untouched, and a failure throws before the file is rewritten so the
    /// durable history survives intact.
    /// </summary>
    public int ClearUnpinned()
    {
        lock (_gate)
        {
            var entries = ReadCore().ToList();
            var remaining = entries.Where(entry => entry.Pinned).ToList();
            var removed = entries.Count - remaining.Count;
            if (removed == 0) return 0;

            WriteCore(remaining);
            return removed;
        }
    }

    public IReadOnlyList<ReadingHistoryEntry> Read()
    {
        lock (_gate)
        {
            return ReadCore();
        }
    }

    /// <summary>
    /// Ordinary retention: every pinned entry is kept, and only the most recent
    /// <see cref="MaximumUnpinnedEntries"/> unpinned entries survive.
    /// </summary>
    private static IReadOnlyList<ReadingHistoryEntry> ApplyRetention(
        IEnumerable<ReadingHistoryEntry> entries) =>
        entries.Where(entry => entry.Pinned)
            .Concat(entries
                .Where(entry => !entry.Pinned)
                .OrderByDescending(entry => entry.LastOpenedUtc)
                .Take(MaximumUnpinnedEntries))
            .ToArray();

    private IReadOnlyList<ReadingHistoryEntry> ReadCore()
    {
        if (!File.Exists(_path)) return Array.Empty<ReadingHistoryEntry>();

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<ReadingHistoryEntry[]>(json)
                ?.Where(IsValid)
                .OrderByDescending(entry => entry.Pinned)
                .ThenByDescending(entry => entry.LastOpenedUtc)
                .ToArray()
                ?? Array.Empty<ReadingHistoryEntry>();
        }
        catch (IOException)
        {
            return Array.Empty<ReadingHistoryEntry>();
        }
        catch (JsonException)
        {
            return Array.Empty<ReadingHistoryEntry>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<ReadingHistoryEntry>();
        }
    }

    private void WriteCore(IReadOnlyList<ReadingHistoryEntry> entries)
    {
        var folder = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(folder);
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool IsValid(ReadingHistoryEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.TitleFolderPath)
        && !string.IsNullOrWhiteSpace(entry.ChapterFilePath)
        && entry.LastOpenedUtc != default;
}
