using System.Net;
using System.Net.Sockets;
using CitadelBridge;
using Module.Proxy.Features.Sync;
using Module.Proxy.SharedLogic;
using Xunit;

namespace Module.Proxy.Tests;

public sealed class ProxySyncServiceTests
{
    [Fact]
    public async Task ReachabilityProbe_RejectsTcpPortThatCannotRelayHttps()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        var endpoint = Parse($"http://127.0.0.1:{port}");

        var result = await new ProxyReachabilityProbe().IsReachableAsync(
            endpoint,
            TimeSpan.FromMilliseconds(200),
            CancellationToken.None);
        using var client = await accept.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(result);
    }

    [Fact]
    public async Task RunAsync_IsolatesSourceFailureAndStopsAtTarget()
    {
        var sources = new[]
        {
            new ProxySource("broken", new Uri("https://source.test/broken"), "http"),
            new ProxySource("working", new Uri("https://source.test/working"), "http"),
        };
        var fetcher = new FakeFetcher(source => source.Name == "broken"
            ? throw new HttpRequestException("offline")
            : "one.test:80\ntwo.test:81\nthree.test:82\n");
        var probe = new FakeProbe(true);
        var service = new ProxySyncService(fetcher, probe, sources);

        var result = await service.RunAsync(
            new ProxySettings(TargetUsableEntries: 2, ParallelTcpChecks: 1),
            new HashSet<string>(StringComparer.Ordinal),
            null,
            CancellationToken.None);

        Assert.Equal(2, result.Reachable.Count);
        Assert.Equal(2, result.Tested);
        Assert.NotNull(result.Sources[0].Error);
        Assert.Null(result.Sources[1].Error);
    }

    [Fact]
    public async Task RunAsync_ExcludesBannedBeforeProbe()
    {
        var source = new ProxySource("one", new Uri("https://source.test/list"), "http");
        var banned = Parse("http://one.test:80");
        var probe = new FakeProbe(true);
        var service = new ProxySyncService(
            new FakeFetcher(_ => "one.test:80\ntwo.test:81"),
            probe,
            [source]);

        var result = await service.RunAsync(
            new ProxySettings(TargetUsableEntries: 10, ParallelTcpChecks: 1),
            new HashSet<string>([banned.Canonical], StringComparer.Ordinal),
            null,
            CancellationToken.None);

        Assert.Equal(1, result.Banned);
        Assert.Equal("two.test", Assert.Single(result.Reachable).Host);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task RunAsync_ObservesCancellation()
    {
        var source = new ProxySource("one", new Uri("https://source.test/list"), "http");
        using var cancellation = new CancellationTokenSource();
        var service = new ProxySyncService(
            new FakeFetcher(_ => "one.test:80\ntwo.test:81"),
            new CancellingProbe(cancellation),
            [source]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(
            new ProxySettings(ParallelTcpChecks: 1),
            new HashSet<string>(),
            null,
            cancellation.Token));
    }

    private static ProxyEndpoint Parse(string value)
    {
        Assert.True(ProxyPoolContract.TryParse(value, null, out var endpoint));
        return endpoint;
    }

    private sealed class FakeFetcher(Func<ProxySource, string> fetch) : IProxySourceFetcher
    {
        public Task<string> FetchAsync(ProxySource source, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(fetch(source));
    }

    private sealed class FakeProbe(bool result) : IProxyReachabilityProbe
    {
        public int Calls { get; private set; }
        public Task<bool> IsReachableAsync(ProxyEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class CancellingProbe(CancellationTokenSource cancellation) : IProxyReachabilityProbe
    {
        public Task<bool> IsReachableAsync(ProxyEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }
    }
}
