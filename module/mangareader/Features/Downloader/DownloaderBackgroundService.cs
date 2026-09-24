using System.Net.Http;
using Module.Mangareader.Features.Downloader.AutoCover;
using Module.Mangareader.Features.Downloader.Catalog;
using Module.Mangareader.Features.Downloader.Lister;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Library;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Features.Downloader;

/// <summary>
/// Runtime-lifetime owner of Downloader work. Constructed in
/// <c>MangaReaderModule.StartRuntimeAsync</c> and disposed when the shell
/// destroys the runtime lifetime. Routed views borrow its context and
/// unsubscribe when hidden; Stop runs the pause-and-drain barrier before
/// that lifetime may die.
/// </summary>
internal sealed class DownloaderBackgroundService : IDisposable
{
    private readonly HttpClient _coverClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ProxyHttpTransport _httpTransport;
    private readonly DownloaderPyHostClient _browser;
    private readonly DownloadQueueFeature _queue;
    private readonly DownloaderOnlineProcess _onlineProcess;
    private int _disposed;
    private int _forceAborted;

    public DownloaderBackgroundService(
        LibraryRootContext libraryRoot,
        ProxyLeaseRegistry proxyReservations)
    {
        ArgumentNullException.ThrowIfNull(libraryRoot);
        ArgumentNullException.ThrowIfNull(proxyReservations);

        var stagingRoot = DownloaderJson.DefaultRoot();
        var proxyPool = new ProxyPoolAdapter("MangaReader Downloader", proxyReservations);
        _httpTransport = new ProxyHttpTransport(proxyPool);
        _browser = new DownloaderPyHostClient(stagingRoot, proxyPool);
        var sources = MangaSourceRegistry.CreateDefault(_browser, _httpTransport);
        var index = new DownloadSourceIndex(stagingRoot);
        _queue = new DownloadQueueFeature(
            libraryRoot,
            sources,
            _browser,
            new DownloadQueueStore(stagingRoot),
            index,
            _httpTransport);

        var lister = new ListerFeature(
            new LocalTitleProbe(),
            () => libraryRoot.CurrentRoot,
            (root, title) => index.DeterministicFolderName(root, title));
        var autoCover = new AutoCoverFeature(
            (url, token) => _httpTransport.GetByteArrayAsync(
                "downloader-auto-cover", _coverClient, url, token),
            lister);
        var catalog = new CatalogFeature(sources, () => libraryRoot.CurrentRoot is not null);
        _onlineProcess = new DownloaderOnlineProcess(catalog, _browser);

        Context = new DownloaderContext(
            sources,
            _queue,
            _browser,
            libraryRoot,
            index,
            lister,
            autoCover,
            _onlineProcess,
            proxyPool,
            _httpTransport);

        // Persisted in-flight jobs are restored as Paused, so process startup
        // does not create network traffic. New/resumed work can run while the
        // main window is hidden in the tray.
        _queue.Start();
    }

    public DownloaderContext Context { get; }

    /// <summary>Safe Stop barrier (module StopRuntimeAsync).</summary>
    public Task PauseAndDrainAsync(CancellationToken cancellationToken = default) =>
        _queue.PauseAndDrainAsync(cancellationToken);

    /// <summary>
    /// Force path: terminal generation on the queue, abort online/browse and
    /// the Downloader-owned PyHost session. Does not dispose; lifetime destroy
    /// runs <see cref="Dispose"/> which honors the forced-terminal flag.
    /// </summary>
    public void ForceAbort(long generation)
    {
        if (Interlocked.Exchange(ref _forceAborted, 1) != 0) return;
        try { _onlineProcess.Stop(); }
        catch (Exception exception)
        {
            Citadel.Core.Log.Main(
                $"[Downloader] force online stop failed: {exception.GetBaseException().Message}");
        }
        try { _browser.AbortSession(); }
        catch (Exception exception)
        {
            Citadel.Core.Log.Main(
                $"[Downloader] force browser abort failed: {exception.GetBaseException().Message}");
        }
        _queue.ForceAbort(generation);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (Volatile.Read(ref _forceAborted) != 0)
        {
            // DisposeAfterForceAbort: cancel/close without settlement waits.
            try { _onlineProcess.Dispose(); }
            catch (Exception exception)
            {
                Citadel.Core.Log.Main(
                    $"[Downloader] force online dispose failed: {exception.GetBaseException().Message}");
            }
            _queue.DisposeAfterForceAbort();
            // PyHost.Dispose already runs graceful ladder on a background task;
            // AbortSession already killed the owned tree on ForceAbort.
            try { _browser.Dispose(); }
            catch (Exception exception)
            {
                Citadel.Core.Log.Main(
                    $"[Downloader] force browser dispose failed: {exception.GetBaseException().Message}");
            }
            try { _httpTransport.Dispose(); }
            catch { /* force path never blocks on transport */ }
            try { _coverClient.Dispose(); }
            catch { /* force path never blocks on cover client */ }
            return;
        }

        // Normal path: Stop already PauseAndDrain'd; Dispose must not GetResult.
        _onlineProcess.Dispose();
        _queue.Dispose();
        _browser.Dispose();
        _httpTransport.Dispose();
        _coverClient.Dispose();
    }
}
