using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace Module.Mangareader.CoverBuilder;

public sealed record CoverSourceResult(byte[] PngBytes, string SourceLabel);

public sealed record FetchedCoverResult(
    string LocalPath,
    string SourceLabel,
    string SourceUrl);

public sealed class CoverSourceLoader
{
    private const long MaximumSourceBytes = 25L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();
    private readonly string _downloadRoot;

    public CoverSourceLoader()
    {
        _downloadRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "cover-downloads");
    }

    public async Task<CoverSourceResult> LoadLocalAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Trim();
        if (TryGetRemoteUri(trimmed, out _))
            throw new InvalidOperationException("Fetch the URL before baking the cover.");

        var localPath = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath
            : Path.GetFullPath(trimmed);
        var file = new FileInfo(localPath);
        if (!file.Exists) throw new FileNotFoundException("Cover image was not found.", localPath);
        if (file.Length > MaximumSourceBytes)
            throw new InvalidDataException("Cover image is larger than 25 MB.");

        var bytes = await File.ReadAllBytesAsync(localPath, cancellationToken);

        var png = await Task.Run(
            () => ConvertToPng(bytes),
            cancellationToken);
        return new CoverSourceResult(png, file.Name);
    }

    public async Task<FetchedCoverResult> FetchAsync(
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUrl);
        var trimmed = sourceUrl.Trim();
        if (!TryGetRemoteUri(trimmed, out var uri))
            throw new ArgumentException("Enter a complete HTTP(S) image URL.", nameof(sourceUrl));

        var bytes = await DownloadAsync(uri, cancellationToken);
        var png = await Task.Run(
            () => ConvertToPng(bytes),
            cancellationToken);
        var localPath = await SaveFetchedPngAsync(uri, png, cancellationToken);
        return new FetchedCoverResult(localPath, uri.Host, trimmed);
    }

    public static bool TryGetRemoteUri(string? source, out Uri uri)
    {
        var valid = Uri.TryCreate(source?.Trim(), UriKind.Absolute, out var candidate)
            && (candidate.Scheme == Uri.UriSchemeHttp
                || candidate.Scheme == Uri.UriSchemeHttps);
        uri = valid ? candidate! : null!;
        return valid;
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

    private async Task<string> SaveFetchedPngAsync(
        Uri uri,
        byte[] pngBytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_downloadRoot);
        var identity = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..16];
        var localPath = Path.Combine(_downloadRoot, $"cover-{identity}.png");
        var temporaryPath = Path.Combine(
            _downloadRoot,
            $".cover-{identity}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(temporaryPath, pngBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, localPath, overwrite: true);
            return localPath;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
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
