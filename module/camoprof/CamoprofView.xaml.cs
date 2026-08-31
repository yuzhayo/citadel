using System.Windows;
using System.Windows.Controls;
using Citadel.Core.Rpl;
using Module.Camoprof.Launcher;
using Module.Camoprof.Network;
using Module.Camoprof.Providers.Google;
using Module.Camoprof.Runtime;
using Module.Camoprof.SharedLogic;

namespace Module.Camoprof;

/// <summary>
/// CamoProf composition root. Feature behaviour belongs to feature folders;
/// this class owns shared lifetime and explicit cross-tab messages.
/// </summary>
public partial class CamoprofView : UserControl
{
    private readonly BrowserSessionCoordinator _sessions;
    private readonly NetworkMonitor _network;
    private readonly LauncherView _launcher;
    private readonly RuntimeView _runtime;
    private bool _loaded;
    private bool _disposed;

    public CamoprofView(Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        InitializeComponent();

        var credentials = new GoogleCredentialStore();
        var catalog = new ProfileCatalog(credentials);
        _sessions = new BrowserSessionCoordinator();
        _network = new NetworkMonitor();
        var google = new GoogleAccountService(_network, credentials, _sessions);
        _launcher = new LauncherView(catalog, _sessions, google, credentials, _network);
        _runtime = new RuntimeView(_sessions);

        LauncherHost.Content = _launcher;
        RuntimeHost.Content = _runtime;

        _runtime.SetupBusyChanged += Runtime_SetupBusyChanged;
        Loaded += CamoprofView_Loaded;
        lifetime.Add(DisposeView);
    }

    private async void CamoprofView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || _disposed)
        {
            return;
        }

        _loaded = true;
        _network.Start();
        await _launcher.RefreshAsync();
    }

    private async void WorkspaceTabs_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_disposed || !ReferenceEquals(e.Source, WorkspaceTabs))
        {
            return;
        }

        if (ReferenceEquals(WorkspaceTabs.SelectedItem, RuntimePanel))
        {
            await _runtime.ActivateAsync();
        }
    }

    private void Runtime_SetupBusyChanged(bool busy)
    {
        if (_disposed)
        {
            return;
        }

        LauncherPanel.IsEnabled = !busy;
        EditorPanel.IsEnabled = !busy;
    }

    private void DisposeView()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Loaded -= CamoprofView_Loaded;
        _runtime.SetupBusyChanged -= Runtime_SetupBusyChanged;
        _launcher.Dispose();
        _network.Dispose();

        // PyHost.Dispose can wait for its graceful shutdown ladder. Keep that
        // wait away from WPF's navigation path, matching the previous screen.
        _ = Task.Run(_sessions.Dispose);
    }
}
