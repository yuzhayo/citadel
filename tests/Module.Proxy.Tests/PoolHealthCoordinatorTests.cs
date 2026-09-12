using CitadelBridge;
using Module.Proxy.Features.Pool;
using Module.Proxy.Features.Sync;
using Module.Proxy.SharedLogic;
using Xunit;

namespace Module.Proxy.Tests;

public sealed class PoolHealthCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "proxy-health-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Refresh_ChecksCommittedPoolOnlyAndPersistsDiagnostics()
    {
        var store = new ProxyPoolStore(_root);
        var fast = Parse("http://fast.test:80");
        var failed = Parse("http://failed.test:81");
        store.Commit([fast, failed]);
        var settings = new ProxySettingsStore(Path.Combine(_root, "settings.json"));
        settings.Save(new ProxySettings(ParallelTcpChecks: 1));
        using var coordinator = new PoolHealthCoordinator(store, settings, new FakeProbe());

        await coordinator.RefreshAsync();

        var health = store.LoadHealth().Entries;
        Assert.Equal(2, health.Count);
        Assert.Equal(ProxyHealthState.Healthy, health[ProxyPoolHealthContract.EndpointKey(fast)].State);
        Assert.Equal(12, health[ProxyPoolHealthContract.EndpointKey(fast)].LatencyMilliseconds);
        Assert.Equal(ProxyHealthState.Unreachable, health[ProxyPoolHealthContract.EndpointKey(failed)].State);
        Assert.Equal(new[] { fast.Canonical, failed.Canonical }.Order(), File.ReadAllLines(store.ActivePath).Order());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ProxyEndpoint Parse(string value)
    {
        Assert.True(ProxyPoolContract.TryParse(value, null, out var endpoint));
        return endpoint;
    }

    private sealed class FakeProbe : IProxyReachabilityProbe
    {
        public Task<ProxyProbeResult> ProbeAsync(ProxyEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(endpoint.Host == "fast.test"
                ? new ProxyProbeResult(true, 12, "OK")
                : new ProxyProbeResult(false, null, "Timeout"));
    }
}
