using System.IO;
using System.Text.Json.Nodes;
using CitadelBridge;
using Module.Camoprof.SharedLogic;
using Xunit;

namespace Module.Camoprof.Tests;

public sealed class BrowserSessionCoordinatorProxyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "camoprof-session-proxy-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OpenAsync_ForwardsLeaseToNewSession()
    {
        var path = WritePool("http://user:secret@proxy.test:8080");
        var adapter = new ProxyPoolAdapter(path) { Enabled = true };
        ProxyLaunchOptions? captured = null;
        using var coordinator = new BrowserSessionCoordinator(
            adapter,
            (profile, url, headless, proxy, token) =>
            {
                captured = proxy;
                return Task.FromResult(new JsonObject { ["session"] = "s1" });
            });

        await coordinator.OpenAsync("profile");

        Assert.NotNull(captured);
        Assert.Equal("http://proxy.test:8080", captured!.Server);
        Assert.Equal("user", captured.Username);
        Assert.Equal("secret", captured.Password);
    }

    [Fact]
    public async Task FailedLaunch_QuarantinesLease()
    {
        var path = WritePool("http://proxy.test:8080");
        var adapter = new ProxyPoolAdapter(path) { Enabled = true };
        using var coordinator = new BrowserSessionCoordinator(
            adapter,
            (_, _, _, _, _) => throw new PyHostException("BROWSER_LAUNCH", "failed"));

        await Assert.ThrowsAsync<ProxyPoolException>(() => coordinator.OpenAsync("profile"));
        Assert.Equal("PROXY_POOL_EXHAUSTED", Assert.Throws<ProxyPoolException>(() => adapter.Acquire()).Code);
    }

    [Fact]
    public async Task FailedLaunch_RetriesWithDifferentProxy()
    {
        var path = WritePool(
            "http://one.test:8001",
            "http://two.test:8002",
            "http://three.test:8003");
        var adapter = new ProxyPoolAdapter(path) { Enabled = true };
        var attempts = new List<string>();
        using var coordinator = new BrowserSessionCoordinator(
            adapter,
            (_, _, _, proxy, _) =>
            {
                attempts.Add(Assert.IsType<ProxyLaunchOptions>(proxy).Server);
                if (attempts.Count < 3)
                {
                    throw new PyHostException("BROWSER_LAUNCH", "proxy timeout");
                }
                return Task.FromResult(new JsonObject { ["session"] = "s3" });
            });

        await coordinator.OpenAsync("profile");

        Assert.Equal(3, attempts.Count);
        Assert.Equal(3, attempts.Distinct(StringComparer.Ordinal).Count());
        Assert.True(coordinator.IsOpen("profile"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string WritePool(params string[] lines)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "proxy.txt");
        File.WriteAllLines(path, lines);
        return path;
    }
}
