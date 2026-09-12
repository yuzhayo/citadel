using Module.Mangareader.Features.Catalog.Runtime;
using Module.Mangareader.Features.CatalogMirror;
using Module.Mangareader.Sources;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Features.Catalog;

/// <summary>
/// Complete runtime boundary for the top-level Catalog tab. Its process,
/// browser profile, provider directory, sync/database coordinator, and
/// cancellation lifetime are separate from Downloader.
/// </summary>
public sealed class CatalogContext : IDisposable
{
    private int _disposed;

    internal CatalogContext(
        CatalogMirrorFeature feature,
        CatalogBrowserClient browser,
        IMangaSourceDirectory sources,
        ProxyPoolAdapter proxyPool,
        ProxyHttpTransport httpTransport)
    {
        Feature = feature ?? throw new ArgumentNullException(nameof(feature));
        Browser = browser ?? throw new ArgumentNullException(nameof(browser));
        Sources = sources ?? throw new ArgumentNullException(nameof(sources));
        ProxyPool = proxyPool ?? throw new ArgumentNullException(nameof(proxyPool));
        HttpTransport = httpTransport ?? throw new ArgumentNullException(nameof(httpTransport));
    }

    public CatalogMirrorFeature Feature { get; }

    public CatalogBrowserClient Browser { get; }

    public IMangaSourceDirectory Sources { get; }

    internal ProxyPoolAdapter ProxyPool { get; }

    internal ProxyHttpTransport HttpTransport { get; }

    public async Task StartOnlineAsync(bool showBrowser, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Browser.ShowBrowser = showBrowser;
        await Browser.EnsureSessionAsync(
            "comix",
            "https://comix.ws/browse",
            headless: !showBrowser,
            cancellationToken).ConfigureAwait(false);
    }

    public void StopOnline()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Feature.RequestStopSync();
        Feature.RequestStopGenreSync();
        Browser.AbortSession();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Feature.RequestStopSync();
        Feature.RequestStopGenreSync();
        Feature.Dispose();
        Browser.Dispose();
    }
}
