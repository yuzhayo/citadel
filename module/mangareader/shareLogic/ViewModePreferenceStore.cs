using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace Module.Mangareader.ShareLogic;

/// <summary>
/// The atomic single-key persistence mechanism behind one Grid/List preference.
/// Only the mechanism is shared: the key, the storage file and the value belong
/// to the feature that owns an instance, so Library and History can never write
/// each other's preference.
///
/// Writes go through a unique temporary file in the same folder and an atomic
/// move, so a reader only ever sees complete content. Reads are tolerant — a
/// missing, oversized or malformed file is the default mode plus a warning, and
/// is never rewritten by being read.
/// </summary>
public sealed class ViewModePreferenceStore
{
    /// <summary>Both view-mode features default to Grid.</summary>
    public const MangaViewMode DefaultMode = MangaViewMode.Grid;

    /// <summary>One key and one short value; anything larger is not this file.</summary>
    private const int MaximumContentBytes = 4 * 1024;

    private static readonly ConcurrentDictionary<string, object> SharedGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate;
    private readonly string _storagePath;

    public ViewModePreferenceStore(string preferenceKey, string fileName, string? storagePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferenceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        PreferenceKey = preferenceKey;
        _storagePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            fileName);
        _gate = SharedGates.GetOrAdd(Path.GetFullPath(_storagePath), static _ => new object());
    }

    /// <summary>The one key this instance is allowed to read or write.</summary>
    public string PreferenceKey { get; }

    public string StoragePath => _storagePath;

    public MangaViewMode Load(out string? warning)
    {
        warning = null;
        lock (_gate)
        {
            string content;
            try
            {
                if (!File.Exists(_storagePath)) return DefaultMode;

                if (new FileInfo(_storagePath).Length > MaximumContentBytes)
                {
                    warning = "The saved view mode file is too large and was ignored.";
                    return DefaultMode;
                }

                content = File.ReadAllText(_storagePath);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                warning = $"The saved view mode could not be read: {exception.Message}";
                return DefaultMode;
            }

            if (string.IsNullOrWhiteSpace(content)) return DefaultMode;

            Dictionary<string, string>? document;
            try
            {
                document = JsonSerializer.Deserialize<Dictionary<string, string>>(content);
            }
            catch (JsonException exception)
            {
                warning = $"The saved view mode file is malformed and was ignored: {exception.Message}";
                return DefaultMode;
            }

            if (document is null || !document.TryGetValue(PreferenceKey, out var raw))
            {
                return DefaultMode;
            }

            return Enum.TryParse<MangaViewMode>(raw, ignoreCase: true, out var mode)
                ? mode
                : DefaultMode;
        }
    }

    public bool Save(MangaViewMode mode, out string? warning)
    {
        warning = null;
        var document = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PreferenceKey] = mode.ToString(),
        };

        lock (_gate)
        {
            var temporaryPath = $"{_storagePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
                File.Move(temporaryPath, _storagePath, overwrite: true);
                return true;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                warning = $"The view mode could not be saved: {exception.Message}";
                return false;
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
