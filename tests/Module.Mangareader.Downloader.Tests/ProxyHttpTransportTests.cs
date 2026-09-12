using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using CitadelBridge;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Downloader.Tests;

public sealed class ProxyHttpTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mangareader-http-proxy-tests", Guid.NewGuid().ToString("N"));
    private string PoolPath => Path.Combine(_root, "proxy.txt");

    [Fact]
    public async Task DirectMode_PreservesInjectedClient()
    {
        var handler = new RecordingHandler();
        using var direct = new HttpClient(handler);
        var adapter = new ProxyPoolAdapter("Downloader", PoolPath);
        using var transport = new ProxyHttpTransport(adapter);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/title");

        using var response = await transport.SendAsync(
            "provider", direct, request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ProxyModeWithEmptyPool_FailsBeforeDirectClient()
    {
        var handler = new RecordingHandler();
        using var direct = new HttpClient(handler);
        var adapter = new ProxyPoolAdapter("Downloader", PoolPath) { Enabled = true };
        using var transport = new ProxyHttpTransport(adapter);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/title");

        var error = await Assert.ThrowsAsync<ProxyPoolException>(() => transport.SendAsync(
            "provider", direct, request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None));

        Assert.Equal("PROXY_POOL_EMPTY", error.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ProxyMode_SendsThroughSelectedHttpProxy()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        WritePool($"http://127.0.0.1:{port}");
        var server = ReplyOnceAsync(listener);

        var handler = new RecordingHandler();
        using var direct = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var adapter = new ProxyPoolAdapter("Downloader", PoolPath) { Enabled = true };
        using var transport = new ProxyHttpTransport(adapter);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://example.test/title");

        using var response = await transport.SendAsync(
            "provider", direct, request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        var requestLine = await server;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("GET http://example.test/title ", requestLine, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CapturedProxyMode_RemainsProxyAfterToggleTurnsOff()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        WritePool($"http://127.0.0.1:{port}");
        var server = ReplyOnceAsync(listener);

        var handler = new RecordingHandler();
        using var direct = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var adapter = new ProxyPoolAdapter("Downloader", PoolPath) { Enabled = true };
        using var transport = new ProxyHttpTransport(adapter);
        var capturedProxyMode = transport.IsProxyMode;
        adapter.Enabled = false;
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://example.test/page");

        using var response = await transport.SendAsync(
            "downloader-pages",
            direct,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            capturedProxyMode,
            CancellationToken.None);
        var requestLine = await server;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("GET http://example.test/page ", requestLine, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ProxyFailure_IsNotReplayedDirectly()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var unusedPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        WritePool($"http://127.0.0.1:{unusedPort}");

        var handler = new RecordingHandler();
        using var direct = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var adapter = new ProxyPoolAdapter("Downloader", PoolPath) { Enabled = true };
        using var transport = new ProxyHttpTransport(adapter);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://example.test/title");

        await Assert.ThrowsAnyAsync<Exception>(() => transport.SendAsync(
            "provider", direct, request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None));

        Assert.Equal(0, handler.Calls);
        Assert.Equal("PROXY_POOL_EXHAUSTED",
            Assert.Throws<ProxyPoolException>(() => adapter.Acquire(ProxyTarget.Http)).Code);
    }

    [Fact]
    public async Task ProxyGet_RetriesThroughNextProxyWithoutUsingDirectClient()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var livePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        WritePool("http://127.0.0.1:1", $"http://127.0.0.1:{livePort}");
        var server = ReplyOnceAsync(listener);

        var handler = new RecordingHandler();
        using var direct = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var adapter = new ProxyPoolAdapter("Downloader", PoolPath) { Enabled = true };
        using var transport = new ProxyHttpTransport(adapter);

        var bytes = await transport.GetByteArrayAsync(
            "cover", direct, "http://example.test/cover.jpg", CancellationToken.None);
        var requestLine = await server;

        Assert.Equal("OK", Encoding.ASCII.GetString(bytes));
        Assert.StartsWith("GET http://example.test/cover.jpg ", requestLine, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
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

    private static async Task<string> ReplyOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync() ?? string.Empty;
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
        }

        var bytes = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        await stream.WriteAsync(bytes);
        return requestLine;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("OK"),
            });
        }
    }
}
