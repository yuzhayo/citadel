using System.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Module.Mangareader.Features.Downloader.Sources.Comix;

/// <summary>
/// One checkable provider option. The key is the provider's own option id and is
/// the only part that may enter a browse query; the display name is what the user
/// reads and what an accessible name is built from.
/// </summary>
public sealed class ComixCheckOption : INotifyPropertyChanged
{
    private bool _isChecked;

    public ComixCheckOption(string key, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Key = key;
        DisplayName = displayName;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => DisplayName;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// The compact multi-select dropdown used by the Comix filter feature. It owns
/// draft selection state and nothing else: it never issues a request, never
/// validates a range, and never persists a choice.
///
/// Composition contract: the shared button keeps its hover, press, focus and
/// disabled behavior; the popup keeps its own outside-click dismissal; the shared
/// scroll viewer keeps the auto-fade behavior. This control adds only the summary
/// label and the checked-key projection.
/// </summary>
public partial class ComixMultiSelectDropDown : UserControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header),
            typeof(string),
            typeof(ComixMultiSelectDropDown),
            new FrameworkPropertyMetadata(string.Empty, OnHeaderChanged));

    public static readonly DependencyProperty OptionsProperty =
        DependencyProperty.Register(
            nameof(Options),
            typeof(IEnumerable),
            typeof(ComixMultiSelectDropDown),
            new FrameworkPropertyMetadata(null, OnOptionsChanged));

    private readonly TextBlock _summary = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public ComixMultiSelectDropDown()
    {
        InitializeComponent();

        var chevron = new TextBlock
        {
            Text = "\uE70D",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(_summary);
        label.Children.Add(chevron);
        ToggleButton.Content = label;

        UpdateSummary();
    }

    /// <summary>Raised after the user changes a checkbox. Never a request.</summary>
    public event EventHandler? SelectionChanged;

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public IEnumerable? Options
    {
        get => (IEnumerable?)GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    /// <summary>The checked provider ids, in option order.</summary>
    public IReadOnlyList<string> SelectedKeys =>
        TypedOptions().Where(option => option.IsChecked).Select(option => option.Key).ToList();

    public void SetSelectedKeys(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var wanted = new HashSet<string>(keys, StringComparer.Ordinal);
        foreach (var option in TypedOptions())
        {
            option.IsChecked = wanted.Contains(option.Key);
        }

        UpdateSummary();
    }

    private IEnumerable<ComixCheckOption> TypedOptions() =>
        OptionList.ItemsSource?.OfType<ComixCheckOption>() ?? [];

    private static void OnHeaderChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ComixMultiSelectDropDown)sender).UpdateSummary();

    private static void OnOptionsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (ComixMultiSelectDropDown)sender;
        control.OptionList.ItemsSource = args.NewValue as IEnumerable;
        control.UpdateSummary();
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e) =>
        OptionsPopup.IsOpen = !OptionsPopup.IsOpen;

    private void Option_CheckClicked(object sender, RoutedEventArgs e)
    {
        UpdateSummary();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One summary and one accessible name for the closed dropdown: the filter
    /// label alone when nothing is checked, and the label with a count when
    /// something is, so the bar never has to widen to show every choice.
    /// </summary>
    private void UpdateSummary()
    {
        if (ToggleButton is null) return;

        var selected = TypedOptions().Count(option => option.IsChecked);
        _summary.Text = selected == 0 ? Header : $"{Header} ({selected})";
        AutomationProperties.SetName(
            ToggleButton,
            selected == 0
                ? Header
                : $"{Header}, {selected} selected");
    }
}
