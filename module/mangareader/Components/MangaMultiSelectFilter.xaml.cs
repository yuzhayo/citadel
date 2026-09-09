using System.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Module.Mangareader.Components;

/// <summary>One reusable keyed checkbox option used by MangaReader filters.</summary>
public sealed class MangaFilterOption : INotifyPropertyChanged
{
    private bool _isChecked;

    public MangaFilterOption(string key, string displayName)
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Shared MangaReader checkbox dropdown. It owns only draft selection and
/// never performs validation, persistence, database work, or network calls.
/// </summary>
public partial class MangaMultiSelectFilter : UserControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header),
            typeof(string),
            typeof(MangaMultiSelectFilter),
            new FrameworkPropertyMetadata(string.Empty, OnHeaderChanged));

    public static readonly DependencyProperty OptionsProperty =
        DependencyProperty.Register(
            nameof(Options),
            typeof(IEnumerable),
            typeof(MangaMultiSelectFilter),
            new FrameworkPropertyMetadata(null, OnOptionsChanged));

    private readonly TextBlock _summary = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public MangaMultiSelectFilter()
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

    public IReadOnlyList<string> SelectedKeys =>
        TypedOptions().Where(option => option.IsChecked).Select(option => option.Key).ToArray();

    public void SetSelectedKeys(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var wanted = new HashSet<string>(keys, StringComparer.Ordinal);
        foreach (var option in TypedOptions()) option.IsChecked = wanted.Contains(option.Key);
        UpdateSummary();
    }

    private IEnumerable<MangaFilterOption> TypedOptions() =>
        OptionList.ItemsSource?.OfType<MangaFilterOption>() ?? [];

    private static void OnHeaderChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((MangaMultiSelectFilter)sender).UpdateSummary();

    private static void OnOptionsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (MangaMultiSelectFilter)sender;
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

    private void UpdateSummary()
    {
        if (ToggleButton is null) return;
        var selected = TypedOptions().Count(option => option.IsChecked);
        _summary.Text = selected == 0 ? Header : $"{Header} ({selected})";
        AutomationProperties.SetName(
            ToggleButton,
            selected == 0 ? Header : $"{Header}, {selected} selected");
    }
}
