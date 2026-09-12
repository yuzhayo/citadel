namespace Module.Mangareader.Features.Downloader.Catalog;

/// <summary>
/// Application-lifetime owner of Downloader browse execution. Screens issue
/// commands and render <see cref="CatalogFeature.State"/>; they never own the
/// cancellation source or browser process they control.
/// </summary>
public sealed class DownloaderOnlineProcess : IDisposable
{
    private readonly CatalogFeature _catalog;
    private readonly DownloaderPyHostClient _browser;
    private readonly object _gate = new();
    private CancellationTokenSource? _browseCancellation;
    private Task? _browseTask;
    private int _disposed;

    internal DownloaderOnlineProcess(
        CatalogFeature catalog,
        DownloaderPyHostClient browser)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
    }

    public CatalogFeature Catalog => _catalog;

    public Task StartAsync(string? query)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_browseTask is { IsCompleted: false }) return _browseTask;

            var cancellation = new CancellationTokenSource();
            _browseCancellation = cancellation;
            // CatalogFeature snapshots the active WPF filter contribution before
            // its first await. Start it on the caller's dispatcher thread; the
            // feature's own asynchronous I/O continues without blocking the UI.
            _browseTask = RunAsync(query, cancellation);
            return _browseTask;
        }
    }

    /// <summary>Stops only the Downloader provider process and active browse.</summary>
    public void Stop()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _browseCancellation;
        }

        _catalog.AbandonActiveBrowse();
        cancellation?.Cancel();
        _browser.AbortSession();
    }

    private async Task RunAsync(string? query, CancellationTokenSource cancellation)
    {
        try
        {
            await _catalog.StartAsync(query, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_browseCancellation, cancellation))
                {
                    _browseCancellation = null;
                    _browseTask = null;
                }
            }
            cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
    }
}
