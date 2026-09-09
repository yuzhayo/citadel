using System.Windows.Controls;
using Module.Mangareader.Features.CatalogMirror;

namespace Module.Mangareader.Features.Catalog;

/// <summary>
/// Route host for the top-level Catalog tab. Like DownloaderView, it only
/// attaches one feature context to one owned child and exposes navigation
/// outward; no provider, database, or queue policy lives in this host.
/// </summary>
public partial class CatalogView : UserControl, IDisposable
{
    private bool _disposed;

    public CatalogView() => InitializeComponent();

    public event EventHandler<CatalogQueueHandoffRequest>? QueueHandoffRequested
    {
        add => CatalogWorkspace.QueueHandoffRequested += value;
        remove => CatalogWorkspace.QueueHandoffRequested -= value;
    }

    public void UseContext(CatalogContext context)
    {
        if (_disposed) return;
        CatalogWorkspace.UseContext(context);
    }

    public void UseQueueTarget(Func<CatalogQueueTargetRequest, string?> confirmFolder)
    {
        if (_disposed) return;
        CatalogWorkspace.UseQueueTarget(confirmFolder);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CatalogWorkspace.Dispose();
    }
}
