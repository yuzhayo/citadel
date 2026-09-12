using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.ShareLogic;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Downloader.Tests;

public sealed class QueueIndependentSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Citadel.QueueIndependent.Tests", Guid.NewGuid().ToString("N"));
    private ProxyPoolAdapter Pool(params string[] endpoints)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "pool.txt");
        File.WriteAllLines(path, endpoints);
        return new ProxyPoolAdapter("test", path) { Enabled = true };
    }
    private static RemotePage Page(int ordinal = 0) => new(ordinal, "page", "http://unreachable.test/page", null, null);
    private static PageFetchResult Failure(PageFetchOutcome outcome) => new(outcome, null, 0, null, "", "fixture", false);

    [Fact]
    public async Task ExhaustsAllCandidatesThenLatchesStopWithoutOpeningShared()
    {
        var pool = Pool("http://one.test:80", "http://two.test:80", "http://three.test:80", "http://four.test:80");
        using var browser = new DownloaderPyHostClient(_root);
        var stopped = false;
        var shared = new QueueSharedSessionAdapter(browser, _ => stopped = true);
        using var independent = new QueueIndependentSession("job", "comix", pool, shared, _ => { });
        var tried = new HashSet<string>();
        await Assert.ThrowsAsync<QueueSessionUnavailableException>(() => independent.FetchAsync(Page(),
            (lease, _) => { Assert.True(tried.Add(lease.Endpoint.Server)); return Task.FromResult(Failure(PageFetchOutcome.NetworkFailed)); },
            _ => throw new Exception("must not dispatch shared"), CancellationToken.None));
        Assert.Equal(4, tried.Count);
        Assert.True(stopped);
        Assert.False(browser.HasSession);
        Assert.Empty(pool.Reservations.Snapshot());
    }

    [Theory]
    [InlineData(PageFetchOutcome.Rejected)]
    [InlineData(PageFetchOutcome.Challenge)]
    [InlineData(PageFetchOutcome.Throttled)]
    [InlineData(PageFetchOutcome.NotFound)]
    public async Task ProviderResponseDoesNotRotateOrRepeatInRecovery(PageFetchOutcome outcome)
    {
        var pool = Pool("http://one.test:80", "http://two.test:80");
        using var browser = new DownloaderPyHostClient(_root);
        using var independent = new QueueIndependentSession("job", "comix", pool,
            new QueueSharedSessionAdapter(browser, _ => throw new Exception("must not stop for exhaustion")), _ => { });
        var calls = 0;
        for (var pass = 0; pass < 2; pass++)
            Assert.Equal(outcome, (await independent.FetchAsync(Page(),
                (_, _) => { calls++; return Task.FromResult(Failure(outcome)); },
                _ => throw new Exception("must not fallback"), CancellationToken.None)).Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SharedFallbackRunsOnceAcrossRecoveryAndIsNotClosedByQueue()
    {
        var pool = Pool("http://one.test:80", "http://two.test:80");
        using var browser = new DownloaderPyHostClient(_root, pool,
            (_, _) => Task.FromResult(new System.Text.Json.Nodes.JsonObject { ["session"] = "shared" }));
        await browser.EnsureSessionAsync("comix", "https://comix.ws/browse", true, CancellationToken.None);
        using var independent = new QueueIndependentSession("job", "comix", pool,
            new QueueSharedSessionAdapter(browser, _ => throw new Exception("unexpected stop")), _ => { });
        var nativeCalls = 0;
        var fallbackCalls = 0;
        for (var pass = 0; pass < 2; pass++)
            await independent.FetchAsync(Page(),
                (_, _) => { nativeCalls++; return Task.FromResult(Failure(PageFetchOutcome.NetworkFailed)); },
                _ => { fallbackCalls++; return Task.FromResult(Failure(PageFetchOutcome.NetworkFailed)); }, CancellationToken.None);
        Assert.Equal(1, nativeCalls); // shared browser's reserved endpoint excluded
        Assert.Equal(1, fallbackCalls);
        Assert.True(browser.HasSessionFor("comix"));
        Assert.Single(pool.Reservations.Snapshot());
        browser.AbortSession();
        Assert.Empty(pool.Reservations.Snapshot());
    }

    [Fact]
    public async Task HeadersThenStalledBodyTimesOutAndReleasesReservation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var pool = Pool($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        using var browser = new DownloaderPyHostClient(_root);
        using var independent = new QueueIndependentSession("job", "comix", pool,
            new QueueSharedSessionAdapter(browser, _ => { }), _ => { });
        using var transport = new PageTransport(browser, _root, independent.Transport, independent);
        using var stopServer = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(stopServer.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stopServer.Token))) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 999\r\n\r\n"), stopServer.Token);
            headersSent.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, stopServer.Token); } catch (OperationCanceledException) { }
        });
        var watch = Stopwatch.StartNew();
        var fetch = transport.FetchIndependentAsync(Page(), "jobs/test/page.raw", null, null, CancellationToken.None);
        await headersSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(pool.Reservations.Snapshot());
        await Assert.ThrowsAsync<QueueSessionUnavailableException>(() => fetch);
        Assert.InRange(watch.Elapsed.TotalSeconds, 2.5, 6);
        Assert.Empty(pool.Reservations.Snapshot());
        Assert.False(File.Exists(Path.Combine(_root, "jobs/test/page.raw.part")));
        stopServer.Cancel();
        await server;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
