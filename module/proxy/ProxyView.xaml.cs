using System.Windows.Controls;
using Citadel.Core.Rpl;
using Module.Proxy.Features.Pool;
using Module.Proxy.Features.Settings;
using Module.Proxy.Features.Sync;
using Module.Proxy.Features.Webshare;
using Module.Proxy.SharedLogic;

namespace Module.Proxy;

/// <summary>Proxy's presentation root; feature behavior belongs in feature folders.</summary>
public partial class ProxyView : UserControl
{
    private readonly ProxySyncCoordinator _coordinator;
    private readonly PoolHealthCoordinator _healthCoordinator;
    private readonly PoolView _pool;
    private readonly SyncView _sync;
    private readonly WebshareCoordinator _webshareCoordinator;
    private readonly WebshareView _webshare;
    private bool _disposed;

    internal ProxyView(
        Lifetime lifetime,
        ProxySyncCoordinator coordinator,
        PoolHealthCoordinator healthCoordinator,
        WebshareCoordinator webshareCoordinator,
        ProxyPoolStore poolStore,
        ProxySettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _healthCoordinator = healthCoordinator ?? throw new ArgumentNullException(nameof(healthCoordinator));
        _webshareCoordinator = webshareCoordinator ?? throw new ArgumentNullException(nameof(webshareCoordinator));
        ArgumentNullException.ThrowIfNull(poolStore);
        ArgumentNullException.ThrowIfNull(settingsStore);
        InitializeComponent();

        _pool = new PoolView(poolStore, _healthCoordinator);
        _sync = new SyncView(_coordinator);
        _webshare = new WebshareView(_webshareCoordinator);
        var settings = new SettingsView(settingsStore);

        _coordinator.PoolCommitted += Coordinator_PoolCommitted;
        _webshareCoordinator.PoolCommitted += Coordinator_PoolCommitted;
        PoolHost.Content = _pool;
        SyncHost.Content = _sync;
        WebshareHost.Content = _webshare;
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
        _webshareCoordinator.PoolCommitted -= Coordinator_PoolCommitted;
        _pool.Dispose();
        _sync.Dispose();
        _webshare.Dispose();
    }
}
