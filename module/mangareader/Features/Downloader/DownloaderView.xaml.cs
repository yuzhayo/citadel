using System.Windows.Controls;
using Module.Mangareader.Features.Downloader.AutoCover;
using Module.Mangareader.Features.Downloader.ManualUrl;

namespace Module.Mangareader.Features.Downloader;

/// <summary>
/// The Downloader's route host. It owns the online Catalog child and its
/// lifetime only — no filter, job, provider or transport state lives here.
/// Queue navigation is relayed to the MangaReader parent, which owns the
/// top-level tabs.
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
    /// Attaches the application-lifetime context to the Catalog child. Called by the
    /// MangaReader composition before this view is loaded.
    /// </summary>
    public void UseContext(DownloaderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_disposed || ReferenceEquals(_context, context)) return;

        _context = context;
        CatalogScreen.UseContext(context);
        ManualUrlScreen.UseContext(context);
        CatalogScreen.OpenDownloadList += CatalogScreen_OpenDownloadList;
        CatalogScreen.CoverCandidateAvailable += CatalogScreen_CoverCandidateAvailable;
        CatalogScreen.OpenManualUrl += CatalogScreen_OpenManualUrl;
        CatalogScreen.ReturnToManualUrl += CatalogScreen_ReturnToManualUrl;
        ManualUrlScreen.BackRequested += ManualUrlScreen_BackRequested;
        ManualUrlScreen.TitleResolved += ManualUrlScreen_TitleResolved;
    }

    /// <summary>
    /// The Catalog asked for the standalone Download Queue tab. This host keeps
    /// no queue screen itself; the MangaReader parent selects that tab.
    /// </summary>
    public event EventHandler? OpenDownloadQueue;

    private void CatalogScreen_OpenDownloadList(object? sender, EventArgs e) =>
        OpenDownloadQueue?.Invoke(this, EventArgs.Empty);

    private void CatalogScreen_OpenManualUrl(object? sender, EventArgs e) => ShowManualUrl();

    private void CatalogScreen_ReturnToManualUrl(object? sender, EventArgs e) => ShowManualUrl();

    private void ManualUrlScreen_BackRequested(object? sender, EventArgs e) => ShowCatalog();

    private async void ManualUrlScreen_TitleResolved(object? sender, ManualUrlResolution resolution)
    {
        if (_disposed) return;
        var failure = await CatalogScreen.OpenManualTitleAsync(resolution);
        if (_disposed) return;

        if (failure is null)
        {
            ShowCatalog();
        }
        else
        {
            ManualUrlScreen.ReportError(failure);
        }
    }

    private void ShowManualUrl()
    {
        CatalogScreen.Visibility = System.Windows.Visibility.Collapsed;
        ManualUrlScreen.Visibility = System.Windows.Visibility.Visible;
    }

    private void ShowCatalog()
    {
        ManualUrlScreen.Visibility = System.Windows.Visibility.Collapsed;
        CatalogScreen.Visibility = System.Windows.Visibility.Visible;
    }

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CatalogScreen.OpenDownloadList -= CatalogScreen_OpenDownloadList;
        CatalogScreen.CoverCandidateAvailable -= CatalogScreen_CoverCandidateAvailable;
        CatalogScreen.OpenManualUrl -= CatalogScreen_OpenManualUrl;
        CatalogScreen.ReturnToManualUrl -= CatalogScreen_ReturnToManualUrl;
        ManualUrlScreen.BackRequested -= ManualUrlScreen_BackRequested;
        ManualUrlScreen.TitleResolved -= ManualUrlScreen_TitleResolved;

        // Take the field, clear it, then cancel and dispose: an automatic cover
        // still in flight must stop with its host, and nothing afterwards can hand
        // out a token from a disposed source.
        var lifetime = _lifetime;
        _lifetime = null;
        lifetime?.Cancel();
        lifetime?.Dispose();

        CatalogScreen.Dispose();
        ManualUrlScreen.Dispose();
        _context = null;
    }
}
