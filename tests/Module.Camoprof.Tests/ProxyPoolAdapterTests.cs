using System.IO;
using CitadelBridge;
using Module.Camoprof.SharedLogic;
using Xunit;

namespace Module.Camoprof.Tests;

public sealed class ProxyPoolAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "camoprof-proxy-tests", Guid.NewGuid().ToString("N"));
    private string PoolPath => Path.Combine(_root, "proxy.txt");

    [Fact]
    public void DirectMode_DoesNotRequirePool()
    {
        var adapter = new ProxyPoolAdapter(PoolPath);
        Assert.Null(adapter.Acquire());
    }

    [Fact]
    public void ProxyMode_RoundRobinsAndQuarantinesFailures()
    {
        WritePool("http://one.test:80", "socks4://skip.test:1080", "socks5://two.test:1080");
        var adapter = new ProxyPoolAdapter(PoolPath) { Enabled = true };

        var first = Assert.IsType<CamoProxyLease>(adapter.Acquire());
        adapter.ReportFailure(first);
        var second = Assert.IsType<CamoProxyLease>(adapter.Acquire());

        Assert.NotEqual(first.Endpoint.Canonical, second.Endpoint.Canonical);
        Assert.NotEqual("socks4", second.Endpoint.Scheme);
    }

    [Fact]
    public void NewSnapshot_ResetsQuarantine()
    {
        WritePool("http://one.test:80");
        var adapter = new ProxyPoolAdapter(PoolPath) { Enabled = true };
        var lease = Assert.IsType<CamoProxyLease>(adapter.Acquire());
        adapter.ReportFailure(lease);
        Assert.Throws<ProxyPoolException>(() => adapter.Acquire());

        Thread.Sleep(20);
        WritePool("http://one.test:80", "http://two.test:81");

        Assert.NotNull(adapter.Acquire());
    }

    [Fact]
    public void EmptyPool_FailsClosed()
    {
        var adapter = new ProxyPoolAdapter(PoolPath) { Enabled = true };
        var error = Assert.Throws<ProxyPoolException>(() => adapter.Acquire());
        Assert.Equal("PROXY_POOL_EMPTY", error.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private void WritePool(params string[] lines)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllLines(PoolPath, lines);
        File.SetLastWriteTimeUtc(PoolPath, DateTime.UtcNow.AddSeconds(1));
    }
}
