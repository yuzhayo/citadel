using System.Windows.Controls;
using Citadel.Core.Rpl;
using Module.Proxy.Features.Pool;
using Module.Proxy.Features.Settings;
using Module.Proxy.Features.Sync;
using Module.Proxy.SharedLogic;

namespace Module.Proxy;

/// <summary>Proxy's presentation root; feature behavior belongs in feature folders.</summary>
public partial class ProxyView : UserControl
{
    private readonly ProxySyncCoordinator _coordinator;
    private readonly PoolView _pool;
    private readonly SyncView _sync;
    private bool _disposed;

    internal ProxyView(
        Lifetime lifetime,
        ProxySyncCoordinator coordinator,
        ProxyPoolStore poolStore,
        ProxySettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        ArgumentNullException.ThrowIfNull(poolStore);
        ArgumentNullException.ThrowIfNull(settingsStore);
        InitializeComponent();

        _pool = new PoolView(poolStore);
        _sync = new SyncView(_coordinator);
        var settings = new SettingsView(settingsStore);

        _coordinator.PoolCommitted += Coordinator_PoolCommitted;
        PoolHost.Content = _pool;
        SyncHost.Content = _sync;
        SettingsHost.Content = settings;
        lifetime.Add(DisposeView);
    }

    private void Coordinator_PoolCommitted(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Coordinator_PoolCommitted(sender, e));
            return;
        }
        _pool.Reload();
    }

    private void DisposeView()
    {
        if (_disposed) return;
        _disposed = true;
        _coordinator.PoolCommitted -= Coordinator_PoolCommitted;
        _sync.Dispose();
    }
}
