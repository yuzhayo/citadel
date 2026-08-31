using System.Windows;
using System.Windows.Controls;
using Citadel.Core.Rpl;
using Module.Camoprof.Editor;
using Module.Camoprof.Launcher;
using Module.Camoprof.Runtime;
using Module.Camoprof.SharedLogic;

namespace Module.Camoprof;

/// <summary>
/// CamoProf composition root. Feature behaviour belongs to the three tab
/// folders; this class owns shared lifetime and explicit cross-tab messages.
/// </summary>
public partial class CamoprofView : UserControl
{
    private readonly BrowserSessionCoordinator _sessions;
    private readonly LauncherView _launcher;
    private readonly EditorView _editor;
    private readonly RuntimeView _runtime;
    private bool _loaded;
    private bool _editorActivated;
    private bool _disposed;

    public CamoprofView(Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        InitializeComponent();

        var catalog = new ProfileCatalog();
        _sessions = new BrowserSessionCoordinator();
        _launcher = new LauncherView(catalog, _sessions);
        _editor = new EditorView(catalog, _sessions);
        _runtime = new RuntimeView(_sessions);

        LauncherHost.Content = _launcher;
        EditorHost.Content = _editor;
        RuntimeHost.Content = _runtime;

        _editor.ProfilesChanged += Editor_ProfilesChanged;
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

        if (ReferenceEquals(WorkspaceTabs.SelectedItem, EditorPanel)
            && !_editorActivated)
        {
            _editorActivated = true;
            await _editor.ActivateAsync();
        }
        else if (ReferenceEquals(WorkspaceTabs.SelectedItem, RuntimePanel))
        {
            await _runtime.ActivateAsync();
        }
    }

    private async void Editor_ProfilesChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            await _launcher.RefreshAsync();
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
        _editor.ProfilesChanged -= Editor_ProfilesChanged;
        _runtime.SetupBusyChanged -= Runtime_SetupBusyChanged;
        _launcher.Dispose();

        // PyHost.Dispose can wait for its graceful shutdown ladder. Keep that
        // wait away from WPF's navigation path, matching the previous screen.
        _ = Task.Run(_sessions.Dispose);
    }
}
