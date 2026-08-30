using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citadel.Setting.Components;
using Citadel.Setting.LayoutEditor;
using TokensEngine = Citadel.Core.Tokens.Tokens;

namespace Citadel.Setting.Screens;

/// <summary>
/// The route `settings/layout`: picks a screen and generates the editor for its
/// declared slots.
///
/// It reads declarations from the host seam and never touches the filesystem,
/// so it can work against either a live or fake descriptor.
///
/// **Settings' own layout is editable too**, keyed by route `settings`. That is
/// not a contradiction with the gate rejecting
/// `settings` as a registration: the layout store and the gate's route space are
/// different namespaces.
/// </summary>
public sealed class ModuleLayoutScreen : SettingScreen
{
    private readonly ISettingHost _host;
    private readonly TokensEngine _tokens;
    private readonly LayoutDeclaration? _ownDeclaration;
    private readonly ComboBox _routes = new();
    private readonly Border _editorHost;
    private readonly ContentControl _editor = new();
    private readonly TextBlock _status = Body(string.Empty);

    public ModuleLayoutScreen(
        ISettingHost host,
        TokensEngine tokens,
        LayoutDeclaration? ownDeclaration,
        Lifetime lifetime)
        : base(lifetime)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _ownDeclaration = ownDeclaration;

        AutomationProperties.SetAutomationId(this, "ModuleLayoutScreen");
        AutomationProperties.SetAutomationId(_routes, "LayoutRoutes");
        AutomationProperties.SetAutomationId(_status, "LayoutStatus");
        _routes.SetResourceReference(FrameworkElement.StyleProperty, "SettingComboBoxStyle");

        _editorHost = Card(_editor, "LayoutEditorCard");

        Add(Section("SCREEN"));
        Add(Card(Stack(
            Body("Every screen declares which parts of it can be moved, resized, or hidden."),
            Row(_routes, Action("Reset this screen", "ResetLayout", ResetCurrent))),
            "LayoutPickerCard"));

        Add(Section("SLOTS"));
        Add(_editorHost);
        Add(Card(_status, "LayoutStatusCard"));

        _routes.SelectionChanged += (_, _) => Rebuild();
        RefreshRoutes();
        _host.Changed += RefreshRoutes;
        Lifetime.Add(() => _host.Changed -= RefreshRoutes);
    }

    /// <summary>Test seam: the generated editor's root.</summary>
    internal FrameworkElement? Editor => _editor.Content as FrameworkElement;

    internal string SelectedRoute => (string?)_routes.SelectedItem ?? string.Empty;

    internal string Status => _status.Text;

    /// <summary>Test seam: the field generated for one slot property.</summary>
    internal SettingField? Find(string slot, string property) =>
        Descendants<SettingField>(_editor)
            .FirstOrDefault(field =>
                AutomationProperties.GetAutomationId(field) == $"{slot}:{property}");

    /// <summary>Test seam: the toggle generated for a visibility slot.</summary>
    internal SettingToggle? FindToggle(string slot) =>
        Descendants<SettingToggle>(_editor)
            .FirstOrDefault(toggle =>
                AutomationProperties.GetAutomationId(toggle) == $"{slot}:visible");

    /// <summary>Test seam: type into a generated field, as a user would.</summary>
    internal void SetNumber(string slot, string property, double value)
    {
        var field = Find(slot, property)
            ?? throw new InvalidOperationException($"no editor for {slot}:{property}");
        field.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in ChildrenOf(root))
        {
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static IEnumerable<DependencyObject> ChildrenOf(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject node) yield return node;
        }
    }

    internal void Select(string route)
    {
        if (!_routes.Items.Contains(route)) return;
        _routes.SelectedItem = route;
    }

    private void RefreshRoutes()
    {
        var previous = SelectedRoute;
        var routes = new List<string>();

        // Settings edits itself like any other screen, so its route is offered
        // first rather than being a special case elsewhere.
        if (_ownDeclaration is not null) routes.Add(SettingsRoute);
        routes.AddRange(_host.Screens()
            .Where(descriptor => descriptor.Layout is not null)
            .Select(descriptor => descriptor.Route));

        _routes.ItemsSource = routes;
        _routes.SelectedItem = routes.Contains(previous, StringComparer.Ordinal)
            ? previous
            : routes.FirstOrDefault();
        Rebuild();
    }

    private void Rebuild()
    {
        var route = SelectedRoute;
        if (route.Length == 0)
        {
            _editor.Content = Body("No screen declares an editable layout yet.");
            _status.Text = string.Empty;
            return;
        }

        var declaration = DeclarationFor(route);
        if (declaration is null)
        {
            _editor.Content = Body($"'{route}' no longer declares a layout.");
            _status.Text = string.Empty;
            return;
        }

        _editor.Content = EditorBuilder.Build(route, declaration, _tokens, PersistEdit);
        _status.Text = $"Editing '{route}'. Changes are saved as overrides for this screen only.";
    }

    private LayoutDeclaration? DeclarationFor(string route)
    {
        if (string.Equals(route, SettingsRoute, StringComparison.Ordinal)) return _ownDeclaration;

        return _host.Screens()
            .FirstOrDefault(descriptor => string.Equals(descriptor.Route, route, StringComparison.Ordinal))
            ?.Layout;
    }

    private void ResetCurrent()
    {
        var route = SelectedRoute;
        if (route.Length == 0) return;

        _tokens.ResetLayout(route);
        Rebuild();
        _status.Text = _tokens.TrySave(out var error)
            ? $"'{route}' back to its declared layout."
            : $"'{route}' reset but NOT saved: {error}";
    }

    private void PersistEdit(LayoutEditResult edit)
    {
        if (!edit.Applied)
        {
            _status.Text = edit.Warning ?? "The layout edit was not applied.";
            return;
        }

        var saved = _tokens.TrySave(out var error);
        var correction = edit.Warning is null ? string.Empty : edit.Warning + " ";
        _status.Text = saved
            ? correction + "Saved."
            : correction + $"Applied but NOT saved: {error}";
    }

    /// <summary>The store key for Settings' own layout.</summary>
    internal const string SettingsRoute = "settings";
}
