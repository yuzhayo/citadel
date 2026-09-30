using System.Windows;
using System.Windows.Controls;
using Module.Mangareader.Components;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Features.Downloader.Sources.WeebCentral;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.FilterSearch.WeebCentral;

/// <summary>Provider-owned filter surface hosted through the generic Downloader contract.</summary>
public sealed class WeebCentralFilterContribution : IRemoteFilterContribution
{
    private WeebCentralFilterPanel? _panel;

    public IRemoteFilterState State => _panel ??= new WeebCentralFilterPanel();

    public FrameworkElement CreatePanel() => _panel ??= new WeebCentralFilterPanel();
}

/// <summary>
/// Draft-only Weeb Central filter state. It reuses MangaReader's checkbox
/// dropdown and Setting controls; request serialization remains in the source.
/// </summary>
public sealed partial class WeebCentralFilterPanel : UserControl, IRemoteFilterState
{
    private const string IdleMessage =
        "Defaults: popularity, newest first, all content. Checked values are sent together on Search.";

    private readonly List<MangaFilterOption> _statuses;
    private readonly List<MangaFilterOption> _types;
    private readonly List<MangaFilterOption> _tags;

    public WeebCentralFilterPanel()
    {
        InitializeComponent();
        _statuses = ToCheckOptions(WeebCentralOptions.Statuses);
        _types = ToCheckOptions(WeebCentralOptions.Types);
        _tags = ToCheckOptions(WeebCentralOptions.Tags);
        StatusFilter.Options = _statuses;
        TypeFilter.Options = _types;
        TagFilter.Options = _tags;
        SortPicker.ItemsSource = WeebCentralOptions.Sorts;
        OrderPicker.ItemsSource = WeebCentralOptions.Orders;
        AdultPicker.ItemsSource = WeebCentralOptions.TriState;
        Reset();
    }

    public IRemoteBrowseFilter? Snapshot(string? keyword) => new WeebCentralBrowseQuery
    {
        SortKey = (SortPicker.SelectedItem as RemoteOption)?.Key ?? WeebCentralOptionKeys.DefaultSort,
        Order = (OrderPicker.SelectedItem as RemoteOption)?.Key ?? WeebCentralOptionKeys.Descending,
        Statuses = StatusFilter.SelectedKeys,
        Types = TypeFilter.SelectedKeys,
        Tags = TagFilter.SelectedKeys,
        Official = WeebCentralOptionKeys.Any,
        Anime = WeebCentralOptionKeys.Any,
        Adult = (AdultPicker.SelectedItem as RemoteOption)?.Key ?? WeebCentralOptionKeys.Any,
    };

    public bool HasBlockingError => ValidationMessage is not null;

    public string? ValidationMessage => null;

    private void Reset()
    {
        SortPicker.SelectedItem = WeebCentralOptions.Sorts.First(option => option.Key == WeebCentralOptionKeys.DefaultSort);
        OrderPicker.SelectedItem = WeebCentralOptions.Orders.First(option => option.Key == WeebCentralOptionKeys.Descending);
        AdultPicker.SelectedItem = WeebCentralOptions.TriState.First(option => option.Key == WeebCentralOptionKeys.Any);
        StatusFilter.SetSelectedKeys([]);
        TypeFilter.SetSelectedKeys([]);
        TagFilter.SetSelectedKeys([]);
        ValidationText.Text = IdleMessage;
    }

    private void DraftFilter_Changed(object? sender, EventArgs e)
    {
        ValidationText.Text = IdleMessage;
    }

    private void AdvancedButton_Click(object sender, RoutedEventArgs e) =>
        AdvancedPopup.IsOpen = !AdvancedPopup.IsOpen;

    private void ResetButton_Click(object sender, RoutedEventArgs e) => Reset();

    private static List<MangaFilterOption> ToCheckOptions(IReadOnlyList<RemoteOption> options) =>
        [.. options.Select(option => new MangaFilterOption(option.Key, option.DisplayName))];
}
