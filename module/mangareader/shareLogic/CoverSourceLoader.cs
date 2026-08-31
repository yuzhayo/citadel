using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace Module.Mangareader.ShareLogic;

public sealed record CoverSourceResult(byte[] PngBytes, string SourceLabel);

public sealed class CoverSourceLoader
{
    private const long MaximumSourceBytes = 25L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();

    public async Task<CoverSourceResult> LoadAsync(
        string source,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var trimmed = source.Trim();

        byte[] bytes;
        string label;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            bytes = await DownloadAsync(uri, cancellationToken);
            label = uri.Host;
        }
        else
        {
            var path = Uri.TryCreate(trimmed, UriKind.Absolute, out uri) && uri.IsFile
                ? uri.LocalPath
                : Path.GetFullPath(trimmed);
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("Cover image was not found.", path);
            if (file.Length > MaximumSourceBytes)
                throw new InvalidDataException("Cover image is larger than 25 MB.");

            bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            label = file.Name;
        }

        var png = await Task.Run(
            () => ConvertToPng(bytes),
            cancellationToken);
        return new CoverSourceResult(png, label);
    }

    private static async Task<byte[]> DownloadAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaximumSourceBytes)
            throw new InvalidDataException("Downloaded cover is larger than 25 MB.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (destination.Length + read > MaximumSourceBytes)
                throw new InvalidDataException("Downloaded cover is larger than 25 MB.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return destination.ToArray();
    }

    private static byte[] ConvertToPng(byte[] bytes)
    {
        try
        {
            using var source = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(
                source,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                throw new InvalidDataException("Cover source has no image frame.");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
            using var destination = new MemoryStream();
            encoder.Save(destination);
            return destination.ToArray();
        }
        catch (Exception exception) when (exception is NotSupportedException
            or FileFormatException
            or ArgumentException)
        {
            throw new InvalidDataException("Cover source is not a supported image.", exception);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Citadel-MangaReader/1.0");
        return client;
    }
}
