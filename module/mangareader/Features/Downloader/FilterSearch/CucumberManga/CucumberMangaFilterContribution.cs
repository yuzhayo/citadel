using System.Windows;
using System.Windows.Controls;
using Module.Mangareader.Components;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Features.Downloader.Sources.CucumberManga;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.FilterSearch.CucumberManga;

/// <summary>Provider-owned filter surface hosted through the generic Downloader contract.</summary>
public sealed class CucumberMangaFilterContribution : IRemoteFilterContribution
{
    private CucumberMangaFilterPanel? _panel;

    public IRemoteFilterState State => _panel ??= new CucumberMangaFilterPanel();

    public FrameworkElement CreatePanel() => _panel ??= new CucumberMangaFilterPanel();
}

/// <summary>
/// Draft-only Cucumber Manga filter state. It reuses MangaReader's checkbox
/// dropdown and Setting controls; request serialization remains in the source.
/// </summary>
public sealed partial class CucumberMangaFilterPanel : UserControl, IRemoteFilterState
{
    private const string IdleMessage =
        "Defaults: latest update and all content. Checked values are sent together on Search.";

    private readonly List<MangaFilterOption> _statuses;
    private readonly List<MangaFilterOption> _genres;

    public CucumberMangaFilterPanel()
    {
        InitializeComponent();
        _statuses = ToCheckOptions(CucumberMangaOptions.Statuses);
        _genres = ToCheckOptions(CucumberMangaOptions.Genres);
        StatusFilter.Options = _statuses;
        GenreFilter.Options = _genres;
        SortPicker.ItemsSource = CucumberMangaOptions.Sorts;
        AdultPicker.ItemsSource = CucumberMangaOptions.AdultModes;
        Reset();
    }

    public IRemoteBrowseFilter? Snapshot(string? keyword) => new CucumberMangaBrowseQuery
    {
        SortKey = (SortPicker.SelectedItem as CucumberMangaSortOption)?.Key ?? "latest",
        Statuses = StatusFilter.SelectedKeys,
        Genres = GenreFilter.SelectedKeys,
        AdultMode = (AdultPicker.SelectedItem as CucumberMangaAdultOption)?.Mode
            ?? CucumberMangaAdultMode.All,
        Author = EmptyToNull(AuthorField.Text),
        Artist = EmptyToNull(ArtistField.Text),
        ReleaseYear = EmptyToNull(ReleaseYearField.Text),
    };

    public bool HasBlockingError => ValidationMessage is not null;

    public string? ValidationMessage
    {
        get
        {
            var raw = ReleaseYearField.Text.Trim();
            if (raw.Length == 0) return null;
            return int.TryParse(raw, out var year) && year is >= 1900 and <= 2200
                ? null
                : "Release year must be a whole year from 1900 to 2200.";
        }
    }

    private void Reset()
    {
        SortPicker.SelectedItem = CucumberMangaOptions.Sorts.First(option => option.Key == "latest");
        AdultPicker.SelectedItem = CucumberMangaOptions.AdultModes.First(
            option => option.Mode == CucumberMangaAdultMode.All);
        StatusFilter.SetSelectedKeys([]);
        GenreFilter.SetSelectedKeys([]);
        ReleaseYearField.Text = string.Empty;
        AuthorField.Text = string.Empty;
        ArtistField.Text = string.Empty;
        ValidationText.Text = IdleMessage;
    }

    private void ValidateDraft() => ValidationText.Text = ValidationMessage ?? IdleMessage;

    private void DraftFilter_Changed(object? sender, EventArgs e) => ValidateDraft();

    private void TextFilter_Changed(string text) => ValidateDraft();

    private void AdvancedButton_Click(object sender, RoutedEventArgs e) =>
        AdvancedPopup.IsOpen = !AdvancedPopup.IsOpen;

    private void ResetButton_Click(object sender, RoutedEventArgs e) => Reset();

    private static List<MangaFilterOption> ToCheckOptions(IReadOnlyList<RemoteOption> options) =>
        [.. options.Select(option => new MangaFilterOption(option.Key, option.DisplayName))];

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
