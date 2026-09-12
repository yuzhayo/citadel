using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Citadel.Core;
using Citadel.Core.Crl;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;
using Citadel.Setting.Components;
using Citadel.Setting;
using Citadel.Ui.Animations;
using Citadel.Ui.Controls;
using Citadel.Ui.Theme;
using WpfSize = System.Windows.Size;

namespace Citadel.Shell;

/// <summary>
/// Sidebar plus content host, and the one place window state drives power
/// saving. Every dimension comes from the token store; nothing is a literal.
///
/// PowerSaving.Set(true) means saving is ON — stop working. That is v0's
/// polarity (PowerSaving.cs) and the flag citizen heartbeats read, so this
/// window drives the application's power-saving state.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Tokens _tokens;
    private readonly ModuleGate _gate;
    private readonly Lifetime _lifetime;
    private readonly ISettingHost? _settingHost;
    private readonly AnimationManager _animations;
    private readonly WindowPlacementStore? _placementStore;
    private readonly DispatcherTimer? _placementSaveTimer;
    private Rect? _startupWorkArea;

    private readonly Dictionary<string, BuiltInRoute> _builtInRoutes;

    internal MainWindow(
        Tokens tokens,
        ModuleGate gate,
        AnimationManager animations,
        Lifetime lifetime,
        IReadOnlyDictionary<string, BuiltInRoute> builtInRoutes,
        ISettingHost? settingHost = null,
        WindowPlacementStore? placementStore = null)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _animations = animations ?? throw new ArgumentNullException(nameof(animations));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _settingHost = settingHost;
        _placementStore = placementStore;
        ArgumentNullException.ThrowIfNull(builtInRoutes);
        _builtInRoutes = builtInRoutes.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);

        var themeResources = new ThemeResources();
        themeResources.Bind(tokens, lifetime);
        Resources.MergedDictionaries.Insert(0, themeResources);

        InitializeComponent();

        if (_placementStore is not null)
        {
            _placementSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _placementSaveTimer.Tick += PlacementSaveTimer_Tick;
            LocationChanged += WindowGeometryChanged;
            SizeChanged += WindowGeometryChanged;
            StateChanged += WindowGeometryChanged;
            Closing += MainWindow_Closing;
            lifetime.Add(() =>
            {
                _placementSaveTimer.Stop();
                _placementSaveTimer.Tick -= PlacementSaveTimer_Tick;
                LocationChanged -= WindowGeometryChanged;
                SizeChanged -= WindowGeometryChanged;
                StateChanged -= WindowGeometryChanged;
                Closing -= MainWindow_Closing;
            });
        }

        ApplyWindowTokens();
        tokens.TokensChanged += ApplyWindowTokens;
        lifetime.Add(() => tokens.TokensChanged -= ApplyWindowTokens);

        Sidebar.Attach(tokens, animations, lifetime);
        Router = new Router(Host, gate, tokens, animations, _builtInRoutes);
        lifetime.Add(Router.Dispose);

        CollapseToggle.Click += OnCollapseClick;
        lifetime.Add(() => CollapseToggle.Click -= OnCollapseClick);

        Sidebar.RouteSelected += OnRouteSelected;
        lifetime.Add(() => Sidebar.RouteSelected -= OnRouteSelected);

        Sidebar.GroupToggled += OnGroupToggled;
        lifetime.Add(() => Sidebar.GroupToggled -= OnGroupToggled);

        if (_settingHost is not null)
        {
            _settingHost.SidebarGroupsChanged += OnSettingHostChanged;
            lifetime.Add(() => _settingHost.SidebarGroupsChanged -= OnSettingHostChanged);
        }

        gate.RegistryChanged += OnRegistryChanged;
        lifetime.Add(() => gate.RegistryChanged -= OnRegistryChanged);

        Router.Navigated += OnNavigated;
        lifetime.Add(() => Router.Navigated -= OnNavigated);

        WirePowerSaving();
        SyncSidebarEntries();
        Router.Navigate(Shell.Router.FallbackRoute);
    }

    public Router Router { get; }

    /// <summary>Test seam: the sidebar the window owns.</summary>
    internal Sidebar SidebarControl => Sidebar;

    internal FrameworkElement AppHeaderElement => AppHeader;

    internal TextBlock AppBrandElement => AppBrand;

    internal RailButton CollapseToggleControl => CollapseToggle;

    internal FrameworkElement ContentCardElement => ContentCard;

    internal ContentControl ContentHostElement => Host;

    internal TextBlock ContentHeaderElement => ContentHeader;

    internal ContentControl ContentHeaderActionElement => ContentHeaderAction;

    private void ApplyWindowTokens()
    {
        var requestedMinWidth = _tokens.Number("WindowMinW");
        var requestedMinHeight = _tokens.Number("WindowMinH");
        MinWidth = _startupWorkArea is { } workArea
            ? Math.Min(requestedMinWidth, workArea.Width)
            : requestedMinWidth;
        MinHeight = _startupWorkArea is { } area
            ? Math.Min(requestedMinHeight, area.Height)
            : requestedMinHeight;

        NativeWindowChromeBehavior.Apply(this, _tokens.Color("BgRail"), _tokens.Color("Fg"));

        // Only the initial size follows the token; a user who resized the
        // window keeps their size, which is why this is not a live binding.
        if (!IsLoaded)
        {
            Width = _tokens.Number("WindowW");
            Height = _tokens.Number("WindowH");
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyStartupBounds();
        NativeWindowChromeBehavior.Apply(this, _tokens.Color("BgRail"), _tokens.Color("Fg"));
    }

    private void ApplyStartupBounds()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var fallbackDpi = VisualTreeHelper.GetDpi(this);
        var (dpiX, dpiY) = MonitorDpi(cursor.X, cursor.Y, fallbackDpi.PixelsPerInchX, fallbackDpi.PixelsPerInchY);
        var pixels = screen.WorkingArea;
        var workArea = WindowBoundsPolicy.PixelsToDips(
            new Rect(pixels.Left, pixels.Top, pixels.Width, pixels.Height),
            dpiX,
            dpiY);
        var bounds = WindowBoundsPolicy.Calculate(
            new WpfSize(_tokens.Number("WindowW"), _tokens.Number("WindowH")),
            new WpfSize(_tokens.Number("WindowMinW"), _tokens.Number("WindowMinH")),
            workArea);

        var saved = _placementStore?.TryLoad();
        if (saved is not null)
        {
            bounds = WindowBoundsPolicy.ClampSaved(
                saved.Bounds,
                new WpfSize(_tokens.Number("WindowMinW"), _tokens.Number("WindowMinH")),
                workArea);
        }

        _startupWorkArea = workArea;
        MinWidth = Math.Min(_tokens.Number("WindowMinW"), workArea.Width);
        MinHeight = Math.Min(_tokens.Number("WindowMinH"), workArea.Height);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (saved?.IsMaximized == true) WindowState = WindowState.Maximized;
    }

    private static (double X, double Y) MonitorDpi(
        int x,
        int y,
        double fallbackX,
        double fallbackY)
    {
        var monitor = MonitorFromPoint(new NativePoint(x, y), MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero
            && GetDpiForMonitor(monitor, MonitorDpiType.Effective, out var dpiX, out var dpiY) == 0
            && dpiX > 0
            && dpiY > 0)
        {
            return (dpiX, dpiY);
        }

        return (fallbackX, fallbackY);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    private enum MonitorDpiType
    {
        Effective = 0,
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

    private void WirePowerSaving()
    {
        // IsVisible is false for a window that has not been loaded yet, so
        // consulting it before load would switch saving ON during construction
        // and back OFF on Show. Once loaded it is the real signal.
        void Refresh() =>
            PowerSaving.Set(WindowState == WindowState.Minimized || (IsLoaded && !IsVisible));

        StateChanged += OnWindowStateChanged;
        IsVisibleChanged += OnWindowVisibilityChanged;
        _lifetime.Add(() =>
        {
            StateChanged -= OnWindowStateChanged;
            IsVisibleChanged -= OnWindowVisibilityChanged;
        });
        Refresh();

        void OnWindowStateChanged(object? sender, EventArgs args) => Refresh();
        void OnWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Refresh();
    }

    private void WindowGeometryChanged(object? sender, EventArgs e)
    {
        if (_placementSaveTimer is null || !IsLoaded) return;
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private void PlacementSaveTimer_Tick(object? sender, EventArgs e)
    {
        _placementSaveTimer?.Stop();
        SaveWindowPlacement();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => SaveWindowPlacement();

    private void SaveWindowPlacement()
    {
        if (_placementStore is null || !IsLoaded) return;
        try
        {
            var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            _placementStore.Save(new WindowPlacement(bounds, WindowState == WindowState.Maximized));
        }
        catch (Exception exception)
        {
            Log.Main("[Shell] window placement save failed: " + exception.Message);
        }
    }

    private void OnRouteSelected(string route) => Router.Navigate(route);

    private void OnGroupToggled(string id)
    {
        if (_settingHost is null) return;
        var group = _settingHost.SidebarGroups()
            .FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (group is not null) _settingHost.SetSidebarGroupExpanded(id, !group.Expanded);
    }

    private void OnSettingHostChanged() => SyncSidebarEntries();

    private void OnCollapseClick(object sender, RoutedEventArgs args) =>
        Sidebar.SetCollapsed(!Sidebar.IsCollapsed);

    private void OnNavigated(string route)
    {
        Sidebar.SelectedRoute = route;
        ContentHeader.Text = ResolveRouteTitle(route);
        RefreshContentHeaderAction();
    }

    private void RefreshContentHeaderAction()
    {
        ContentHeaderAction.Content = null;
        if (Router.CurrentView is not IContentHeaderActionProvider provider) return;

        try
        {
            ContentHeaderAction.Content = provider.CreateContentHeaderAction()
                ?? throw new InvalidOperationException("header action provider returned null");
        }
        catch (Exception exception)
        {
            ContentHeaderAction.Content = null;
            Log.Main($"[Shell] content header action skipped: {exception.Message}");
        }
    }

    private void OnRegistryChanged()
    {
        SyncSidebarEntries();
        Router.OnRegistryChanged();
        Sidebar.SelectedRoute = Router.CurrentRoute;
    }

    /// <summary>
    /// The sidebar mirrors the gate's snapshot; Settings is the control's own
    /// fixed entry, so it is never in this list.
    /// </summary>
    private void SyncSidebarEntries()
    {
        Sidebar.Entries.Clear();
        var descriptors = _gate.Snapshot();
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in _settingHost?.SidebarGroups() ?? [])
        {
            Sidebar.Entries.Add(NavEntry.Group(group.Id, group.Name, group.Expanded));
            foreach (var descriptor in descriptors.Where(item => group.Routes.Contains(item.Route, StringComparer.OrdinalIgnoreCase)))
            {
                if (!assigned.Add(descriptor.Route)) continue;
                if (!group.Expanded) continue;
                Sidebar.Entries.Add(new NavEntry(
                    descriptor.Route,
                    descriptor.Title,
                    descriptor.Icon ?? string.Empty,
                    GroupId: group.Id,
                    IsChild: true));
            }
        }

        foreach (var descriptor in descriptors)
        {
            if (assigned.Contains(descriptor.Route)) continue;
            Sidebar.Entries.Add(new NavEntry(
                descriptor.Route,
                descriptor.Title,
                descriptor.Icon ?? string.Empty));
        }
    }

    private string ResolveRouteTitle(string route)
    {
        if (_builtInRoutes.TryGetValue(route, out var builtIn)) return builtIn.Title;

        var descriptor = _gate.Snapshot()
            .FirstOrDefault(item => string.Equals(item.Route, route, StringComparison.Ordinal));
        if (descriptor is not null) return descriptor.Title;

        return _builtInRoutes.TryGetValue(Router.FallbackRoute, out var fallback)
            ? fallback.Title
            : "Settings";
    }
}
