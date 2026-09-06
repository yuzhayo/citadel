using System.Windows;
using System.Windows.Controls;
using Module.Mangareader.Features.Downloader.AutoCover;

namespace Module.Mangareader.Features.Downloader;

/// <summary>
/// The Downloader's route host. It owns routing and child lifetime only — no
/// filter, job, provider or transport state lives here, and hiding a screen
/// never disposes it.
/// </summary>
public partial class DownloaderView : UserControl, IDisposable
{
    private DownloaderContext? _context;

    /// <summary>
    /// The lifetime of this route host, and the only token the automatic cover
    /// invocation gets. Without it an automatic fetch kept running after the
    /// Downloader was closed, with no owner left and nothing to stop it writing.
    /// The manual Fetch Cover below the remote cover owns its own cancellation in
    /// the screen that hosts the button.
    /// </summary>
    private CancellationTokenSource? _lifetime = new();
    private bool _disposed;

    public DownloaderView() => InitializeComponent();

    /// <summary>
    /// Attaches the module-lifetime context to both children. Called by the
    /// MangaReader composition before this view is loaded.
    /// </summary>
    public void UseContext(DownloaderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_disposed || ReferenceEquals(_context, context)) return;

        _context = context;
        CatalogScreen.UseContext(context);
        DownloadListScreen.UseContext(context);
        CatalogScreen.OpenDownloadList += CatalogScreen_OpenDownloadList;
        CatalogScreen.CoverCandidateAvailable += CatalogScreen_CoverCandidateAvailable;
        DownloadListScreen.BackRequested += DownloadListScreen_BackRequested;
        Navigate(DownloaderRoute.Catalog);
    }

    public DownloaderRoute Route { get; private set; } = DownloaderRoute.Catalog;

    private void CatalogScreen_OpenDownloadList(object? sender, EventArgs e) =>
        Navigate(DownloaderRoute.DownloadList);

    /// <summary>
    /// Relays one immutable cover candidate to Auto Cover. This host decides
    /// nothing: Auto Cover owns whether a cover is written, and the queue never
    /// waits for it. A failure stays here and can never change a chapter job.
    /// </summary>
    private async void CatalogScreen_CoverCandidateAvailable(object? sender, CoverCandidate candidate)
    {
        if (_disposed || _context is null || _lifetime is null) return;

        // Snapshotted before the first await, so a disposal during the fetch cannot
        // leave this handler reading a field its host already cleared.
        var autoCover = _context.AutoCover;
        var lifetime = _lifetime;

        try
        {
            await autoCover.SaveCoverAsync(
                candidate,
                AutoCoverTrigger.Automatic,
                confirmOverwrite: null,
                lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // This host's lifetime ended while the cover was in flight. That is a
            // normal shutdown and there is no surface left to report it to.
        }
        catch (Exception)
        {
            // Auto Cover is advisory. Losing one cover must not surface as a
            // routing failure or touch the queue.
        }
    }

    private void DownloadListScreen_BackRequested(object? sender, EventArgs e) =>
        Navigate(DownloaderRoute.Catalog);

    private void Navigate(DownloaderRoute route)
    {
        if (_disposed) return;
        Route = route;

        // Visibility, not reconstruction: Catalog keeps its provider, filters,
        // result page, selection and scroll anchor, and the queue keeps running.
        var catalog = route == DownloaderRoute.Catalog;
        CatalogScreen.Visibility = catalog ? Visibility.Visible : Visibility.Collapsed;
        DownloadListScreen.Visibility = catalog ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CatalogScreen.OpenDownloadList -= CatalogScreen_OpenDownloadList;
        CatalogScreen.CoverCandidateAvailable -= CatalogScreen_CoverCandidateAvailable;
        DownloadListScreen.BackRequested -= DownloadListScreen_BackRequested;

        // Take the field, clear it, then cancel and dispose: an automatic cover
        // still in flight must stop with its host, and nothing afterwards can hand
        // out a token from a disposed source.
        var lifetime = _lifetime;
        _lifetime = null;
        lifetime?.Cancel();
        lifetime?.Dispose();

        CatalogScreen.Dispose();
        DownloadListScreen.Dispose();
        _context = null;
    }
}
