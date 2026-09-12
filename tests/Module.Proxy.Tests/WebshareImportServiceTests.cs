using CitadelBridge;
using Module.Proxy.Features.Sync;
using Module.Proxy.Features.Webshare;
using Module.Proxy.SharedLogic;
using Xunit;

namespace Module.Proxy.Tests;

public sealed class WebshareImportServiceTests
{
    [Fact]
    public void Mapper_UsesBackboneOrDirectAddressWithoutExposingKey()
    {
        var proxy = new WebshareProxy("198.51.100.22", 8080, "user-a", "pass-a", true);

        Assert.True(WebshareImportService.TryMap(proxy, "backbone", out var backbone));
        Assert.Equal("p.webshare.io", backbone.Host);
        Assert.True(WebshareImportService.TryMap(proxy, "direct", out var direct));
        Assert.Equal("198.51.100.22", direct.Host);
        Assert.NotEqual(backbone.Canonical, direct.Canonical);
        Assert.DoesNotContain("pass-a", backbone.Masked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_ValidatesAllDeduplicatedWebshareEndpoints()
    {
        var api = new FakeApi(new WebshareFetchResult(
        [
            new WebshareProxy("198.51.100.22", 8080, "one", "pass", true),
            new WebshareProxy("198.51.100.23", 8081, "two", "pass", true),
            new WebshareProxy("198.51.100.23", 8081, "two", "pass", true),
        ], 0));
        var service = new WebshareImportService(api, new HealthyProbe());

        var result = await service.RunAsync(
            ["key-a", "key-b"],
            new ProxySettings(ParallelTcpChecks: 1, WebshareConnectionMode: "direct"),
            new HashSet<string>(StringComparer.Ordinal),
            null,
            CancellationToken.None);

        Assert.Equal(2, result.Candidates);
        Assert.Equal(2, result.Reachable.Count);
        Assert.Equal(2, api.Calls);
    }

    private sealed class FakeApi(WebshareFetchResult result) : IWebshareApiClient
    {
        private readonly WebshareFetchResult _result = result;
        public int Calls { get; private set; }
        public Task<WebshareFetchResult> FetchAllAsync(string apiKey, string mode, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class HealthyProbe : IProxyReachabilityProbe
    {
        public Task<ProxyProbeResult> ProbeAsync(ProxyEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProxyProbeResult(true, 10, "OK"));
    }
}
