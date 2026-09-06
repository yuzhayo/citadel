using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Module.Mangareader.Sources;
using Module.Mangareader.Features.Downloader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.Comix;

/// <summary>
/// The Comix registration's filter contribution. It owns the panel and hands
/// Catalog only the generic contract, so the panel never calls the adapter
/// directly: lookups arrive as an injected delegate from this registration.
///
/// <see cref="CreatePanel"/> and <see cref="State"/> deliberately resolve to the
/// same instance, because the panel the screen hosts is the panel whose draft the
/// source snapshots at Start. A second instance here would let a user edit
/// filters that never reach a request.
/// </summary>
public sealed class ComixFilterContribution : IRemoteFilterContribution
{
    private readonly Func<RemoteLookupKind, string, CancellationToken, Task<IReadOnlyList<RemoteLookupOption>>> _lookup;
    private ComixFilterPanel? _panel;

    public ComixFilterContribution(
        Func<RemoteLookupKind, string, CancellationToken, Task<IReadOnlyList<RemoteLookupOption>>> lookup) =>
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));

    public IRemoteFilterState State => _panel ??= Create();

    public FrameworkElement CreatePanel() => _panel = Create();

    private ComixFilterPanel Create() => new(_lookup);
}

/// <summary>
/// Comix filters: the compact bar dropdowns plus the Advanced Filters popover,
/// one immutable <see cref="ComixBrowseQuery"/> draft, and local validation.
/// Composed only from existing shared controls and this feature's own checkbox
/// dropdown; it adds no Citadel primitive, style or template, and it never builds
/// a provider URL or issues a browse request.
///
/// Editing a dropdown, a field or the match mode changes draft state only. The
/// only provider calls this panel makes are the explicit author/artist lookups,
/// on Enter or on their own Search action.
/// </summary>
public sealed partial class ComixFilterPanel : UserControl, IRemoteFilterState
{
    private const string IdleMessage =
        "Defaults: latest update, Safe + Suggestive. Every filter value here was captured "
        + "live from the provider. No request is sent until Start.";

    private readonly Func<RemoteLookupKind, string, CancellationToken, Task<IReadOnlyList<RemoteLookupOption>>> _lookup;
    private readonly List<ComixCheckOption> _types;
    private readonly List<ComixCheckOption> _statuses;
    private readonly List<ComixCheckOption> _ratings;
    private readonly List<ComixCheckOption> _demographics;
    private readonly List<ComixCheckOption> _genres;
    private ComixGenreMode _genreMode = ComixGenreMode.And;
    private CancellationTokenSource? _lookupCancellation;

    public ComixFilterPanel(
        Func<RemoteLookupKind, string, CancellationToken, Task<IReadOnlyList<RemoteLookupOption>>> lookup)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        InitializeComponent();

        _types = CheckOptions(ComixOptions.Types);
        _statuses = CheckOptions(ComixOptions.Statuses);
        _ratings = CheckOptions(ComixOptions.Ratings);
        _demographics = CheckOptions(ComixOptions.Demographics);

        // One dropdown offers both lists, because the provider serializes genres
        // and formats through the same genres_in[] key and distinguishes them by
        // id alone.
        _genres = [.. CheckOptions(ComixOptions.Genres), .. CheckOptions(ComixOptions.Formats)];

        TypeFilter.Options = _types;
        StatusFilter.Options = _statuses;
        RatingFilter.Options = _ratings;
        DemographicFilter.Options = _demographics;
        GenreFilter.Options = _genres;
        SortPicker.ItemsSource = ComixOptions.SortOptions;

        Reset();
    }

    public IRemoteBrowseFilter? CurrentFilter => BuildQuery();

    /// <summary>
    /// Blocking covers both a rejected range and an unparseable numeric field, so
    /// Start can never silently drop what the user typed.
    /// </summary>
    public bool HasBlockingError => ValidationMessage is not null;

    public string? ValidationMessage => BuildQuery().ValidationError ?? NumericParseError;

    private string? NumericParseError { get; set; }

    /// <summary>
    /// Restores the provider-observed defaults — latest update plus Safe and
    /// Suggestive — and clears every other draft value. Reset never fetches.
    /// </summary>
    public void Reset()
    {
        SortPicker.SelectedItem = ComixOptions.FindSort(ComixOptions.DefaultSortKey);
        RatingFilter.SetSelectedKeys(ComixOptions.DefaultRatingKeys);
        TypeFilter.SetSelectedKeys([]);
        StatusFilter.SetSelectedKeys([]);
        DemographicFilter.SetSelectedKeys([]);
        GenreFilter.SetSelectedKeys([]);

        MinChapterField.Text = string.Empty;
        YearFromField.Text = string.Empty;
        YearToField.Text = string.Empty;
        AuthorField.Text = string.Empty;
        ArtistField.Text = string.Empty;
        AuthorList.ItemsSource = null;
        ArtistList.ItemsSource = null;

        _genreMode = ComixGenreMode.And;
        GenreModeButton.Content = "AND";

        NumericParseError = null;
        ValidationText.Text = IdleMessage;
    }

    private ComixBrowseQuery BuildQuery() => new()
    {
        SortKey = (SortPicker.SelectedItem as ComixSortOption)?.Key ?? ComixOptions.DefaultSortKey,
        Ratings = RatingFilter.SelectedKeys,
        Types = TypeFilter.SelectedKeys,
        Statuses = StatusFilter.SelectedKeys,
        Demographics = DemographicFilter.SelectedKeys,
        Genres = GenreFilter.SelectedKeys,
        GenreMode = _genreMode,
        MinimumChapter = ParseNullableInt(MinChapterField.Text),
        YearFrom = ParseNullableInt(YearFromField.Text),
        YearTo = ParseNullableInt(YearToField.Text),
        AuthorKey = (AuthorList.SelectedItem as RemoteLookupOption)?.Key,
        ArtistKey = (ArtistList.SelectedItem as RemoteLookupOption)?.Key,
    };

    private static List<ComixCheckOption> CheckOptions(IReadOnlyList<RemoteOption> source) =>
        [.. source.Select(option => new ComixCheckOption(option.Key, option.DisplayName))];

    private int? ParseNullableInt(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return int.TryParse(raw.Trim(), out var value) ? value : null;
    }

    private void DraftFilter_Changed(object? sender, EventArgs e) => ValidateNumeric();

    private void NumericDraft_Changed(string text) => ValidateNumeric();

    private void SortPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => ValidateNumeric();

    private void ResolvedOption_Changed(object sender, SelectionChangedEventArgs e) => ValidateNumeric();

    /// <summary>
    /// Field-local validation only. An unparseable or inverted range blocks
    /// Start and never reaches the provider.
    /// </summary>
    private void ValidateNumeric()
    {
        NumericParseError = FirstParseError(
            ("Minimum chapter", MinChapterField.Text),
            ("Release year from", YearFromField.Text),
            ("Release year to", YearToField.Text));

        ValidationText.Text = NumericParseError ?? BuildQuery().ValidationError ?? IdleMessage;
    }

    private static string? FirstParseError(params (string Label, string Raw)[] fields)
    {
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Raw)) continue;
            if (!int.TryParse(field.Raw.Trim(), out _))
            {
                return $"{field.Label} must be a whole number.";
            }
        }

        return null;
    }

    private void AdvancedButton_Click(object sender, RoutedEventArgs e) =>
        AdvancedPopup.IsOpen = !AdvancedPopup.IsOpen;

    private void GenreModeButton_Click(object sender, RoutedEventArgs e)
    {
        _genreMode = _genreMode == ComixGenreMode.And ? ComixGenreMode.Or : ComixGenreMode.And;
        GenreModeButton.Content = _genreMode == ComixGenreMode.And ? "AND" : "OR";
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e) => Reset();

    private async void AuthorSearchButton_Click(object sender, RoutedEventArgs e) =>
        await SearchAsync(RemoteLookupKind.Author, AuthorField.Text, AuthorSearchButton, AuthorList);

    private async void ArtistSearchButton_Click(object sender, RoutedEventArgs e) =>
        await SearchAsync(RemoteLookupKind.Artist, ArtistField.Text, ArtistSearchButton, ArtistList);

    /// <summary>An explicit Enter is the same action as the field's Search.</summary>
    private async void LookupField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        if (ReferenceEquals(sender, AuthorField))
        {
            await SearchAsync(RemoteLookupKind.Author, AuthorField.Text, AuthorSearchButton, AuthorList);
        }
        else if (ReferenceEquals(sender, ArtistField))
        {
            await SearchAsync(RemoteLookupKind.Artist, ArtistField.Text, ArtistSearchButton, ArtistList);
        }
    }

    /// <summary>
    /// One explicit lookup. Free typing is never sent as a provider value: only a
    /// resolved option key can enter the query.
    /// </summary>
    private async Task SearchAsync(
        RemoteLookupKind kind,
        string query,
        Control button,
        ListBox target)
    {
        if (!IsLoaded) return;
        if (string.IsNullOrWhiteSpace(query))
        {
            ValidationText.Text = "Type a term, then press Search.";
            return;
        }

        _lookupCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _lookupCancellation = cancellation;
        button.IsEnabled = false;
        ValidationText.Text = "Resolving options…";

        try
        {
            var options = await _lookup(kind, query.Trim(), cancellation.Token).ConfigureAwait(true);
            if (!IsLoaded || !ReferenceEquals(_lookupCancellation, cancellation)) return;

            target.ItemsSource = options;
            target.SelectedItem = options.Count == 1 ? options[0] : null;
            ValidationText.Text = options.Count == 0
                ? "No provider option matched that term."
                : $"{options.Count} option resolved. Select one to use it.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!IsLoaded || !ReferenceEquals(_lookupCancellation, cancellation)) return;
            ValidationText.Text = "Lookup failed: " + exception.GetBaseException().Message;
        }
        finally
        {
            if (IsLoaded) button.IsEnabled = true;
            if (ReferenceEquals(_lookupCancellation, cancellation))
            {
                _lookupCancellation = null;
            }

            cancellation.Dispose();
        }
    }
}
