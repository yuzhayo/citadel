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
        var connectTimeouts = new List<int>();
        using var client = new DownloaderPyHostClient(
            Path.Combine(_root, "downloader"),
            adapter,
            (payload, _) =>
            {
                connectTimeouts.Add(payload["connect_timeout_ms"]!.GetValue<int>());
                return OpenOnThirdAttempt(payload, attempts);
            });

        var session = await client.EnsureSessionAsync(
            "comix", "https://comix.ws/browse", true, CancellationToken.None);

        Assert.Equal("s3", session);
        AssertThreeDistinctAttempts(attempts);
        Assert.All(connectTimeouts, value => Assert.Equal(5000, value));
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

    [Fact]
    public async Task Downloader_ExplicitRotationRequiresADifferentVerifiedEgressIp()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "rotate-proxy.txt");
        File.WriteAllLines(path,
        [
            "http://user-one:secret-one@shared.test:8000",
            "http://user-two:secret-two@shared.test:8000",
            "http://user-three:secret-three@shared.test:8000",
        ]);
        var adapter = new ProxyPoolAdapter("Downloader", path) { Enabled = true };
        var attempts = new List<string>();
        using var client = new DownloaderPyHostClient(
            Path.Combine(_root, "rotate-downloader"),
            adapter,
            (payload, _) =>
            {
                var proxy = Assert.IsType<JsonObject>(payload["proxy"]);
                attempts.Add(string.Join('|',
                    proxy["server"]!.GetValue<string>(),
                    proxy["username"]!.GetValue<string>(),
                    proxy["password"]!.GetValue<string>()));
                return Task.FromResult(new JsonObject { ["session"] = "s" + attempts.Count });
            },
            egressProbe: _ => Task.FromResult(
                attempts.Count < 3 ? "198.51.100.10" : "198.51.100.20"));

        await client.EnsureSessionAsync(
            "comix", "https://comix.ws/browse", true, CancellationToken.None);
        var firstDisplay = client.ActiveProxyDisplay;

        Assert.Equal("http://user-one:secret-one@shared.test:8000", firstDisplay);
        Assert.Equal("198.51.100.10", client.ActiveEgressIp);
        var rotation = await client.RotateProxyWithVerifiedEgressAsync(CancellationToken.None);

        Assert.True(rotation.Changed);
        Assert.Equal(3, attempts.Count);
        Assert.NotEqual(attempts[0], attempts[1]);
        Assert.NotEqual(attempts[1], attempts[2]);
        Assert.NotEqual(firstDisplay, client.ActiveProxyDisplay);
        Assert.Equal("198.51.100.20", client.ActiveEgressIp);
    }

    [Theory]
    [InlineData("SITE_CHALLENGE_TIMEOUT")]
    [InlineData("API_CLIENT_UNAVAILABLE")]
    public async Task Downloader_ApiReadinessFailureRotatesProxyAndRetriesTheRequestOnce(
        string failureCode)
    {
        var adapter = CreatePool("Downloader");
        var opened = new List<string>();
        var apiCalls = 0;
        using var client = new DownloaderPyHostClient(
            Path.Combine(_root, "challenge-downloader"),
            adapter,
            (payload, _) =>
            {
                var proxy = Assert.IsType<JsonObject>(payload["proxy"]);
                opened.Add(proxy["server"]!.GetValue<string>());
                return Task.FromResult(new JsonObject { ["session"] = "s" + opened.Count });
            },
            (command, _, _) =>
            {
                Assert.Equal("downloader.api", command);
                apiCalls++;
                if (apiCalls == 1)
                    throw new PyHostException(failureCode, "provider page is not ready");
                return Task.FromResult(new JsonObject { ["status"] = 200 });
            });

        await client.EnsureSessionAsync(
            "comix", "https://comix.ws/browse", true, CancellationToken.None);
        var response = await client.ApiAsync(
            "https://comix.ws/api/v1/manga/example", CancellationToken.None);

        Assert.Equal(200, response["status"]!.GetValue<int>());
        Assert.Equal(2, apiCalls);
        Assert.Equal(2, opened.Count);
        Assert.NotEqual(opened[0], opened[1]);
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
