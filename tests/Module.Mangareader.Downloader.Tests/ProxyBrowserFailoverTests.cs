using System.IO;
using System.Text.Json.Nodes;
using CitadelBridge;
using Module.Mangareader.Features.Catalog.Runtime;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Downloader.Tests;

public sealed class ProxyBrowserFailoverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mangareader-browser-proxy-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Downloader_RetriesDifferentProxyUntilOpenSucceeds()
    {
        var adapter = CreatePool("Downloader");
        var attempts = new List<string>();
        using var client = new DownloaderPyHostClient(
            Path.Combine(_root, "downloader"),
            adapter,
            (payload, _) => OpenOnThirdAttempt(payload, attempts));

        var session = await client.EnsureSessionAsync(
            "comix", "https://comix.ws/browse", true, CancellationToken.None);

        Assert.Equal("s3", session);
        AssertThreeDistinctAttempts(attempts);
    }

    [Fact]
    public async Task Catalog_RetriesDifferentProxyUntilOpenSucceeds()
    {
        var adapter = CreatePool("Catalog");
        var attempts = new List<string>();
        using var client = new CatalogBrowserClient(
            Path.Combine(_root, "catalog"),
            adapter,
            (payload, _) => OpenOnThirdAttempt(payload, attempts));

        var session = await client.EnsureSessionAsync(
            "comix", "https://comix.ws/browse", true, CancellationToken.None);

        Assert.Equal("s3", session);
        AssertThreeDistinctAttempts(attempts);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private ProxyPoolAdapter CreatePool(string lane)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "proxy.txt");
        File.WriteAllLines(path,
        [
            "http://one.test:8001",
            "http://two.test:8002",
            "http://three.test:8003",
        ]);
        return new ProxyPoolAdapter(lane, path) { Enabled = true };
    }

    private static Task<JsonObject> OpenOnThirdAttempt(
        JsonObject payload,
        ICollection<string> attempts)
    {
        var proxy = Assert.IsType<JsonObject>(payload["proxy"]);
        attempts.Add(proxy["server"]!.GetValue<string>());
        if (attempts.Count < 3)
        {
            throw new PyHostException("BROWSER_LAUNCH", "proxy timeout");
        }
        return Task.FromResult(new JsonObject { ["session"] = "s3" });
    }

    private static void AssertThreeDistinctAttempts(IReadOnlyCollection<string> attempts)
    {
        Assert.Equal(3, attempts.Count);
        Assert.Equal(3, attempts.Distinct(StringComparer.Ordinal).Count());
    }
}
