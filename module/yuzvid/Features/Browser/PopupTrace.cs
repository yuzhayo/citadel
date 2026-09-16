using System;
using System.Diagnostics;
using System.IO;

namespace Module.Yuzvid.Features.Browser;

/// <summary>
/// R0 diagnosis trace: navigation + popup-request + session lifecycle.
/// Writes to Debug output AND a rotating file log (best effort, never throws).
/// Only hosts are logged — never full URIs (tokens stay out of the file).
/// </summary>
internal static class PopupTrace
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Citadel", "Yuzvid", "popup-trace.log");

    public static string HostOf(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return "?";
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            && !string.IsNullOrEmpty(parsed.Host)
            ? parsed.Host
            : "?";
    }

    public static void Write(string kind, string detail)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{kind}] {detail}";
        Debug.WriteLine("[Yuzvid] " + line);
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (dir is not null) Directory.CreateDirectory(dir);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var bak = LogPath + ".bak";
                    try { File.Delete(bak); } catch { }
                    File.Move(LogPath, bak);
                }
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch { /* trace must never break browsing */ }
    }
}
