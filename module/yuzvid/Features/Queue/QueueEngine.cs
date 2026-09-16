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

    public QueueItem(string title, string url, string item)
    {
        Title = title;
        Url = url;
        Item = item;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Owns download transfers. No WebView2, no Browser internals: downloads route
/// through the controller-supplied <see cref="IWebProxy"/> (same local proxy as
/// browsing), so upstream/proxy ownership never leaves the Browser feature.
/// All public methods must be called from the UI thread (items are UI-bound).
/// </summary>
public sealed class QueueEngine : IDisposable
{
    private const int MaxAttempts = 2;
    private const int BufferSize = 64 * 1024;

    private readonly Func<IWebProxy?> _proxyProvider;
    private bool _disposed;

    public ObservableCollection<QueueItem> Items { get; } = new();

    public static string DownloadsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Citadel", "Yuzvid", "Downloads");

    public QueueEngine(Func<IWebProxy?> proxyProvider)
    {
        _proxyProvider = proxyProvider ?? throw new ArgumentNullException(nameof(proxyProvider));
    }

    public QueueItem Enqueue(VideoLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var title = string.IsNullOrEmpty(link.EpisodeTitle) ? link.Slug : $"{link.EpisodeTitle} (part {link.EpisodeNum})";
        if (string.IsNullOrEmpty(title)) title = link.Host;
        var item = new QueueItem(title, link.Full, link.ServerGroup) { Detail = link.ServerGroup };
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
        Directory.CreateDirectory(DownloadsFolder);
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

    private static string NextFreePath(string fileName)
    {
        var path = Path.Combine(DownloadsFolder, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(DownloadsFolder, $"{stem} ({i}){ext}");
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
