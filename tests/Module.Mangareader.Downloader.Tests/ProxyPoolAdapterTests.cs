using System.IO;
using CitadelBridge;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Downloader.Tests;

public sealed class ProxyPoolAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mangareader-proxy-tests", Guid.NewGuid().ToString("N"));
    private string PoolPath => Path.Combine(_root, "proxy.txt");

    [Fact]
    public void DirectMode_DoesNotReadOrRequirePool()
    {
        var adapter = new ProxyPoolAdapter("Downloader test", PoolPath);

        Assert.Null(adapter.Acquire(ProxyTarget.Browser));
        Assert.Null(adapter.Acquire(ProxyTarget.Http));
    }

    [Fact]
    public void DownloaderAndCatalog_OwnIndependentCursorAndQuarantine()
    {
        WritePool("http://one.test:80", "http://two.test:81");
        var downloader = new ProxyPoolAdapter("Downloader", PoolPath) { Enabled = true };
        var catalog = new ProxyPoolAdapter("Catalog", PoolPath) { Enabled = true };

        var failed = Assert.IsType<ProxyLease>(downloader.Acquire(ProxyTarget.Http));
        downloader.ReportFailure(failed);

        Assert.Equal(failed.Endpoint.Canonical,
            Assert.IsType<ProxyLease>(catalog.Acquire(ProxyTarget.Http)).Endpoint.Canonical);
        Assert.NotEqual(failed.Endpoint.Canonical,
            Assert.IsType<ProxyLease>(downloader.Acquire(ProxyTarget.Http)).Endpoint.Canonical);
    }

    [Fact]
    public void TargetCompatibility_IsEnforcedWithoutDirectFallback()
    {
        WritePool("socks5://browser.test:1080");
        var adapter = new ProxyPoolAdapter("Catalog", PoolPath) { Enabled = true };

        Assert.Equal("socks5", adapter.Acquire(ProxyTarget.Browser)!.Endpoint.Scheme);
        var error = Assert.Throws<ProxyPoolException>(() => adapter.Acquire(ProxyTarget.Http));
        Assert.Equal("PROXY_POOL_INCOMPATIBLE", error.Code);
    }

    [Fact]
    public void EmptyPool_FailsClosed()
    {
        var adapter = new ProxyPoolAdapter("Downloader", PoolPath) { Enabled = true };

        var error = Assert.Throws<ProxyPoolException>(() => adapter.Acquire(ProxyTarget.Http));

        Assert.Equal("PROXY_POOL_EMPTY", error.Code);
    }

    [Fact]
    public void ProxyMode_PrefersHealthyLowestLatencyBeforeRoundRobin()
    {
        WritePool("http://slow.test:80", "http://fast.test:81", "http://failed.test:82");
        var endpoints = ProxyPoolContract.ReadSnapshot(PoolPath).Endpoints;
        var records = endpoints.Select(endpoint => new ProxyHealthRecord(
            ProxyPoolHealthContract.EndpointKey(endpoint),
            endpoint.Host == "failed.test" ? ProxyHealthState.Unreachable : ProxyHealthState.Healthy,
            endpoint.Host == "fast.test" ? 20 : endpoint.Host == "slow.test" ? 200 : null,
            DateTimeOffset.UtcNow,
            endpoint.Host == "failed.test" ? "Timeout" : "OK"));
        File.WriteAllText(ProxyPoolHealthContract.HealthPathFor(PoolPath), ProxyPoolHealthContract.Serialize(records));

        var adapter = new ProxyPoolAdapter("Downloader", PoolPath) { Enabled = true };

        var fast = adapter.Acquire(ProxyTarget.Http)!;
        Assert.Equal("fast.test", fast.Endpoint.Host);
        adapter.ReportFailure(fast);
        Assert.Equal("slow.test", adapter.Acquire(ProxyTarget.Http)!.Endpoint.Host);
    }

    [Fact]
    public async Task WebshareOriginsBalanceReservationsAcrossAccounts()
    {
        WritePool(
            "http://a-one.test:80", "http://a-two.test:81",
            "http://b-one.test:82", "http://b-two.test:83");
        var endpoints = ProxyPoolContract.ReadSnapshot(PoolPath).Endpoints;
        File.WriteAllText(
            ProxyPoolOriginContract.OriginsPathFor(PoolPath),
            ProxyPoolOriginContract.Serialize(
                endpoints.Select(endpoint =>
                    (endpoint, new ProxyPoolOrigin("webshare", endpoint.Host.StartsWith("a-", StringComparison.Ordinal)
                        ? "ws-a"
                        : "ws-b"))),
                endpoints));
        var adapter = new ProxyPoolAdapter("Downloader", PoolPath) { Enabled = true };

        using var first = await adapter.ReserveAsync("one", adapter.AvailableCandidates(ProxyTarget.Http), CancellationToken.None);
        using var second = await adapter.ReserveAsync("two", adapter.AvailableCandidates(ProxyTarget.Http), CancellationToken.None);
        using var third = await adapter.ReserveAsync("three", adapter.AvailableCandidates(ProxyTarget.Http), CancellationToken.None);
        using var fourth = await adapter.ReserveAsync("four", adapter.AvailableCandidates(ProxyTarget.Http), CancellationToken.None);

        Assert.Equal(2, new[] { first, second, third, fourth }.Count(item => item.AccountId == "ws-a"));
        Assert.Equal(2, new[] { first, second, third, fourth }.Count(item => item.AccountId == "ws-b"));
    }

    [Fact]
    public async Task SharedRegistryBalancesAccountsAcrossDownloaderAndCatalog()
    {
        WritePool(
            "http://a-one.test:80", "http://a-two.test:81",
            "http://b-one.test:82", "http://b-two.test:83");
        var endpoints = ProxyPoolContract.ReadSnapshot(PoolPath).Endpoints;
        File.WriteAllText(
            ProxyPoolOriginContract.OriginsPathFor(PoolPath),
            ProxyPoolOriginContract.Serialize(
                endpoints.Select(endpoint =>
                    (endpoint, new ProxyPoolOrigin("webshare", endpoint.Host.StartsWith("a-", StringComparison.Ordinal)
                        ? "ws-a"
                        : "ws-b"))),
                endpoints));

        var reservations = new ProxyLeaseRegistry();
        var downloader = new ProxyPoolAdapter("Downloader", reservations, PoolPath) { Enabled = true };
        var catalog = new ProxyPoolAdapter("Catalog", reservations, PoolPath) { Enabled = true };

        using var first = await downloader.ReserveAsync(
            "download-page", downloader.AvailableCandidates(ProxyTarget.Http), CancellationToken.None);
        using var second = await catalog.ReserveAsync(
            "catalog-cover", catalog.AvailableCandidates(ProxyTarget.Http), CancellationToken.None);
        using var third = await downloader.ReserveAsync(
            "download-manifest", downloader.AvailableCandidates(ProxyTarget.Http), CancellationToken.None);
        using var fourth = await catalog.ReserveAsync(
            "catalog-browser", catalog.AvailableCandidates(ProxyTarget.Http), CancellationToken.None);

        var leases = new[] { first, second, third, fourth };
        Assert.Equal(4, leases.Select(item => item.Lease.Endpoint.Canonical).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, leases.Count(item => item.AccountId == "ws-a"));
        Assert.Equal(2, leases.Count(item => item.AccountId == "ws-b"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void WritePool(params string[] lines)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllLines(PoolPath, lines);
    }
}
