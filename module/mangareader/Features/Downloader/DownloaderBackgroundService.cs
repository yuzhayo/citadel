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
/// Application-lifetime owner of Downloader work. Routed views borrow its
/// context and unsubscribe when hidden; only a feature Stop command may stop
/// its provider session before the application lifetime ends.
/// </summary>
internal sealed class DownloaderBackgroundService : IDisposable
{
    private readonly HttpClient _coverClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ProxyHttpTransport _httpTransport;
    private readonly DownloaderPyHostClient _browser;
    private readonly DownloadQueueFeature _queue;
    private readonly DownloaderOnlineProcess _onlineProcess;
    private int _disposed;

    public DownloaderBackgroundService(LibraryRootContext libraryRoot)
    {
        ArgumentNullException.ThrowIfNull(libraryRoot);

        var stagingRoot = DownloaderJson.DefaultRoot();
        var proxyPool = new ProxyPoolAdapter("MangaReader Downloader");
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _onlineProcess.Dispose();
        _queue.Dispose();
        _browser.Dispose();
        _httpTransport.Dispose();
        _coverClient.Dispose();
    }
}
