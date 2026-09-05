using System.IO;
using System.Text.Json;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.History;

public sealed record ReadingHistoryEntry(
    string Title,
    string TitleFolderPath,
    string ChapterTitle,
    string ChapterFilePath,
    DateTimeOffset LastOpenedUtc);

public sealed class ReadingHistoryStore
{
    private readonly object _gate = new();
    private readonly string _path;

    public ReadingHistoryStore()
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "history.json");
    }

    public void Record(MangaTitle title, ChapterInfo chapter)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(chapter);

        lock (_gate)
        {
            var entries = ReadCore().ToList();
            entries.RemoveAll(entry => string.Equals(
                entry.TitleFolderPath,
                title.FolderPath,
                StringComparison.OrdinalIgnoreCase));
            entries.Add(new ReadingHistoryEntry(
                title.Title,
                title.FolderPath,
                chapter.Title,
                chapter.FilePath,
                DateTimeOffset.UtcNow));
            WriteCore(entries.OrderByDescending(entry => entry.LastOpenedUtc).ToArray());
        }
    }

    public IReadOnlyList<ReadingHistoryEntry> Read()
    {
        lock (_gate)
        {
            return ReadCore();
        }
    }

    private IReadOnlyList<ReadingHistoryEntry> ReadCore()
    {
        if (!File.Exists(_path)) return Array.Empty<ReadingHistoryEntry>();

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<ReadingHistoryEntry[]>(json)
                ?.Where(IsValid)
                .OrderByDescending(entry => entry.LastOpenedUtc)
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
