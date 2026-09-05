using System.Windows;
using System.Windows.Controls;
using Module.Mangareader.Features.Downloader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.Comix;

/// <summary>
/// The Comix registration's filter contribution. It owns the panel and hands
/// Catalog only the generic contract, so the panel never calls the adapter
/// directly: lookups arrive as an injected delegate from this registration.
/// </summary>
public sealed class ComixFilterContribution : IRemoteFilterContribution
{
    private readonly Func<RemoteLookupKind, string, CancellationToken, Task<IReadOnlyList<RemoteLookupOption>>> _lookup;
    private ComixFilterPanel? _panel;

    public ComixFilterContribution(
        Func<RemoteLookupKind, string, CancellationToken, Task<IReadOnlyList<RemoteLookupOption>>> lookup) =>
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));

    public IRemoteFilterState State => _panel ??= new ComixFilterPanel(_lookup);

    public FrameworkElement CreatePanel()
    {
        _panel = new ComixFilterPanel(_lookup);
        return _panel;
    }
}

/// <summary>
/// Comix Advanced Filters: input state and local validation, producing one
/// immutable <see cref="ComixBrowseQuery"/>. Composed only from existing shared
/// controls; it adds no primitive, style or template, and it never builds a
/// provider URL or issues a browse request.
///
/// Editing a field or changing a selection performs no remote request. Only the
/// explicit Search actions on genre, format, author and artist call the
/// provider, through the delegate this panel was given.
/// </summary>
public sealed partial class ComixFilterPanel : UserControl, IRemoteFilterState
{
    private readonly Func<RemoteLookupKind, string, CancellationToken, Task<IReadOnlyList<RemoteLookupOption>>> _lookup;
    private ComixGenreMode _genreMode = ComixGenreMode.And;
    private CancellationTokenSource? _lookupCancellation;

    public ComixFilterPanel(
        Func<RemoteLookupKind, string, CancellationToken, Task<IReadOnlyList<RemoteLookupOption>>> lookup)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        InitializeComponent();

        RatingList.ItemsSource = ComixOptions.Ratings;
        TypeList.ItemsSource = ComixOptions.Types;
        DemographicList.ItemsSource = ComixOptions.Demographics;
        StatusList.ItemsSource = ComixOptions.Statuses;
        SortPicker.ItemsSource = ComixOptions.Sorts;

        DisableUncapturedFilters();
        Reset();
    }

    /// <summary>
    /// Only the filters whose live query key was captured stay usable. The rest
    /// are visibly disabled with the reason on the panel, so nobody can set a
    /// filter that the provider would silently ignore or reject.
    /// </summary>
    private void DisableUncapturedFilters()
    {
        Control[] uncaptured =
        [
            GenreSearchField, GenreSearchButton, GenreModeButton, GenreList,
            FormatSearchField, FormatSearchButton, FormatList,
            DemographicList, StatusList,
            MinChapterField, YearFromField, YearToField,
            AuthorField, AuthorSearchButton, AuthorList,
            ArtistField, ArtistSearchButton, ArtistList,
        ];

        foreach (var control in uncaptured)
        {
            control.IsEnabled = false;
        }
    }

    public IRemoteBrowseFilter? CurrentFilter => BuildQuery();

    public bool HasBlockingError => BuildQuery().ValidationError is not null;

    public string? ValidationMessage => BuildQuery().ValidationError ?? NumericParseError;

    private string? NumericParseError { get; set; }

    /// <summary>
    /// One idle message that states both facts a user needs: the captured
    /// defaults, and which filters are disabled until their live query keys are
    /// captured from the site.
    /// </summary>
    private const string IdleMessage =
        "Defaults: latest update, Safe + Suggestive. Genre, format, demographic, status, "
        + "chapter/year range, author and artist are disabled until their live query keys "
        + "are captured. No request is sent until Start.";

    public void Reset()
    {
        RatingList.SelectedItems.Clear();
        foreach (var rating in ComixOptions.Ratings)
        {
            if (ComixOptions.DefaultRatingKeys.Contains(rating.Key))
            {
                RatingList.SelectedItems.Add(rating);
            }
        }

        TypeList.SelectedItems.Clear();
        DemographicList.SelectedItems.Clear();
        StatusList.SelectedItems.Clear();
        GenreList.ItemsSource = null;
        GenreList.SelectedItems.Clear();
        FormatList.ItemsSource = null;
        FormatList.SelectedItems.Clear();
        AuthorList.ItemsSource = null;
        ArtistList.ItemsSource = null;

        GenreSearchField.Text = string.Empty;
        FormatSearchField.Text = string.Empty;
        AuthorField.Text = string.Empty;
        ArtistField.Text = string.Empty;
        MinChapterField.Text = string.Empty;
        YearFromField.Text = string.Empty;
        YearToField.Text = string.Empty;

        _genreMode = ComixGenreMode.And;
        GenreModeButton.Content = "AND";
        SortPicker.SelectedItem = ComixOptions.Sorts
            .FirstOrDefault(option => option.Key == ComixOptions.DefaultSortKey);

        NumericParseError = null;
        ValidationText.Text = IdleMessage;
    }

    private ComixBrowseQuery BuildQuery() => new()
    {
        SortKey = (SortPicker.SelectedItem as RemoteOption)?.Key ?? ComixOptions.DefaultSortKey,
        Ratings = Keys(RatingList),
        Types = Keys(TypeList),
        Genres = Keys(GenreList),
        Formats = Keys(FormatList),
        GenreMode = _genreMode,
        Demographics = Keys(DemographicList),
        Statuses = Keys(StatusList),
        MinimumChapter = ParseNullableInt(MinChapterField.Text),
        YearFrom = ParseNullableInt(YearFromField.Text),
        YearTo = ParseNullableInt(YearToField.Text),
        AuthorKey = (AuthorList.SelectedItem as RemoteLookupOption)?.Key,
        ArtistKey = (ArtistList.SelectedItem as RemoteLookupOption)?.Key,
    };

    private static IReadOnlyList<string> Keys(ListBox list) =>
        list.SelectedItems.OfType<RemoteOption>().Select(option => option.Key).ToList();

    private int? ParseNullableInt(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return int.TryParse(raw.Trim(), out var value) ? value : null;
    }

    private void MinChapterField_TextChanged(string text) => ValidateNumeric();

    private void YearField_TextChanged(string text) => ValidateNumeric();

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

        var message = NumericParseError ?? BuildQuery().ValidationError;
        ValidationText.Text = message ?? IdleMessage;
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

    private void SortPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => ValidateNumeric();

    private void GenreModeButton_Click(object sender, RoutedEventArgs e)
    {
        _genreMode = _genreMode == ComixGenreMode.And ? ComixGenreMode.Or : ComixGenreMode.And;
        GenreModeButton.Content = _genreMode == ComixGenreMode.And ? "AND" : "OR";
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e) => Reset();

    private async void GenreSearchButton_Click(object sender, RoutedEventArgs e) =>
        await SearchAsync(RemoteLookupKind.Genre, GenreSearchField.Text, GenreSearchButton, GenreList);

    private async void FormatSearchButton_Click(object sender, RoutedEventArgs e) =>
        await SearchAsync(RemoteLookupKind.Format, FormatSearchField.Text, FormatSearchButton, FormatList);

    private async void AuthorSearchButton_Click(object sender, RoutedEventArgs e) =>
        await SearchAsync(RemoteLookupKind.Author, AuthorField.Text, AuthorSearchButton, AuthorList);

    private async void ArtistSearchButton_Click(object sender, RoutedEventArgs e) =>
        await SearchAsync(RemoteLookupKind.Artist, ArtistField.Text, ArtistSearchButton, ArtistList);

    /// <summary>
    /// One explicit lookup. Free typing is never sent as a provider value: only
    /// a resolved option key can enter the query.
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

    private void AuthorList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ValidateNumeric();

    private void ArtistList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ValidateNumeric();
}
