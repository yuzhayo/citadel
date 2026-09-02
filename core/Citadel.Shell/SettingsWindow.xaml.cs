using System.Windows;
using System.Windows.Controls;
using Citadel.Core;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;
using Citadel.Setting.Components;
using Citadel.Ui.Theme;

namespace Citadel.Shell;

/// <summary>
/// One reusable, modeless editor window for Settings' three sub-screens.
///
/// Its ThemeResources intentionally stay at defaults instead of binding to the
/// target token store. Appearance can therefore resize/recolour the main shell
/// without moving the slider under the pointer or restyling its own editor.
/// </summary>
internal partial class SettingsWindow : Window
{
    private Lifetime? _viewLifetime;
    private bool _closed;

    public SettingsWindow(Window owner)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Resources.MergedDictionaries.Add(new ThemeResources());
        InitializeComponent();
    }

    internal string? CurrentRoute { get; private set; }

    internal FrameworkElement? CurrentView => PopupHost.Content as FrameworkElement;

    internal Lifetime? ViewLifetime => _viewLifetime;

    internal string RouteTitle => PopupTitle.Text;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowChromeBehavior.Apply(
            this,
            Defaults.All["BgRail"].Argb,
            Defaults.All["Fg"].Argb);
    }

    internal bool TryShowRoute(string route, BuiltInRoute definition)
    {
        if (_closed) return false;
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(definition);

        if (string.Equals(CurrentRoute, route, StringComparison.Ordinal)
            && CurrentView is not null)
        {
            return true;
        }

        var lifetime = new Lifetime();
        FrameworkElement created;
        try
        {
            created = definition.CreateView(lifetime)
                ?? throw new InvalidOperationException("settings factory returned null");
            if (LogicalTreeHelper.GetParent(created) is not null)
            {
                throw new InvalidOperationException("settings factory returned an attached view");
            }
        }
        catch (Exception exception)
        {
            lifetime.Destroy();
            Log.Main($"[SettingsWindow] '{route}' failed: {exception.Message}");
            return false;
        }

        var oldLifetime = _viewLifetime;
        PopupHost.Content = created;
        _viewLifetime = lifetime;
        CurrentRoute = route;
        PopupTitle.Text = definition.Title;
        oldLifetime?.Destroy();
        return true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        PopupHost.Content = null;
        var lifetime = _viewLifetime;
        _viewLifetime = null;
        CurrentRoute = null;
        lifetime?.Destroy();
        base.OnClosed(e);
    }
}
