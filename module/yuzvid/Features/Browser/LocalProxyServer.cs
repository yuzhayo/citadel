using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CitadelBridge;

namespace Module.Yuzvid.Features.Browser;

/// <summary>
/// Local HTTP proxy server. WebView2 always connects to localhost:PORT.
/// This server routes traffic either direct or through an upstream proxy.
/// Supports both HTTP and SOCKS5 upstream proxies.
/// </summary>
internal sealed class LocalProxyServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SecureDnsResolver _dnsResolver = new();
    private Task? _acceptLoop;
    private ProxyEndpoint? _upstream;
    private int _dnsMode;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public bool IsRunning => _acceptLoop is { IsCompleted: false };

    public ProxyEndpoint? Upstream
    {
        get => _upstream;
        set => _upstream = value;
    }

    public bool ProxyEnabled => _upstream is not null;

    public BrowserDnsMode DnsMode
    {
        get => (BrowserDnsMode)Volatile.Read(ref _dnsMode);
        set => Volatile.Write(ref _dnsMode, (int)value);
    }

    public LocalProxyServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
    }

    public void Start() => _acceptLoop = AcceptLoopAsync();

    private async Task AcceptLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                _ = HandleClientAsync(client, token);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var firstLine = await ReadLineAsync(stream, token);
                if (string.IsNullOrEmpty(firstLine)) return;

                var parts = firstLine.Split(' ', 3);
                if (parts.Length < 3) return;

                var method = parts[0].ToUpperInvariant();
                var target = parts[1];

                if (method == "CONNECT")
                {
                    await HandleConnectAsync(stream, target, token);
                }
                else
                {
                    await HandleHttpAsync(stream, firstLine, token);
                }
            }
        }
        catch { }
    }

    // ─── CONNECT (HTTPS) ──────────────────────────────────────────────────

    private static bool IsSocksScheme(ProxyEndpoint proxy) =>
        proxy.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase);

    private async Task HandleConnectAsync(NetworkStream clientStream, string target, CancellationToken token)
    {
        var upstream = _upstream;
        var hostPort = target.Split(':');
        var host = hostPort[0];
        var port = hostPort.Length > 1 ? int.Parse(hostPort[1]) : 443;

        TcpClient? remote = null;
        try
        {
            if (upstream is not null)
            {
                if (IsSocksScheme(upstream))
                {
                    remote = await Socks5ConnectAsync(upstream, host, port, token);
                }
                else
                {
                    remote = await HttpProxyConnectAsync(upstream, target, token);
                }
            }
            else
            {
                remote = await ConnectDirectAsync(host, port, token);
            }

            // Consume remaining browser headers (Host: ..., other headers, \r\n)
            // so they don't get relayed into the tunnel and corrupt TLS
            string? headerLine;
            while ((headerLine = await ReadLineAsync(clientStream, token)) != null && headerLine.Length > 0) { }

            // Tell browser tunnel is established
            var okResp = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
            await clientStream.WriteAsync(okResp, token);

            // Relay bidirectional
            using var remoteStream = remote.GetStream();
            await RelayAsync(clientStream, remoteStream, token);
        }
        catch
        {
            try
            {
                var failResp = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\n\r\n");
                await clientStream.WriteAsync(failResp, token);
            }
            catch { }
        }
        finally
        {
            remote?.Dispose();
        }
    }

    // ─── HTTP (non-CONNECT) ───────────────────────────────────────────────

    private async Task HandleHttpAsync(NetworkStream clientStream, string firstLine, CancellationToken token)
    {
        var upstream = _upstream;
        var uri = new Uri(firstLine.Split(' ')[1]);
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 80;

        TcpClient? remote = null;
        try
        {
            if (upstream is not null)
            {
                if (IsSocksScheme(upstream))
                {
                    remote = await Socks5ConnectAsync(upstream, host, port, token);
                    // Forward request with relative path
                    var pathLine = firstLine.Replace(uri.AbsoluteUri, uri.PathAndQuery);
                    var reqBytes = Encoding.ASCII.GetBytes(pathLine + "\r\n");
                    await remote.GetStream().WriteAsync(reqBytes, token);
                }
                else
                {
                    remote = await HttpProxyConnectAsync(upstream, firstLine.Split(' ')[1], token);
                    // Forward request with absolute URL
                    var reqBytes = Encoding.ASCII.GetBytes(firstLine + "\r\n");
                    await remote.GetStream().WriteAsync(reqBytes, token);
                }
            }
            else
            {
                // Direct
                remote = await ConnectDirectAsync(host, port, token);
                var pathLine = firstLine.Replace(uri.AbsoluteUri, uri.PathAndQuery);
                var reqBytes = Encoding.ASCII.GetBytes(pathLine + "\r\n");
                await remote.GetStream().WriteAsync(reqBytes, token);
            }

            using var remoteStream = remote.GetStream();
            await RelayAsync(clientStream, remoteStream, token);
        }
        catch { }
        finally
        {
            remote?.Dispose();
        }
    }

    // ─── SOCKS5 ───────────────────────────────────────────────────────────

    private async Task<TcpClient> ConnectDirectAsync(string host, int port, CancellationToken token)
    {
        Exception? lastError = null;
        foreach (var address in await _dnsResolver.ResolveAsync(host, DnsMode, token))
        {
            var client = new TcpClient(address.AddressFamily);
            try
            {
                await client.ConnectAsync(address, port, token);
                return client;
            }
            catch (Exception exception)
            {
                lastError = exception;
                client.Dispose();
            }
        }

        throw new IOException($"Unable to connect to {host}:{port}.", lastError);
    }

    private static async Task<TcpClient> Socks5ConnectAsync(
        ProxyEndpoint proxy, string targetHost, int targetPort, CancellationToken token)
    {
        var client = new TcpClient();
        await client.ConnectAsync(proxy.Host, proxy.Port, token);
        var stream = client.GetStream();

        // Greeting: version 5, 1 auth method
        var greeting = new byte[] { 0x05, 0x01, 0x00 }; // no auth by default
        if (proxy.HasAuthentication)
            greeting[2] = 0x02; // username/password auth
        await stream.WriteAsync(greeting, token);

        // Read greeting response
        var resp = new byte[2];
        await ReadExactAsync(stream, resp, token);
        if (resp[0] != 0x05)
        {
            client.Dispose();
            throw new InvalidOperationException($"SOCKS5 server returned version {resp[0]}");
        }

        // Auth negotiation
        if (resp[1] == 0x02 && proxy.HasAuthentication)
        {
            // Username/password auth (RFC 1929)
            var auth = new List<byte>();
            auth.Add(0x01); // version
            auth.Add((byte)proxy.Username.Length);
            auth.AddRange(Encoding.ASCII.GetBytes(proxy.Username));
            auth.Add((byte)proxy.Password.Length);
            auth.AddRange(Encoding.ASCII.GetBytes(proxy.Password));
            await stream.WriteAsync(auth.ToArray(), token);

            var authResp = new byte[2];
            await ReadExactAsync(stream, authResp, token);
            if (authResp[1] != 0x00)
            {
                client.Dispose();
                throw new InvalidOperationException("SOCKS5 proxy authentication failed");
            }
        }
        else if (resp[1] == 0xFF)
        {
            client.Dispose();
            throw new InvalidOperationException("SOCKS5 proxy requires authentication");
        }

        // Connect request: CONNECT, reserved, DOMAIN, host, port
        var connect = new List<byte>();
        connect.Add(0x05); // version
        connect.Add(0x01); // CONNECT
        connect.Add(0x00); // reserved
        connect.Add(0x03); // domain name
        connect.Add((byte)targetHost.Length);
        connect.AddRange(Encoding.ASCII.GetBytes(targetHost));
        connect.Add((byte)(targetPort >> 8));
        connect.Add((byte)(targetPort & 0xFF));
        await stream.WriteAsync(connect.ToArray(), token);

        // Read connect response
        var connectResp = new byte[10];
        await ReadExactAsync(stream, connectResp, token);
        if (connectResp[1] != 0x00)
        {
            var errorMsg = connectResp[1] switch
            {
                0x01 => "general failure",
                0x02 => "connection not allowed",
                0x03 => "network unreachable",
                0x04 => "host unreachable",
                0x05 => "connection refused",
                0x06 => "TTL expired",
                0x07 => "command not supported",
                0x08 => "address type not supported",
                _ => $"error code {connectResp[1]}"
            };
            client.Dispose();
            throw new InvalidOperationException($"SOCKS5 connect failed: {errorMsg}");
        }

        return client;
    }

    // ─── HTTP Proxy CONNECT ────────────────────────────────────────────────

    private static async Task<TcpClient> HttpProxyConnectAsync(
        ProxyEndpoint proxy, string target, CancellationToken token)
    {
        var client = new TcpClient();
        await client.ConnectAsync(proxy.Host, proxy.Port, token);
        var stream = client.GetStream();

        var connectReq = $"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n";
        if (proxy.HasAuthentication)
        {
            var cred = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{proxy.Username}:{proxy.Password}"));
            connectReq += $"Proxy-Authorization: Basic {cred}\r\n";
        }
        connectReq += "\r\n";

        var reqBytes = Encoding.ASCII.GetBytes(connectReq);
        await stream.WriteAsync(reqBytes, token);

        // Read response line
        var response = await ReadLineAsync(stream, token);
        if (response == null || !response.Contains("200"))
        {
            client.Dispose();
            throw new InvalidOperationException($"HTTP proxy CONNECT failed: {response}");
        }

        // Skip remaining headers
        string? headerLine;
        while ((headerLine = await ReadLineAsync(stream, token)) != null && headerLine.Length > 0) { }

        return client;
    }

    // ─── Relay ─────────────────────────────────────────────────────────────

    private static async Task RelayAsync(Stream a, Stream b, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var t1 = PumpAsync(a, b, cts.Token);
        var t2 = PumpAsync(b, a, cts.Token);
        await Task.WhenAny(t1, t2);
        cts.Cancel(); // stop the other direction
        await Task.WhenAll(t1, t2); // wait for clean exit
    }

    private static async Task PumpAsync(Stream source, Stream dest, CancellationToken token)
    {
        var buf = new byte[8192];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = await source.ReadAsync(buf, token);
                if (read == 0) break;
                await dest.WriteAsync(buf.AsMemory(0, read), token);
            }
        }
        catch { }
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token);
            if (read == 0) throw new IOException("Connection closed while reading");
            offset += read;
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var sb = new StringBuilder(256);
        var buf = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buf, token);
            if (read == 0) return sb.Length > 0 ? sb.ToString() : null;
            var ch = (char)buf[0];
            if (ch == '\n') break;
            if (ch != '\r') sb.Append(ch);
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _listener.Stop();
    }
}
