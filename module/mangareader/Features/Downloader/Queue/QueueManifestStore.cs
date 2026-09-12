using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Queue;

internal sealed class QueueManifestStore(string path)
{
    private sealed record Snapshot(int Schema, string Hash, RemoteChapterManifest Manifest);

    public RemoteChapterManifest? Read(RemoteChapterIdentity chapter)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path));
            return saved is { Schema: 1, Manifest: not null } && saved.Manifest.Chapter == chapter
                && saved.Hash == Hash(saved.Manifest) ? saved.Manifest : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Write(RemoteChapterManifest manifest) =>
        DownloaderJson.WriteAtomic(path, new Snapshot(1, Hash(manifest), manifest));

    private static string Hash(RemoteChapterManifest manifest) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest)));
}
