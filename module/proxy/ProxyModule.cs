using System.Net.Http;
using System.Windows;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Module.Proxy.Features.Sync;
using Module.Proxy.Features.Pool;
using Module.Proxy.Features.Webshare;
using Module.Proxy.SharedLogic;

namespace Module.Proxy;

/// <summary>The shell entry point for the Proxy citizen.</summary>
public sealed class ProxyModule : IModule
{
    // Loader retains one module instance for the application process. Keep the
    // background sync runtime here so rebuilding a routed view cannot cancel it.
    private readonly HttpClient _http = new();
    private readonly ProxyPoolStore _poolStore = new();
    private readonly ProxySettingsStore _settingsStore = new();
    private readonly ProxySyncCoordinator _coordinator;
    private readonly PoolHealthCoordinator _healthCoordinator;
    private readonly WebshareCoordinator _webshareCoordinator;

    public ProxyModule()
    {
        var probe = new ProxyReachabilityProbe();
        var service = new ProxySyncService(
            new HttpProxySourceFetcher(_http),
            probe);
        _coordinator = new ProxySyncCoordinator(service, _poolStore, _settingsStore);
        _healthCoordinator = new PoolHealthCoordinator(_poolStore, _settingsStore, probe);
        _webshareCoordinator = new WebshareCoordinator(
            new WebshareImportService(new HttpWebshareApiClient(_http), probe),
            new WebshareCredentialStore(),
            _poolStore,
            _settingsStore);
    }

    public string Route => "proxy";

    public FrameworkElement CreateView(Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        return new ProxyView(lifetime, _coordinator, _healthCoordinator, _webshareCoordinator, _poolStore, _settingsStore);
    }
}
