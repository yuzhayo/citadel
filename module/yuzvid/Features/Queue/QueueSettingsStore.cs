using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Module.Yuzvid.Features.Queue;

/// <summary>
/// Persisted Queue preferences — one small JSON under
/// <c>%LocalAppData%\Citadel\Yuzvid\queue-settings.json</c>. Currently the
/// download folder. Fault-tolerant: any IO/parse failure falls back to the
/// default folder and never throws (a broken setting must not break the queue).
///
/// Internal to the Queue feature.
/// </summary>
internal static class QueueSettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Citadel", "Yuzvid", "queue-settings.json");

    /// <summary>Default download folder (also QueueEngine.DownloadsFolder).</summary>
    public static string DefaultDownloadFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Citadel", "Yuzvid", "Downloads");

    /// <summary>Resolved download folder: stored value when valid, else default.</summary>
    public static string ResolveDownloadFolder()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var doc = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(FilePath));
                if (doc is { DownloadFolder: { Length: > 0 } folder }
                    && Path.IsPathRooted(folder))
                    return folder;
            }
        }
        catch
        {
            // fall through to default
        }
        return DefaultDownloadFolder;
    }

    public static void SetDownloadFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        try
        {
            var doc = new SettingsDocument { DownloadFolder = Path.GetFullPath(folder) };
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(doc, WriteOptions));
        }
        catch
        {
            // best effort — persistence must never break the queue
        }
    }

    private sealed class SettingsDocument
    {
        [JsonPropertyName("download_folder")] public string? DownloadFolder { get; set; }
    }
}