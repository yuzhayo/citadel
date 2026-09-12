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
