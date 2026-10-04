using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CitadelBridge;

namespace Module.Yuzvid.Features.Queue;

/// <summary>Hasil resolve CDN: URL langsung + metadata file.</summary>
internal sealed record CdnResolution(
    bool Success,
    string? CdnUrl,
    string? Filename,
    long FileSize,
    string? Etag,
    string? Error);

/// <summary>Snapshot progres satu job unduhan dari plugin Python.</summary>
internal sealed record DownloadProgress(
    string Status,
    double Percent,
    long Downloaded,
    long Total,
    double Speed,
    double Eta,
    string? SavePath,
    string? Error);

/// <summary>
/// Adapter command/payload Queue atas transport pyhost bersama. Feature Queue
/// memegang client ini; parent dan feature lain tidak menyentuh internalnya.
///
/// Transport: satu proses pyhost per client, di-start lazily. Plugin
/// <c>yuzvid_downloader</c> di-aktifkan lewat nama (CITADEL_PYHOST_PLUGINS);
/// core pyhost tetap bebas-fitur.
///
/// Protokol v1 bersifat sekuensial tanpa event: unduhan panjang berjalan di
/// worker thread plugin, dan client melakukan POLL <c>yuzvid.download.status</c>
/// sampai status terminal.
/// </summary>
internal sealed class QueueDownloaderClient : IDisposable
{
    private const string PluginName = "yuzvid_downloader";

    /// <summary>Resolve CDN: buka browser, jelajah 2 halaman, poll anchor.</summary>
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Start unduhan: plugin balas segera setelah thread hidup.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Poll status: request pendek.</summary>
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(10);

    private readonly object _sync = new();
    private string _outputDir;
    private PyHost? _host;
    private int _disposed;

    /// <summary>Folder unduhan Queue (sama dengan yang dipakai QueueEngine).</summary>
    public QueueDownloaderClient(string outputDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        _outputDir = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(_outputDir);
    }

    /// <summary>Retarget the plugin output folder for subsequent jobs.</summary>
    public void SetOutputDir(string outputDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        var full = Path.GetFullPath(outputDir);
        lock (_sync)
        {
            _outputDir = full;
        }
        Directory.CreateDirectory(full);
    }

    public async Task<CdnResolution> ResolveAsync(string url, CancellationToken ct)
    {
        var host = EnsureHost();
        var response = await host.SendAsync(
            "yuzvid.resolve",
            new JsonObject { ["url"] = url },
            ResolveTimeout,
            ct).ConfigureAwait(false);

        return new CdnResolution(
            response["success"]?.GetValue<bool>() ?? false,
            response["cdn_url"]?.GetValue<string>(),
            response["filename"]?.GetValue<string>(),
            response["file_size"]?.GetValue<long>() ?? 0,
            response["etag"]?.GetValue<string>(),
            response["error"]?.GetValue<string>());
    }

    public async Task<string?> StartAsync(
        string cdnUrl, string filename, long fileSize, string? etag,
        int threads, CancellationToken ct)
    {
        var host = EnsureHost();
        string outputDir;
        lock (_sync) { outputDir = _outputDir; }
        var response = await host.SendAsync(
            "yuzvid.download.start",
            new JsonObject
            {
                ["cdn_url"] = cdnUrl,
                ["filename"] = filename,
                ["file_size"] = fileSize,
                ["etag"] = etag ?? "",
                ["output_dir"] = outputDir,
                ["threads"] = threads,
            },
            StartTimeout,
            ct).ConfigureAwait(false);

        var outcome = response["outcome"]?.GetValue<string>();
        if (!string.Equals(outcome, "started", StringComparison.Ordinal))
            return null;
        return response["job_id"]?.GetValue<string>();
    }

    public async Task<DownloadProgress> StatusAsync(string jobId, CancellationToken ct)
    {
        var host = EnsureHost();
        var r = await host.SendAsync(
            "yuzvid.download.status",
            new JsonObject { ["job_id"] = jobId },
            StatusTimeout,
            ct).ConfigureAwait(false);

        return new DownloadProgress(
            r["status"]?.GetValue<string>() ?? "unknown",
            r["percent"]?.GetValue<double>() ?? 0,
            r["downloaded"]?.GetValue<long>() ?? 0,
            r["total"]?.GetValue<long>() ?? 0,
            r["speed"]?.GetValue<double>() ?? 0,
            r["eta"]?.GetValue<double>() ?? 0,
            r["save_path"]?.GetValue<string>(),
            r["error"]?.GetValue<string>());
    }

    public async Task StopAsync(string jobId, CancellationToken ct)
    {
        var host = EnsureHost();
        await host.SendAsync(
            "yuzvid.download.stop",
            new JsonObject { ["job_id"] = jobId },
            StatusTimeout,
            ct).ConfigureAwait(false);
    }

    private PyHost EnsureHost()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_host is not null)
                return _host;

            var python = RuntimeSetup.VenvPython;
            if (!File.Exists(python))
            {
                throw new InvalidOperationException(
                    "Runtime belum siap — buka tab Runtime lalu jalankan Setup runtime.");
            }

            var script = RuntimeSetup.DeployedPyhostScript;
            if (!File.Exists(script))
            {
                throw new InvalidOperationException(
                    "Payload pyhost tidak ter-deploy: " + script);
            }

            _host = PyHost.Start(python, script, CredenzPath.Resolve(), PluginName);
            return _host;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_sync)
        {
            _host?.Dispose();
            _host = null;
        }
    }
}
