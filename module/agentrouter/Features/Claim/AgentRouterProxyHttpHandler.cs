using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.Http;
using System.IO;
using CitadelBridge;

namespace Module.Agentrouter.Features.Claim;

/// <summary>Creates HTTP clients over leased HTTP(S), SOCKS4, or SOCKS5 endpoints.</summary>
internal static class AgentRouterProxyHttpHandler
{
    public static SocketsHttpHandler Create(ProxyEndpoint? proxy)
    {
        var handler = new SocketsHttpHandler();
        if (proxy is null)
        {
            return handler;
        }

        if (proxy.Scheme is "http" or "https")
        {
            var webProxy = new WebProxy(proxy.Server);
            if (proxy.Username is not null)
            {
                webProxy.Credentials = new NetworkCredential(
                    proxy.Username, proxy.Password ?? string.Empty);
            }
            handler.Proxy = webProxy;
            handler.UseProxy = true;
            return handler;
        }

        if (proxy.Scheme is "socks4" or "socks5")
        {
            handler.ConnectCallback = (context, cancellationToken) =>
                ConnectSocksAsync(proxy, context.DnsEndPoint, cancellationToken);
            return handler;
        }

        handler.Dispose();
        throw new NotSupportedException($"Unsupported Agent Router proxy scheme: {proxy.Scheme}");
    }

    private static ValueTask<Stream> ConnectSocksAsync(
        ProxyEndpoint proxy,
        DnsEndPoint destination,
        CancellationToken cancellationToken)
        => proxy.Scheme == "socks4"
            ? ConnectSocks4Async(proxy, destination, cancellationToken)
            : ConnectSocks5Async(proxy, destination, cancellationToken);

    private static async ValueTask<Stream> ConnectSocks4Async(
        ProxyEndpoint proxy,
        DnsEndPoint destination,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(proxy.Host, proxy.Port, cancellationToken)
                .ConfigureAwait(false);
            var stream = client.GetStream();
            var userId = Encoding.UTF8.GetBytes(proxy.Username ?? string.Empty);
            var address = IPAddress.TryParse(destination.Host, out var parsedAddress)
                && parsedAddress.AddressFamily == AddressFamily.InterNetwork
                    ? parsedAddress.GetAddressBytes()
                    : new byte[] { 0, 0, 0, 1 };
            var domain = address[0] == 0 && address[1] == 0
                && address[2] == 0 && address[3] == 1
                    ? Encoding.ASCII.GetBytes(destination.Host)
                    : [];
            if (userId.Length > 255 || domain.Length > 255)
            {
                throw new IOException("SOCKS4 proxy request exceeds protocol limits");
            }

            var request = new byte[9 + userId.Length + domain.Length + (domain.Length > 0 ? 1 : 0)];
            request[0] = 4;
            request[1] = 1;
            request[2] = (byte)(destination.Port >> 8);
            request[3] = (byte)destination.Port;
            address.CopyTo(request, 4);
            userId.CopyTo(request, 8);
            request[8 + userId.Length] = 0;
            if (domain.Length > 0)
            {
                domain.CopyTo(request, 9 + userId.Length);
                request[^1] = 0;
            }

            await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            var response = new byte[8];
            await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
            if (response[0] is not (0 or 4) || response[1] != 0x5A)
            {
                throw new IOException($"SOCKS4 connect failed (reply {response[1]})");
            }

            return new OwnedNetworkStream(client, stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async ValueTask<Stream> ConnectSocks5Async(
        ProxyEndpoint proxy,
        DnsEndPoint destination,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(proxy.Host, proxy.Port, cancellationToken)
                .ConfigureAwait(false);
            var stream = client.GetStream();

            var authenticated = proxy.Username is not null;
            await stream.WriteAsync(
                authenticated ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 },
                cancellationToken).ConfigureAwait(false);
            var selection = new byte[2];
            await stream.ReadExactlyAsync(selection, cancellationToken).ConfigureAwait(false);
            if (selection[0] != 5 || selection[1] == 0xFF
                || (selection[1] == 2 && !authenticated)
                || (selection[1] != 0 && selection[1] != 2))
            {
                throw new IOException("SOCKS5 proxy rejected available authentication methods");
            }

            if (selection[1] == 2)
            {
                var username = Encoding.UTF8.GetBytes(proxy.Username!);
                var password = Encoding.UTF8.GetBytes(proxy.Password ?? string.Empty);
                if (username.Length > byte.MaxValue || password.Length > byte.MaxValue)
                {
                    throw new IOException("SOCKS5 credentials exceed protocol limits");
                }

                var auth = new byte[3 + username.Length + password.Length];
                auth[0] = 1;
                auth[1] = (byte)username.Length;
                username.CopyTo(auth, 2);
                auth[2 + username.Length] = (byte)password.Length;
                password.CopyTo(auth, 3 + username.Length);
                await stream.WriteAsync(auth, cancellationToken).ConfigureAwait(false);
                var authResult = new byte[2];
                await stream.ReadExactlyAsync(authResult, cancellationToken).ConfigureAwait(false);
                if (authResult[0] != 1 || authResult[1] != 0)
                {
                    throw new IOException("SOCKS5 proxy authentication failed");
                }
            }

            var host = Encoding.ASCII.GetBytes(destination.Host);
            if (host.Length is 0 or > byte.MaxValue)
            {
                throw new IOException("Destination host exceeds SOCKS5 protocol limits");
            }

            var connect = new byte[7 + host.Length];
            connect[0] = 5;
            connect[1] = 1;
            connect[2] = 0;
            connect[3] = 3; // Domain name; let the proxy resolve DNS.
            connect[4] = (byte)host.Length;
            host.CopyTo(connect, 5);
            connect[^2] = (byte)(destination.Port >> 8);
            connect[^1] = (byte)destination.Port;
            await stream.WriteAsync(connect, cancellationToken).ConfigureAwait(false);

            var response = new byte[4];
            await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
            if (response[0] != 5 || response[1] != 0)
            {
                throw new IOException($"SOCKS5 connect failed (reply {response[1]})");
            }

            var addressLength = response[3] switch
            {
                1 => 4,
                4 => 16,
                3 => await ReadDomainLengthAsync(stream, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new IOException("SOCKS5 proxy returned an invalid address type"),
            };
            var remainder = new byte[addressLength + 2];
            await stream.ReadExactlyAsync(remainder, cancellationToken).ConfigureAwait(false);
            return new OwnedNetworkStream(client, stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async ValueTask<int> ReadDomainLengthAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        return length[0];
    }

    private sealed class OwnedNetworkStream(TcpClient client, Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) client.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
