using Module.Mangareader.Features.Catalog.Runtime;
using Module.Mangareader.Features.CatalogMirror;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Catalog;

/// <summary>
/// Complete runtime boundary for the top-level Catalog tab. Its process,
/// browser profile, provider directory, sync/database coordinator, and
/// cancellation lifetime are separate from Downloader.
/// </summary>
public sealed class CatalogContext(
    CatalogMirrorFeature feature,
    CatalogBrowserClient browser,
    IMangaSourceDirectory sources) : IDisposable
{
    private int _disposed;

    public CatalogMirrorFeature Feature { get; } =
        feature ?? throw new ArgumentNullException(nameof(feature));

    public CatalogBrowserClient Browser { get; } =
        browser ?? throw new ArgumentNullException(nameof(browser));

    public IMangaSourceDirectory Sources { get; } =
        sources ?? throw new ArgumentNullException(nameof(sources));

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
