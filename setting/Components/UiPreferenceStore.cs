using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Citadel.Setting.Components;

/// <summary>
/// One small, atomic store for non-sensitive UI preferences. Feature data and
/// transient input never enter this file; controls opt in with a stable key.
/// </summary>
internal sealed class UiPreferenceStore
{
    private const int SchemaVersion = 1;
    private const int MaximumBytes = 256 * 1024;
    private const int MaximumValueLength = 64 * 1024;

    private static readonly ConcurrentDictionary<string, object> Gates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate;
    private readonly string _path;

    public UiPreferenceStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "ui-preferences.json"));
        _gate = Gates.GetOrAdd(_path, static _ => new object());
    }

    public string? Read(string key)
    {
        if (!ValidKey(key)) return null;
        lock (_gate)
        {
            return ReadDocument()?.Values.GetValueOrDefault(key);
        }
    }

    public void Write(string key, string value)
    {
        if (!ValidKey(key) || value.Length > MaximumValueLength) return;

        lock (_gate)
        {
            var document = ReadDocument() ?? new PreferenceDocument();
            document.Values[key] = value;

            var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(
                    temporary,
                    JsonSerializer.Serialize(document, JsonOptions),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or JsonException)
            {
                // A preference must never break or block its owning screen.
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private PreferenceDocument? ReadDocument()
    {
        try
        {
            if (!File.Exists(_path)) return new PreferenceDocument();
            if (new FileInfo(_path).Length > MaximumBytes) return null;

            var document = JsonSerializer.Deserialize<PreferenceDocument>(File.ReadAllText(_path));
            return document is { Version: SchemaVersion, Values: not null }
                ? document
                : null;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return null;
        }
    }

    private static bool ValidKey(string key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= 200;

    private sealed class PreferenceDocument
    {
        public int Version { get; set; } = SchemaVersion;

        public Dictionary<string, string> Values { get; set; } =
            new(StringComparer.Ordinal);
    }
}
