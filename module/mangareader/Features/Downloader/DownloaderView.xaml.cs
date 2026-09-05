using System.Windows;
using System.Windows.Controls;

namespace Module.Mangareader.Features.Downloader;

/// <summary>
/// The Downloader's route host. It owns routing and child lifetime only — no
/// filter, job, provider or transport state lives here, and hiding a screen
/// never disposes it.
/// </summary>
public partial class DownloaderView : UserControl, IDisposable
{
    private DownloaderContext? _context;
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
        DownloadListScreen.BackRequested += DownloadListScreen_BackRequested;
        Navigate(DownloaderRoute.Catalog);
    }

    public DownloaderRoute Route { get; private set; } = DownloaderRoute.Catalog;

    private void CatalogScreen_OpenDownloadList(object? sender, EventArgs e) =>
        Navigate(DownloaderRoute.DownloadList);

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
        DownloadListScreen.BackRequested -= DownloadListScreen_BackRequested;
        CatalogScreen.Dispose();
        DownloadListScreen.Dispose();
        _context = null;
    }
}
