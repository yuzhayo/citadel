using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Citadel.Setting.Components;

/// <summary>
/// Optional contract for a feature-owned control whose preference can be
/// represented as one short string without exposing feature semantics here.
/// </summary>
public interface IUiPreferenceControl
{
    event EventHandler? PreferenceChanged;

    string CapturePreference();

    void RestorePreference(string value);
}

/// <summary>
/// Opt-in persistence for durable UI choices. A stable key is mandatory so the
/// screen-blind shared component never guesses ownership or mixes two controls.
/// </summary>
public static class UiPreference
{
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached(
            "Key",
            typeof(string),
            typeof(UiPreference),
            new FrameworkPropertyMetadata(null, OnKeyChanged));

    private static readonly ConditionalWeakTable<FrameworkElement, PreferenceState> States = new();
    private static UiPreferenceStore _store = new();

    public static string? GetKey(DependencyObject target) =>
        (string?)target.GetValue(KeyProperty);

    public static void SetKey(DependencyObject target, string? value) =>
        target.SetValue(KeyProperty, value);

    internal static IDisposable OverrideStoreForTesting(string path)
    {
        var previous = _store;
        _store = new UiPreferenceStore(path);
        return new RestoreStore(previous);
    }

    internal static void Restore(FrameworkElement element)
    {
        var key = GetKey(element);
        if (string.IsNullOrWhiteSpace(key)) return;
        var value = _store.Read(key);
        if (value is null) return;

        var state = States.GetOrCreateValue(element);
        state.Restoring = true;
        try
        {
            switch (element)
            {
                case SettingTable table:
                    table.RestorePreference(value);
                    break;
                case Selector selector when int.TryParse(
                    value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index):
                    if (index >= -1 && index < selector.Items.Count) selector.SelectedIndex = index;
                    break;
                case ToggleButton toggle when bool.TryParse(value, out var isChecked):
                    toggle.IsChecked = isChecked;
                    break;
                case SettingField field:
                    field.Text = value;
                    break;
                case IUiPreferenceControl custom:
                    custom.RestorePreference(value);
                    break;
            }
        }
        finally
        {
            state.Restoring = false;
        }
    }

    private static void OnKeyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        Detach(element);
        if (string.IsNullOrWhiteSpace(args.NewValue as string)) return;

        var state = States.GetOrCreateValue(element);
        element.Loaded += Element_Loaded;
        element.Unloaded += Element_Unloaded;
        if (element is Selector selector) selector.SelectionChanged += Selector_SelectionChanged;
        if (element is ToggleButton toggle)
        {
            toggle.Checked += Toggle_Changed;
            toggle.Unchecked += Toggle_Changed;
        }
        if (element is SettingField field)
        {
            state.FieldChanged = _ => SaveIfLive(element);
            field.TextChanged += state.FieldChanged;
        }
        if (element is IUiPreferenceControl custom) custom.PreferenceChanged += Custom_Changed;

        if (element.IsLoaded) Restore(element);
    }

    private static void Detach(FrameworkElement element)
    {
        element.Loaded -= Element_Loaded;
        element.Unloaded -= Element_Unloaded;
        if (element is Selector selector) selector.SelectionChanged -= Selector_SelectionChanged;
        if (element is ToggleButton toggle)
        {
            toggle.Checked -= Toggle_Changed;
            toggle.Unchecked -= Toggle_Changed;
        }
        if (element is SettingField field
            && States.TryGetValue(element, out var state)
            && state.FieldChanged is not null)
        {
            field.TextChanged -= state.FieldChanged;
        }
        if (element is IUiPreferenceControl custom) custom.PreferenceChanged -= Custom_Changed;
        States.Remove(element);
    }

    private static void Element_Loaded(object sender, RoutedEventArgs args) =>
        Restore((FrameworkElement)sender);

    private static void Element_Unloaded(object sender, RoutedEventArgs args) =>
        Save((FrameworkElement)sender);

    private static void Selector_SelectionChanged(object sender, SelectionChangedEventArgs args) =>
        SaveIfLive((FrameworkElement)sender);

    private static void Toggle_Changed(object sender, RoutedEventArgs args) =>
        SaveIfLive((FrameworkElement)sender);

    private static void Custom_Changed(object? sender, EventArgs args)
    {
        if (sender is FrameworkElement element) SaveIfLive(element);
    }

    private static void SaveIfLive(FrameworkElement element)
    {
        if (element.IsLoaded) Save(element);
    }

    private static void Save(FrameworkElement element)
    {
        var key = GetKey(element);
        if (string.IsNullOrWhiteSpace(key)) return;
        if (States.TryGetValue(element, out var state) && state.Restoring) return;

        string? value = element switch
        {
            SettingTable table => table.CapturePreference(),
            Selector selector => selector.SelectedIndex.ToString(CultureInfo.InvariantCulture),
            ToggleButton toggle => (toggle.IsChecked == true).ToString(CultureInfo.InvariantCulture),
            SettingField field => field.Text,
            IUiPreferenceControl custom => custom.CapturePreference(),
            _ => null,
        };
        if (value is not null) _store.Write(key, value);
    }

    private sealed class PreferenceState
    {
        public bool Restoring { get; set; }

        public Action<string>? FieldChanged { get; set; }
    }

    private sealed class RestoreStore(UiPreferenceStore previous) : IDisposable
    {
        private UiPreferenceStore? _previous = previous;

        public void Dispose()
        {
            var previousStore = Interlocked.Exchange(ref _previous, null);
            if (previousStore is not null) _store = previousStore;
        }
    }
}
