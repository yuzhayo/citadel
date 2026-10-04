using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Module.Yuzvid.Features.Extraction;

namespace Module.Yuzvid.Features.Queue;

/// <summary>One download row. Bound to the queue table; mutated on the UI thread.</summary>
public sealed class QueueItem : INotifyPropertyChanged
{
    public string Title { get; }
    public string Url { get; }
    public string Item { get; }

    /// <summary>
    /// True when the URL already IS the media file (.mp4/.m3u8/.webm) and the
    /// naive HTTP path can fetch it directly. False for player pages
    /// (playmogo/doodstream /d/ links) that must be resolved to a CDN URL first.
    /// </summary>
    internal bool IsDirectMedia { get; }

    private string _status = "Antre";
    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(nameof(Status)); } }
    }

    private string _progress = "—";
    public string Progress
    {
        get => _progress;
        set { if (_progress != value) { _progress = value; OnPropertyChanged(nameof(Progress)); } }
    }

    private string _detail = "menunggu";
    public string Detail
    {
        get => _detail;
        set { if (_detail != value) { _detail = value; OnPropertyChanged(nameof(Detail)); } }
    }

    internal CancellationTokenSource? Cts;
    internal string? FilePath;
    internal int Attempts;

    /// <summary>Job id di plugin Python saat unduhan lewat engine (null untuk jalur direct).</summary>
    internal string? EngineJobId;

    public QueueItem(string title, string url, string item, bool isDirectMedia = false)
    {
        Title = title;
        Url = url;
        Item = item;
        IsDirectMedia = isDirectMedia;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Owns download transfers. Two paths, decided per item:
///   - <b>direct media</b> (.mp4/.m3u8/.webm): naive HttpGet, routed through the
///     controller-supplied <see cref="IWebProxy"/> (same local proxy as browsing).
///   - <b>player page</b> (playmogo/doodstream /d/ link): resolved to a CDN URL
///     and downloaded by the pyhost <c>yuzvid_downloader</c> plugin (multi-thread
///     range + resume). The plugin runs its own engine; this class only drives it.
/// No WebView2, no Browser internals: upstream/proxy ownership never leaves the
/// Browser feature. All public methods must be called from the UI thread.
/// </summary>
public sealed class QueueEngine : IDisposable
{
    private const int MaxAttempts = 2;
    private const int BufferSize = 64 * 1024;
    private const int EngineThreads = 4;

    private readonly Func<IWebProxy?> _proxyProvider;
    private readonly QueueDownloaderClient _engine;
    private bool _disposed;

    public ObservableCollection<QueueItem> Items { get; } = new();

    /// <summary>Default folder (immutable); resolved folder comes from settings.</summary>
    public static string DownloadsFolder => QueueSettingsStore.DefaultDownloadFolder;

    /// <summary>Active download folder (configurable via UI, persisted).</summary>
    public string DownloadFolder { get; private set; }

    /// <summary>
    /// Constructor is internal: the composition root (YuzvidModule) builds the
    /// engine + client pair; the parent receives the ready-made instance and
    /// never constructs feature types (guard YUZ-2).
    /// </summary>
    internal QueueEngine(Func<IWebProxy?> proxyProvider, QueueDownloaderClient engine)
    {
        _proxyProvider = proxyProvider ?? throw new ArgumentNullException(nameof(proxyProvider));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        DownloadFolder = QueueSettingsStore.ResolveDownloadFolder();
    }

    /// <summary>
    /// Change the download folder (persisted). Client is retargeted so the
    /// Python engine writes into the new folder for subsequent jobs.
    /// </summary>
    public void SetDownloadFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var full = Path.GetFullPath(folder);
        DownloadFolder = full;
        QueueSettingsStore.SetDownloadFolder(full);
        _engine.SetOutputDir(full);
        Directory.CreateDirectory(full);
    }

    public QueueItem Enqueue(VideoLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        // DisplayTitle is the same derivation the extractor shows (anchor label →
        // episode title → humanized slug); the old Slug-first form surfaced raw
        // hashes (e.g. "snu73d1s27wb") for label-carrying pages.
        var title = link.DisplayTitle;
        if (string.IsNullOrEmpty(title)) title = link.Host;
        var item = new QueueItem(title, link.Full, link.ServerGroup, link.IsDirectMedia)
        {
            Detail = link.DetailLine,
        };
        Items.Add(item);
        return item;
    }

    public async Task StartAsync(QueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Status == "Mengunduh") return;
        if (item.Status == "Selesai") return;

        item.Attempts = 0;
        item.Cts?.Dispose();
        item.Cts = new CancellationTokenSource();

        while (item.Attempts < MaxAttempts)
        {
            item.Attempts++;
            item.Status = "Mengunduh";
            item.Detail = item.Attempts > 1
                ? $"mencoba lagi ({item.Attempts}/{MaxAttempts})"
                : "menghubungi…";
            try
            {
                await DownloadOnceAsync(item, item.Cts.Token);
                item.Status = "Selesai";
                item.Progress = "100%";
                item.Detail = item.FilePath ?? "selesai";
                return;
            }
            catch (OperationCanceledException) when (item.Cts.IsCancellationRequested)
            {
                item.Status = "Batal";
                item.Detail = "dibatalkan";
                return;
            }
            catch (Exception ex) when (item.Attempts < MaxAttempts)
            {
                item.Detail = "gagal: " + ex.Message + " — mencoba lagi…";
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                item.Status = "Gagal";
                item.Progress = "—";
                item.Detail = ex.Message;
                return;
            }
        }
    }

    public void Cancel(QueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try { item.Cts?.Cancel(); } catch { /* best effort */ }
        TryStopEngineJob(item.EngineJobId);
    }

    /// <summary>
    /// Best-effort stop job plugin (lepas dari kanal UI). Kegagalan diabaikan —
    /// checkpoint tetap tersimpan sehingga resume aman.
    /// </summary>
    private void TryStopEngineJob(string? jobId)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _engine.StopAsync(jobId!, cts.Token).ConfigureAwait(false);
            }
            catch { /* plugin/transport tidak siap — checkpoint tetap aman */ }
        });
    }

    public void CancelAll()
    {
        foreach (var item in Items)
        {
            if (item.Status == "Mengunduh" || item.Status == "Antre")
            {
                item.Status = "Batal";
                item.Detail = "dibatalkan";
            }
            try { item.Cts?.Cancel(); } catch { /* best effort */ }
        }
    }

    public void ClearCompleted()
    {
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].Status == "Selesai" || Items[i].Status == "Batal")
                Items.RemoveAt(i);
        }
    }

    public void Remove(QueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Cancel(item);
        Items.Remove(item);
    }

    private async Task DownloadOnceAsync(QueueItem item, CancellationToken token)
    {
        Directory.CreateDirectory(DownloadFolder);
        if (item.IsDirectMedia)
        {
            await DownloadDirectAsync(item, token);
        }
        else
        {
            await DownloadViaEngineAsync(item, token);
        }
    }

    /// <summary>
    /// Player page (/d/ link): resolve → CDN URL, then hand the transfer to the
    /// pyhost engine (multi-thread range + resume). Polls plugin status ~2x/s.
    /// </summary>
    private async Task DownloadViaEngineAsync(QueueItem item, CancellationToken token)
    {
        item.Detail = "resolve CDN…";
        var resolution = await _engine.ResolveAsync(item.Url, token);
        if (!resolution.Success || string.IsNullOrEmpty(resolution.CdnUrl))
        {
            throw new InvalidOperationException(
                resolution.Error ?? "CDN URL tidak ditemukan");
        }

        item.Detail = $"engine: {resolution.Filename}";
        var jobId = await _engine.StartAsync(
            resolution.CdnUrl!,
            resolution.Filename ?? "video.mp4",
            resolution.FileSize,
            resolution.Etag,
            EngineThreads,
            token);
        if (string.IsNullOrEmpty(jobId))
            throw new InvalidOperationException("Plugin unduhan menolak job.");

        item.EngineJobId = jobId;
        item.FilePath = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var p = await _engine.StatusAsync(jobId!, token);

            switch (p.Status)
            {
                case "done":
                    item.Progress = "100%";
                    item.FilePath = p.SavePath;
                    return;
                case "failed":
                    throw new InvalidOperationException(p.Error ?? "Unduhan gagal");
                case "stopped":
                    throw new OperationCanceledException(token);
                default:
                    var mb = p.Downloaded / 1048576.0;
                    var totalMb = p.Total / 1048576.0;
                    item.Progress = p.Total > 0
                        ? $"{p.Percent:0}% ({mb:0.0}/{totalMb:0.0} MB)"
                        : $"{mb:0.0} MB";
                    var speed = p.Speed >= 1048576
                        ? $"{p.Speed / 1048576:0.0} MB/s"
                        : $"{p.Speed / 1024:0} KB/s";
                    item.Detail = $"engine · {speed}";
                    await Task.Delay(500, token);
                    break;
            }
        }
    }

    /// <summary>Direct media URL: naive streaming GET through the browser proxy.</summary>
    private async Task DownloadDirectAsync(QueueItem item, CancellationToken token)
    {
        var filePath = NextFreePath(BuildFileName(item.Url));
        item.FilePath = filePath;

        using var handler = new HttpClientHandler();
        var proxy = _proxyProvider();
        handler.Proxy = proxy;
        handler.UseProxy = proxy is not null;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        using var response = await http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var content = await response.Content.ReadAsStreamAsync(token);
        await using var file = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        long received = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, token)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), token);
            received += read;
            item.Progress = total is > 0
                ? $"{received * 100 / total}%"
                : $"{received / 1048576.0:0.0} MB";
        }
    }

    private static string BuildFileName(string url)
    {
        string name;
        try
        {
            var uri = new Uri(url);
            name = Path.GetFileName(uri.AbsolutePath);
        }
        catch
        {
            name = "video";
        }
        if (string.IsNullOrWhiteSpace(name)) name = "video";
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return name;
    }

    private string NextFreePath(string fileName)
    {
        var path = Path.Combine(DownloadFolder, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(DownloadFolder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelAll();
    }
}
