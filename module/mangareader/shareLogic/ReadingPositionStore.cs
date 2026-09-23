using System.IO;
using System.Text.Json;

namespace Module.Mangareader.ShareLogic;

/// <summary>
/// Persists the last reading position (0..1 through the chapter) per chapter file
/// path so a chapter can be reopened where the reader left off. File-backed and
/// atomic; a missing or damaged file is treated as "no saved position", never an
/// error. Neutral mechanism: the Reader writes it, the Library detail reads it.
/// </summary>
public sealed class ReadingPositionStore
{
    public static ReadingPositionStore Shared { get; } = new();

    private const int MaxEntries = 500;

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, double>? _cache;

    public ReadingPositionStore(string? storagePath = null)
    {
        _path = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "reading-positions.json");
    }

    public double? Get(string chapterFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chapterFilePath);
        lock (_gate)
        {
            return Load().TryGetValue(chapterFilePath, out var progress) ? progress : null;
        }
    }

    public void Set(string chapterFilePath, double progress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chapterFilePath);
        var clamped = Math.Clamp(double.IsFinite(progress) ? progress : 0, 0, 1);
        lock (_gate)
        {
            var map = Load();
            map[chapterFilePath] = clamped;
            Trim(map);
            Save(map);
        }
    }

    public void Clear(string chapterFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chapterFilePath);
        lock (_gate)
        {
            var map = Load();
            if (map.Remove(chapterFilePath)) Save(map);
        }
    }

    private static void Trim(Dictionary<string, double> map)
    {
        while (map.Count > MaxEntries)
        {
            var oldest = map.Keys.First();
            map.Remove(oldest);
        }
    }

    private Dictionary<string, double> Load()
    {
        if (_cache is not null) return _cache;

        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_path));
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("positions", out var positions)
                    && positions.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in positions.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Number
                            && property.Value.TryGetDouble(out var value)
                            && double.IsFinite(value))
                        {
                            map[property.Name] = Math.Clamp(value, 0, 1);
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            map.Clear();
        }

        _cache = map;
        return _cache;
    }

    private void Save(Dictionary<string, double> map)
    {
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(new { version = 1, positions = map }));
            File.Move(temporary, _path, overwrite: true);
            _cache = map;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }
}
