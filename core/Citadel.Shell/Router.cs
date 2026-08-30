using System.Windows;
using System.Windows.Controls;
using Citadel.Core;
using Citadel.Core.Crl;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;
using Citadel.Ui.Animations;

namespace Citadel.Shell;

/// <summary>
/// Route → view, and the owner of the displayed view's lifetime.
///
/// Two route spaces, deliberately separate. Built-in routes come from the
/// composition root as factories and never pass through the gate — the gate
/// rejects them, so they could not arrive that way even if
/// asked. Citizen routes come from the registry. Settings sub-routes have no
/// sidebar entry at all, which is why v0's "routes are nav entries" shape is
/// not reused.
///
/// Nav-away and unregister both destroy the view lifetime and null the field.
/// v0 destroyed without nulling (MainWindow.xaml.cs:208) and relied on
/// Lifetime.Destroy being idempotent; it is, but a stale reference to a dead
/// lifetime is still a trap for the next reader.
///
/// Navigation crossfades old and new layers through the app's one
/// AnimationManager. The old screen's lifetime dies before the
/// visual transition starts, so a fading screen is inert; a second navigation
/// cancels the first transition and removes its stale layer.
///
/// CreateView is wrapped. The searcher isolates load and construction, but
/// CreateView runs *here*, later — so a module that throws, returns null, or
/// returns an already-parented element would take the shell down and break the
/// promise that a broken citizen cannot. On failure the new
/// lifetime dies, the failure is recorded, the citizen is unregistered, and we
/// land on settings. Layout and synchronous host attachment are part of the
/// same guarded transaction, so a half-created view is never shown.
/// </summary>
public sealed class Router : IDisposable
{
    /// <summary>Where a failed or vanished route lands.</summary>
    public const string FallbackRoute = "settings";

    private static readonly TimeSpan NavigationDuration = TimeSpan.FromMilliseconds(180);

    private readonly ContentControl _host;
    private readonly Grid _surface = new();
    private readonly ModuleGate _gate;
    private readonly Tokens _tokens;
    private readonly AnimationManager _animations;
    private readonly Dictionary<string, BuiltInRoute> _builtIn;
    private Lifetime? _viewLifetime;
    private Lifetime? _transitionLifetime;
    private ContentPresenter? _currentLayer;
    private FrameworkElement? _currentView;
    private bool _navigating;
    private bool _disposed;

    internal Router(
        ContentControl host,
        ModuleGate gate,
        Tokens tokens,
        AnimationManager animations,
        IReadOnlyDictionary<string, BuiltInRoute> builtInRoutes)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _animations = animations ?? throw new ArgumentNullException(nameof(animations));
        ArgumentNullException.ThrowIfNull(builtInRoutes);
        _builtIn = builtInRoutes.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        _host.Content = _surface;
    }

    /// <summary>Raised after the displayed route changed, including on fallback.</summary>
    public event Action<string>? Navigated;

    public string? CurrentRoute { get; private set; }

    /// <summary>Alive only while a view is displayed. Test-visible on purpose.</summary>
    internal Lifetime? ViewLifetime => _viewLifetime;

    /// <summary>The actual screen, below the transition layer.</summary>
    internal FrameworkElement? CurrentView => _currentView;

    /// <summary>True while old and new layers share the host.</summary>
    internal bool TransitionActive => _transitionLifetime?.Alive == true;

    /// <summary>
    /// Navigate, rebuilding even for the current route so a late-arriving
    /// citizen can replace a placeholder. v0 needed `_currentRoute = ""` to
    /// defeat its own early-return (MainWindow.xaml.cs:118); no such reset
    /// exists here because there is no early-return to defeat.
    /// </summary>
    public void Navigate(string route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // RejectForFailedView fires RegistryChanged synchronously, and the
        // window forwards that to OnRegistryChanged — which sees the displayed
        // route gone and navigates to the fallback while this call is still
        // unwinding. Without this guard the user sees Settings built twice for
        // one failure; deleting the active citizen uses the same guard.
        if (_navigating) return;
        _navigating = true;
        try
        {
            NavigateCore(route);
        }
        finally
        {
            _navigating = false;
        }
    }

    private void NavigateCore(string route)
    {
        var oldLayer = PrepareForNavigation();

        if (_builtIn.TryGetValue(route, out var builtIn))
        {
            if (TryCreateAndAttachBuiltIn(route, builtIn, out var view, out var layer, out var lifetime))
            {
                Show(route, view, layer, lifetime, oldLayer);
            }
            else if (!string.Equals(route, FallbackRoute, StringComparison.Ordinal))
            {
                NavigateToFallback(oldLayer);
            }
            else
            {
                LeaveEmpty(oldLayer);
            }
            return;
        }

        var descriptor = _gate.Snapshot()
            .FirstOrDefault(d => string.Equals(d.Route, route, StringComparison.Ordinal));
        if (descriptor is null)
        {
            Log.Main($"[Router] no route '{route}'; falling back to {FallbackRoute}");
            NavigateToFallback(oldLayer);
            return;
        }

        if (!TryCreateAndAttachCitizen(
            descriptor, out var citizenView, out var citizenLayer, out var citizenLifetime))
        {
            NavigateToFallback(oldLayer);
            return;
        }

        Show(descriptor.Route, citizenView, citizenLayer, citizenLifetime, oldLayer);
    }

    /// <summary>
    /// Called when the registry changed. Only acts if the displayed citizen went
    /// away — otherwise a sidebar refresh would tear down a healthy view.
    /// </summary>
    public void OnRegistryChanged()
    {
        // A failed CreateView unregisters the citizen, which fires this while
        // Navigate is still unwinding. Navigate's own failure path already lands
        // on the fallback, so acting here too would build Settings twice for one
        // failure.
        if (_navigating) return;
        if (CurrentRoute is null || _builtIn.ContainsKey(CurrentRoute)) return;

        var stillThere = _gate.Snapshot()
            .Any(d => string.Equals(d.Route, CurrentRoute, StringComparison.Ordinal));
        if (stillThere) return;

        Log.Main($"[Router] displayed route '{CurrentRoute}' was unregistered; leaving");
        Navigate(FallbackRoute);
    }

    private bool TryCreateAndAttachCitizen(
        ModuleDescriptor descriptor,
        out FrameworkElement view,
        out ContentPresenter layer,
        out Lifetime lifetime)
    {
        view = null!;
        layer = null!;
        lifetime = new Lifetime();
        FrameworkElement? created = null;
        ContentPresenter? candidateLayer = null;
        try
        {
            created = descriptor.Instance.CreateView(lifetime)
                ?? throw new InvalidOperationException("CreateView returned null");
            if (VisualTreeHelperParent(created) is not null)
            {
                throw new InvalidOperationException("CreateView returned a view that already has a parent");
            }

            LayoutApplier.Attach(created, descriptor.Route, descriptor.Layout, _tokens, lifetime);
            candidateLayer = Attach(created);
            view = created;
            layer = candidateLayer;
            return true;
        }
        catch (Exception exception)
        {
            RemoveCandidate(candidateLayer);
            lifetime.Destroy();
            _gate.RejectForFailedView(descriptor.Route, exception.Message);
            return false;
        }
    }

    private bool TryCreateAndAttachBuiltIn(
        string routeName,
        BuiltInRoute route,
        out FrameworkElement view,
        out ContentPresenter layer,
        out Lifetime lifetime)
    {
        view = null!;
        layer = null!;
        lifetime = new Lifetime();
        ContentPresenter? candidateLayer = null;
        try
        {
            var created = route.CreateView(lifetime)
                ?? throw new InvalidOperationException("built-in route factory returned null");
            if (VisualTreeHelperParent(created) is not null)
            {
                throw new InvalidOperationException("built-in route returned a view that already has a parent");
            }

            LayoutApplier.Attach(created, routeName, route.Layout, _tokens, lifetime);
            candidateLayer = Attach(created);
            view = created;
            layer = candidateLayer;
            return true;
        }
        catch (Exception exception)
        {
            RemoveCandidate(candidateLayer);
            lifetime.Destroy();
            Log.Main($"[Router] built-in route failed: {exception.Message}");
            return false;
        }
    }

    private void NavigateToFallback(ContentPresenter? oldLayer)
    {
        if (_builtIn.TryGetValue(FallbackRoute, out var fallback)
            && TryCreateAndAttachBuiltIn(
                FallbackRoute, fallback, out var view, out var layer, out var lifetime))
        {
            Show(FallbackRoute, view, layer, lifetime, oldLayer);
            return;
        }

        // Only reachable if the composition root forgot or broke the built-in route.
        Log.Main($"[Router] no '{FallbackRoute}' factory; host left empty");
        LeaveEmpty(oldLayer);
    }

    private ContentPresenter Attach(FrameworkElement view)
    {
        var layer = new ContentPresenter();
        try
        {
            layer.Content = view;
            _surface.Children.Add(layer);
            return layer;
        }
        catch
        {
            if (_surface.Children.Contains(layer)) _surface.Children.Remove(layer);
            layer.Content = null;
            throw;
        }
    }

    private void Show(
        string route,
        FrameworkElement view,
        ContentPresenter layer,
        Lifetime lifetime,
        ContentPresenter? oldLayer)
    {
        _viewLifetime = lifetime;
        _currentView = view;
        _currentLayer = layer;
        CurrentRoute = route;
        StartCrossfade(oldLayer, layer);
        Navigated?.Invoke(route);
    }

    private ContentPresenter? PrepareForNavigation()
    {
        CancelTransition();
        var oldLayer = _currentLayer;
        _viewLifetime?.Destroy();
        _viewLifetime = null;
        _currentView = null;
        _currentLayer = null;
        return oldLayer;
    }

    private void StartCrossfade(ContentPresenter? oldLayer, ContentPresenter newLayer)
    {
        if (oldLayer is null || !_surface.Children.Contains(oldLayer)
            || PowerSaving.On(PowerSaving.Flags.Animations))
        {
            RemoveLayer(oldLayer);
            newLayer.Opacity = 1;
            return;
        }

        oldLayer.Opacity = 1;
        newLayer.Opacity = 0;
        var transition = new Lifetime();
        _transitionLifetime = transition;

        try
        {
            var animation = _animations.Create(
                NavigationDuration,
                progress =>
                {
                    if (!ReferenceEquals(_transitionLifetime, transition)) return;
                    oldLayer.Opacity = 1 - progress;
                    newLayer.Opacity = progress;
                },
                Easings.EaseOutCubic,
                () => FinishTransition(transition, oldLayer, newLayer));

            if (!animation.Start(transition))
            {
                FinishTransition(transition, oldLayer, newLayer);
            }
        }
        catch (Exception exception)
        {
            Log.Main($"[Router] navigation animation skipped: {exception.Message}");
            FinishTransition(transition, oldLayer, newLayer);
        }
    }

    private void FinishTransition(
        Lifetime transition,
        ContentPresenter oldLayer,
        ContentPresenter newLayer)
    {
        if (!ReferenceEquals(_transitionLifetime, transition)) return;
        _transitionLifetime = null;
        RemoveLayer(oldLayer);
        newLayer.Opacity = 1;
        transition.Destroy();
    }

    private void CancelTransition()
    {
        var transition = _transitionLifetime;
        _transitionLifetime = null;
        transition?.Destroy();

        if (_currentLayer is null)
        {
            ClearSurface();
            return;
        }

        _currentLayer.Opacity = 1;
        for (var index = _surface.Children.Count - 1; index >= 0; index--)
        {
            if (_surface.Children[index] is ContentPresenter layer
                && !ReferenceEquals(layer, _currentLayer))
            {
                RemoveLayer(layer);
            }
        }
    }

    private void LeaveEmpty(ContentPresenter? oldLayer)
    {
        RemoveLayer(oldLayer);
        CurrentRoute = null;
        _currentView = null;
        _currentLayer = null;
        _viewLifetime = null;
    }

    private void RemoveCandidate(ContentPresenter? layer)
    {
        if (layer is null) return;
        RemoveLayer(layer);
    }

    private void RemoveLayer(ContentPresenter? layer)
    {
        if (layer is null) return;
        if (_surface.Children.Contains(layer)) _surface.Children.Remove(layer);
        layer.Content = null;
    }

    private void ClearSurface()
    {
        foreach (var layer in _surface.Children.OfType<ContentPresenter>().ToList())
        {
            RemoveLayer(layer);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelTransition();
        _viewLifetime?.Destroy();
        _viewLifetime = null;
        _currentView = null;
        _currentLayer = null;
        CurrentRoute = null;
        ClearSurface();
    }

    private static DependencyObject? VisualTreeHelperParent(FrameworkElement element) =>
        element.Parent ?? System.Windows.Media.VisualTreeHelper.GetParent(element);
}
