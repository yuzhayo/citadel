using System.IO;
using System.Text.Json.Nodes;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Features.Downloader.Sources.Comix;
using Module.Mangareader.ShareLogic;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Downloader.Tests;

public sealed class QueueManifestSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Citadel.QueueManifest.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FreshManifestUsesOwnedProfileWhileDownloaderIsInactive()
    {
        Directory.CreateDirectory(_root);
        var poolPath = Path.Combine(_root, "pool.txt");
        File.WriteAllText(poolPath, "http://proxy.test:80");
        var pool = new ProxyPoolAdapter("test", poolPath) { Enabled = true };
        using var downloader = new DownloaderPyHostClient(_root);
        var opened = 0;
        var apiCalls = 0;
        var id = Guid.NewGuid().ToString("N");
        var session = new QueueManifestSession(_root, id, pool, new ComixSource(downloader),
            new QueueSharedSessionAdapter(downloader, _ => throw new Exception("unexpected shared fallback")), _ => { },
            lease => new DownloaderPyHostClient(_root, pool, "queue-" + id, lease,
                (payload, _) =>
                {
                    Assert.Equal("queue-" + id, payload["profile_identity"]!.GetValue<string>());
                    Assert.NotNull(payload["proxy"]);
                    opened++;
                    return Task.FromResult(new JsonObject { ["session"] = "owned" });
                },
                (command, payload, _) =>
                {
                    Assert.Equal("downloader.api", command);
                    apiCalls++;
                    return Task.FromResult(JsonNode.Parse("""{"status":200,"is_json":true,"json":{"pages":{"items":[{"url":"https://images.test/1.jpg"}]}}}""")!.AsObject());
                }));
        Assert.Equal(0, opened);
        var manifest = await session.GetAsync(Chapter(), CancellationToken.None);
        Assert.Single(manifest.Pages);
        Assert.Equal(1, opened);
        Assert.Equal(1, apiCalls);
        Assert.False(downloader.HasSession);
        Assert.Empty(pool.Reservations.Snapshot());

        var store = new QueueManifestStore(Path.Combine(_root, "manifest.json"));
        store.Write(manifest);
        Assert.Equal(manifest.ManifestHash, store.Read(Chapter())!.ManifestHash);
        Assert.Null(store.Read(Chapter() with { ChapterId = "different" }));
        File.WriteAllText(Path.Combine(_root, "manifest.json"), File.ReadAllText(Path.Combine(_root, "manifest.json")).Replace("images.test", "altered.test"));
        Assert.Null(store.Read(Chapter()));
        File.WriteAllText(Path.Combine(_root, "manifest.json"), "{\"Schema\":1,\"Hash\":\"invalid\",\"Manifest\":null}");
        Assert.Null(store.Read(Chapter()));
    }

    private static RemoteChapterIdentity Chapter() => new("comix",
        new RemoteTitleIdentity("comix", "title", "hid", ""), "chapter", "1", new RemoteGroupIdentity("comix", "group"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
