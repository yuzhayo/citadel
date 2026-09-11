using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Threading;
using Citadel.Setting.Components;
using Module.Mangareader.Components;
using Module.Mangareader.Features.Catalog;
using Module.Mangareader.ShareLogic;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.CatalogMirror;

/// <summary>
/// Catalog UI event wiring, Dispatcher marshaling, page card rendering and
/// navigation events. It never calls providers, HTTP, the filesystem or the
/// queue directly, parses no JSON, and owns no sync/query policy: every tap
/// becomes one feature call, and every state render comes from one immutable
/// feature state.
/// </summary>
public partial class CatalogMirrorView : UserControl, IDisposable
{
    private static readonly string[] SortLabels =
    [
        "Latest update",
        "Title (A-Z)",
        "Title (Z-A)",
        "Year (newest)",
        "Year (oldest)",
        "Latest chapter (highest)",
        "Latest chapter (lowest)",
    ];

    private static readonly CatalogContentRating[] RatingValues =
    [
        CatalogContentRating.Safe,
        CatalogContentRating.Suggestive,
        CatalogContentRating.Erotica,
        CatalogContentRating.Pornographic,
    ];

    private readonly ObservableCollection<CatalogMirrorCardModel> _resultCards = [];
    private readonly Dictionary<(string SourceId, string TitleId), CatalogMirrorCardModel> _resultCardsByIdentity = [];
    private readonly ViewModePreferenceStore _viewModeStore = new(
        "Catalog.ViewMode",
        "catalog-view-mode.json");
    private CatalogMirrorFeature? _feature;
    private CatalogContext? _context;
    private CancellationTokenSource? _onlineStart;
    private Func<CatalogQueueTargetRequest, string?>? _confirmFolder;
    private CatalogMirrorResultPage? _renderedPage;
    private CheckBox? _selectAllBox;
    private DateTimeOffset _lastRenderUtc;
    private List<MangaFilterOption> _ratingOptions = [];
    private List<MangaFilterOption> _typeOptions = [];
    private List<MangaFilterOption> _statusOptions = [];
    private List<MangaFilterOption> _languageOptions = [];
    private List<MangaFilterOption> _genreOptions = [];
    private bool _restoreScrollPending;
    private bool _resetScrollPending = true;
    private bool _hasRenderedResults;
    private bool _ready;
    private bool _suppressDetailEvents;
    private MangaViewMode _viewMode;
    private double _savedScrollOffset;
    private bool _disposed;

    public CatalogMirrorView()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _resultCards;
        ResultsTable.ItemsSource = _resultCards;
        _viewMode = _viewModeStore.Load(out _);
        ViewModeSelector.Mode = _viewMode;
        ViewModeSelector.ModeRequested += ViewModeSelector_ModeRequested;
        ApplyViewMode();
    }

    /// <summary>
    /// Raised with an immutable handoff once chapters are selected and their
    /// target folder is confirmed. The parent subscribes it to the queue.
    /// </summary>
    public event EventHandler<CatalogQueueHandoffRequest>? QueueHandoffRequested;

    public void UseContext(CatalogContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_disposed || _context is not null) return;
        _context = context;
        UseFeature(context.Feature);
    }

    public void UseFeature(CatalogMirrorFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (_disposed || _feature is not null) return;

        _feature = feature;
        _feature.StateChanged += Feature_StateChanged;

        foreach (var label in SortLabels)
        {
            SortBox.Items.Add(label);
        }

        SortBox.SelectedIndex = 0;
        BuildRatingOptions();
        _ready = true;
        _ = InitializeAsync();
    }

    /// <summary>
    /// First snapshot load. A corrupt or newer snapshot surfaces on the status
    /// line instead of faulting an unobserved task; the tab can still Sync.
    /// </summary>
    private async Task InitializeAsync()
    {
        if (_feature is null) return;
        try
        {
            await _feature.InitializeAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            StatusLine.Text = "Snapshot failed to load: " + exception.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Folder confirmation for Queue Selected. The parent owns the Library
    /// root and the mapping index; this view never reads either directly.
    /// </summary>
    public void UseQueueTarget(Func<CatalogQueueTargetRequest, string?> confirmFolder)
    {
        _confirmFolder = confirmFolder ?? throw new ArgumentNullException(nameof(confirmFolder));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var onlineStart = _onlineStart;
        _onlineStart = null;
        onlineStart?.Cancel();
        onlineStart?.Dispose();

        if (_feature is not null)
        {
            _feature.StateChanged -= Feature_StateChanged;
        }
        ViewModeSelector.ModeRequested -= ViewModeSelector_ModeRequested;
    }

    private void Feature_StateChanged(object? sender, CatalogMirrorState state)
    {
        if (_disposed || _feature is null) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Feature_StateChanged(sender, state));
            return;
        }

        // Coalesce rapid sync-progress ticks: same result object and a fresh
        // render mean only the status line would churn. Terminal states, new
        // results and detail changes always render at once.
        var now = DateTimeOffset.UtcNow;
        if (state.Sync.State == CatalogSyncState.Syncing
            && state.Detail is null
            && ReferenceEquals(state.Results, _renderedPage)
            && now - _lastRenderUtc < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        _lastRenderUtc = now;
        Render(state);
    }

    private void Render(CatalogMirrorState state)
    {
        RenderSyncSlot(state);
        RenderGenreSyncSlot(state);
        RenderProgress(state);
        if (state.Detail is null)
        {
            var returningFromDetail = DetailHost.Visibility == Visibility.Visible;
            DetailHost.Visibility = Visibility.Collapsed;
            ResultsArea.Visibility = Visibility.Visible;
            PaginationRow.Visibility = Visibility.Visible;
            if (!_hasRenderedResults
                || returningFromDetail
                || !ReferenceEquals(state.Results, _renderedPage))
            {
                RenderResults(state);
                RenderPagination(state);
                _hasRenderedResults = true;
            }
        }
        else
        {
            ResultsArea.Visibility = Visibility.Collapsed;
            PaginationRow.Visibility = Visibility.Collapsed;
            DetailHost.Visibility = Visibility.Visible;
            RenderDetail(state.Detail);
        }
    }

    private void RenderGenreSyncSlot(CatalogMirrorState state)
    {
        var progress = state.GenreSync;
        if (progress is null)
        {
            GenreSyncButton.Content = "Sync Genres";
            GenreSyncButton.IsEnabled = CanStartGenreSync(state);
            return;
        }

        switch (progress.State)
        {
            case CatalogGenreSyncState.Syncing:
                GenreSyncButton.Content = "Stop Genres";
                GenreSyncButton.IsEnabled = true;
                GenreStatusText.Text =
                    $"Genre details {progress.ProcessedTitles}/{progress.AvailableTitles}: {progress.TitleName} • {progress.TaggedTitles} tags";
                break;
            case CatalogGenreSyncState.Stopping:
                GenreSyncButton.Content = "Stopping…";
                GenreSyncButton.IsEnabled = false;
                break;
            case CatalogGenreSyncState.Stopped:
            case CatalogGenreSyncState.Error:
                GenreSyncButton.Content = "Resume Genres";
                GenreSyncButton.IsEnabled = true;
                GenreStatusText.Text = progress.ErrorMessage
                    ?? $"Genre enrichment stopped after {progress.ProcessedTitles}/{progress.AvailableTitles} titles.";
                break;
            case CatalogGenreSyncState.Ready:
                GenreSyncButton.Content = "Sync Genres";
                GenreSyncButton.IsEnabled = true;
                GenreStatusText.Text = "Genre enrichment complete. Press Load Catalog to enable the genre filter.";
                break;
            default:
                GenreSyncButton.Content = "Sync Genres";
                GenreSyncButton.IsEnabled = CanStartGenreSync(state);
                break;
        }
    }

    private static bool CanStartGenreSync(CatalogMirrorState state) =>
        state.Sync.State == CatalogSyncState.Syncing
        || (state.Sync.State == CatalogSyncState.Ready
            && state.Results is not null
            && !state.IsPartial);

    private void RenderSyncSlot(CatalogMirrorState state)
    {
        var sync = state.Sync;
        var warnings = _feature?.LastWarnings ?? [];
        WarningsButton.Visibility =
            sync.State == CatalogSyncState.Ready && warnings.Count != 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (WarningsButton.Visibility == Visibility.Visible)
        {
            WarningsButton.Content = $"Warnings ({warnings.Count})";
        }

        // Refresh needs an active snapshot; the traversal states share the
        // single Start/Stop slot.
        RefreshButton.IsEnabled = state.Results is not null
            && sync.State is not CatalogSyncState.Syncing
            && sync.State is not CatalogSyncState.Stopping
            && sync.State is not CatalogSyncState.Loading;
        switch (sync.State)
        {
            case CatalogSyncState.Syncing:
                SyncButton.Content = "Stop";
                SyncButton.IsEnabled = true;
                StatusLine.Text = sync.PartitionKey is null
                    ? "Starting sync…"
                    : $"Syncing {sync.PartitionKey} page {sync.Page} • {sync.StagedRecords} staged";
                break;
            case CatalogSyncState.Stopping:
                SyncButton.Content = "Stopping…";
                SyncButton.IsEnabled = false;
                StatusLine.Text = "Stopping after the current request…";
                break;
            case CatalogSyncState.Stopped:
                SyncButton.Content = "Resume Sync";
                SyncButton.IsEnabled = true;
                StatusLine.Text =
                    $"Stopped at {sync.PartitionKey} page {sync.Page} • Resume to continue";
                break;
            case CatalogSyncState.Error:
                SyncButton.Content = "Resume Sync";
                SyncButton.IsEnabled = true;
                StatusLine.Text = string.IsNullOrWhiteSpace(sync.ErrorMessage)
                    ? "Sync failed."
                    : $"Sync failed: {sync.ErrorMessage}";
                if (state.Results is not null)
                {
                    StatusLine.Text += " • previous snapshot still shown";
                }

                break;
            case CatalogSyncState.Loading:
                SyncButton.Content = "Sync Catalog";
                SyncButton.IsEnabled = false;
                StatusLine.Text = "Loading snapshot…";
                break;
            default:
                SyncButton.Content = "Sync Catalog";
                SyncButton.IsEnabled = true;
                StatusLine.Text = state.Results is null
                    ? "No snapshot."
                    : state.IsPartial
                        ? $"Loaded {state.Results.TotalResults} staged titles from database."
                        : DescribeReady(sync, state.Results.TotalResults, _feature?.LastDeltaUtc);
                break;
        }
    }

    private static string DescribeReady(
        CatalogSyncProgress sync,
        int loadedTitles,
        DateTimeOffset? lastDeltaUtc)
    {
        var titleCount = sync.UniqueTitles > 0 ? sync.UniqueTitles : loadedTitles;
        var text = sync.LastSyncedUtc is null
            ? $"Ready • {titleCount} titles"
            : $"Synced {sync.LastSyncedUtc.Value.LocalDateTime:g} • {titleCount} titles";
        if (lastDeltaUtc is not null)
        {
            text += $" • last refresh {lastDeltaUtc.Value.LocalDateTime:g}";
        }

        return sync.WarningCount > 0 ? $"{text} • {sync.WarningCount} warnings" : text;
    }

    /// <summary>
    /// Progress bar honesty rule: determinate overall percentage only when
    /// every partition reports a provider total; a moving indeterminate bar
    /// while pages commit; hidden otherwise. Never a fabricated percent.
    /// </summary>
    private void RenderProgress(CatalogMirrorState state)
    {
        var sync = state.Sync;
        PartialLabel.Visibility =
            state.IsPartial && state.Results is not null
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (sync.State is not CatalogSyncState.Syncing
            && sync.State is not CatalogSyncState.Stopping)
        {
            SyncProgressBar.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = sync.Partitions;
        if (parts.Count == 0)
        {
            SyncProgressBar.Visibility = Visibility.Visible;
            SyncProgressBar.IsIndeterminate = true;
            return;
        }

        long staged = 0;
        long total = 0;
        foreach (var part in parts)
        {
            if (part.ProviderTotal is null)
            {
                SyncProgressBar.Visibility = Visibility.Visible;
                SyncProgressBar.IsIndeterminate = true;
                return;
            }

            staged += part.StagedRecords;
            total += part.ProviderTotal.Value;
        }

        if (parts.Count != sync.PartitionCount || total <= 0)
        {
            SyncProgressBar.Visibility = Visibility.Visible;
            SyncProgressBar.IsIndeterminate = true;
            return;
        }

        SyncProgressBar.Visibility = Visibility.Visible;
        SyncProgressBar.IsIndeterminate = false;
        SyncProgressBar.Value = Math.Min(100, staged * 100.0 / total);
    }

    private void RenderResults(CatalogMirrorState state)
    {
        if (state.Results is null)
        {
            _renderedPage = null;
            ClearResultCards();
            ResultsScroll.Visibility = Visibility.Collapsed;
            ResultsTable.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
            EmptyTitle.Text = "No snapshot yet";
            EmptyDetail.Text =
                "Press Sync Catalog to download the title index. Search, filter, sort and paging stay offline afterwards.";
            return;
        }

        if (state.Results.TotalResults == 0)
        {
            _renderedPage = state.Results;
            ClearResultCards();
            ResultsScroll.Visibility = Visibility.Collapsed;
            ResultsTable.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
            EmptyTitle.Text = "No titles match";
            EmptyDetail.Text = "Loosen the search or filters. Nothing was requested from the network.";
            return;
        }

        EmptyPanel.Visibility = Visibility.Collapsed;
        ApplyViewMode();

        // Same page object (e.g. a status-only sync tick) needs no collection
        // work. A new staged page is reconciled into the existing collection,
        // keeping the ItemsSource and card instances stable while sync runs.
        if (!ReferenceEquals(_renderedPage, state.Results))
        {
            _renderedPage = state.Results;
            ReconcileResultCards(state.Results.Items);

            // Background staging mutates the stable collection in place. It
            // must not enqueue an offset write: a delayed write from an older
            // render can otherwise override a scroll the user made meanwhile.
            if (_resetScrollPending)
            {
                ResultsScroll.ScrollToHome();
            }

            _resetScrollPending = false;
        }

        if (_restoreScrollPending)
        {
            _restoreScrollPending = false;
            RestoreResultsOffset(_savedScrollOffset);
        }

        if (_feature is not null)
        {
            RebuildOptions(TypeFilter, ref _typeOptions,
                _feature.AvailableTypes.Select(value => new CatalogGenreOption(value, value)).ToArray());
            RebuildOptions(StatusFilter, ref _statusOptions,
                _feature.AvailableStatuses.Select(value => new CatalogGenreOption(value, value)).ToArray());
            RebuildOptions(LanguageFilter, ref _languageOptions,
                _feature.AvailableLanguages.Select(value => new CatalogGenreOption(value, value)).ToArray());
            RebuildOptions(GenreFilter, ref _genreOptions, _feature.AvailableGenres);
            GenreFilter.IsEnabled = _feature.IsGenreEnrichmentComplete;
            GenreStatusText.Text = _feature.IsGenreEnrichmentComplete
                ? "Selected values inside one filter are matched as alternatives; filter categories combine together."
                : "Genres become available after Genre Sync completes and Load Catalog is pressed.";
        }
    }

    private void ReconcileResultCards(IReadOnlyList<CatalogSnapshotItem> items)
    {
        for (var targetIndex = 0; targetIndex < items.Count; targetIndex++)
        {
            var item = items[targetIndex];
            var identity = (item.SourceId, item.TitleId);
            if (!_resultCardsByIdentity.TryGetValue(identity, out var model))
            {
                model = new CatalogMirrorCardModel(item);
                _resultCardsByIdentity.Add(identity, model);
            }
            else
            {
                model.Item = item;
            }

            var currentIndex = _resultCards.IndexOf(model);
            if (currentIndex < 0)
            {
                _resultCards.Insert(targetIndex, model);
            }
            else if (currentIndex != targetIndex)
            {
                _resultCards.Move(currentIndex, targetIndex);
            }
        }

        while (_resultCards.Count > items.Count)
        {
            var removed = _resultCards[^1];
            _resultCards.RemoveAt(_resultCards.Count - 1);
            _resultCardsByIdentity.Remove((removed.Item.SourceId, removed.Item.TitleId));
        }
    }

    private void ClearResultCards()
    {
        _resultCards.Clear();
        _resultCardsByIdentity.Clear();
    }

    private void RestoreResultsOffset(double offset) =>
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                if (!_disposed && DetailHost.Visibility != Visibility.Visible)
                {
                    ResultsScroll.ScrollToVerticalOffset(offset);
                }
            });

    private void RenderDetail(CatalogDetailState detail)
    {
        _suppressDetailEvents = true;
        try
        {
            DetailView.Title = detail.Display?.Title ?? detail.Title.Title;
            DetailView.Synopsis = detail.Display?.Synopsis ?? string.Empty;
            DetailView.Metadata = DetailRows(detail);
            DetailView.Cover = DecodeCover(detail.Display?.CoverPath);

            GroupBox.ItemsSource = detail.Groups;
            var selected = detail.Groups.FirstOrDefault(group =>
                string.Equals(group.GroupId, detail.SelectedGroupId, StringComparison.Ordinal));
            GroupBox.SelectedItem = selected;
            GroupBox.IsEnabled = !detail.IsLoading && string.IsNullOrWhiteSpace(detail.ErrorMessage);

            ChapterTable.ItemsSource = detail.Chapters;
            if (_selectAllBox is not null)
            {
                _selectAllBox.IsChecked = detail.Chapters.Count != 0
                    && detail.Chapters.All(chapter => chapter.IsSelected);
            }

            QueueSelectedButton.IsEnabled = !detail.IsLoading
                && string.IsNullOrWhiteSpace(detail.ErrorMessage)
                && detail.Chapters.Any(chapter => chapter.IsSelected);

            DetailStatus.Text = detail.IsLoading
                ? "Loading detail…"
                : detail.ErrorMessage ?? string.Empty;
        }
        finally
        {
            _suppressDetailEvents = false;
        }
    }

    private static IReadOnlyList<MangaDetailMetadataRow> DetailRows(CatalogDetailState detail)
    {
        if (detail.Display is null)
        {
            return [];
        }

        var rows = new List<MangaDetailMetadataRow>
        {
            new("Genres", detail.Display.GenresText),
        };
        rows.AddRange(detail.Display.Metadata.Select(
            pair => new MangaDetailMetadataRow(pair.DisplayName, pair.Key)));
        return rows;
    }

    private static BitmapImage? DecodeCover(string? coverPath)
    {
        if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(coverPath, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async void TitleCard_Click(object sender, RoutedEventArgs e)
    {
        if (_feature is null) return;
        if ((sender as FrameworkElement)?.Tag is not CatalogMirrorCardModel card) return;

        _savedScrollOffset = ResultsScroll.VerticalOffset;
        try
        {
            await _feature.OpenTitleAsync(card.Item, CancellationToken.None);
        }
        catch (Exception exception)
        {
            StatusLine.Text = "Open failed: " + exception.GetBaseException().Message;
        }
    }

    private void TitleRow_OpenClick(object sender, RoutedEventArgs e) => TitleCard_Click(sender, e);

    private void ViewModeSelector_ModeRequested(object? sender, MangaViewMode mode)
    {
        if (_disposed || _viewMode == mode) return;
        _viewMode = mode;
        _viewModeStore.Save(mode, out _);
        ApplyViewMode();
    }

    private void ApplyViewMode()
    {
        if (_disposed) return;
        var list = _viewMode == MangaViewMode.List;
        ViewModeSelector.Mode = _viewMode;
        ResultsScroll.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        ResultsTable.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DetailBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_feature is null) return;
        _restoreScrollPending = true;
        _feature.CloseDetail();
    }

    private async void GroupBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDetailEvents || _feature is null) return;
        if (GroupBox.SelectedItem is not CatalogDetailGroup group) return;

        try
        {
            await _feature.SelectGroupAsync(group.GroupId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            DetailStatus.Text = "Group failed: " + exception.GetBaseException().Message;
        }
    }

    private void ChapterCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressDetailEvents || _feature is null) return;
        if ((sender as FrameworkElement)?.Tag is not string chapterId) return;
        if (sender is not CheckBox box || box.IsChecked is not bool selected) return;

        _feature.SetChapterSelected(chapterId, selected);
    }

    private void SelectAllCheckBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box) _selectAllBox = box;
    }

    private void SelectAllCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressDetailEvents || _feature is null) return;
        if (sender is not CheckBox box || box.IsChecked is not bool selected) return;

        _feature.SelectAllChapters(selected);
    }

    private void QueueSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_feature is null || _confirmFolder is null) return;

        var detail = _feature.Current.Detail;
        if (detail is null) return;

        var folder = _confirmFolder(new CatalogQueueTargetRequest(
            detail.Title.SourceId,
            detail.Title.TitleId,
            detail.Title.TitleHid,
            detail.Display?.Title ?? detail.Title.Title));
        if (folder is null)
        {
            DetailStatus.Text = "Queue cancelled: no target folder.";
            return;
        }

        var handoff = _feature.TryBuildHandoff(folder);
        if (handoff is null)
        {
            DetailStatus.Text = "Select at least one chapter first.";
            return;
        }

        QueueHandoffRequested?.Invoke(this, handoff);
    }

    private void RenderPagination(CatalogMirrorState state)
    {
        var page = state.Results?.Page ?? 1;
        var total = state.Results?.TotalPages ?? 1;
        PageLabel.Text = $"Page {page} of {total}";
        PreviousPageButton.IsEnabled = state.Results is not null && page > 1;
        NextPageButton.IsEnabled = state.Results is not null && page < total;
    }

    private void BuildRatingOptions()
    {
        _ratingOptions = RatingValues
            .Select(value => new MangaFilterOption(
                value.ToString().ToLowerInvariant(), value.ToString()))
            .ToList();
        RatingFilter.Options = _ratingOptions;
    }

    private static void RebuildOptions(
        MangaMultiSelectFilter control,
        ref List<MangaFilterOption> current,
        IReadOnlyList<CatalogGenreOption> values)
    {
        if (current.Count == values.Count
            && current.Select(option => option.Key)
                .SequenceEqual(values.Select(value => value.Key), StringComparer.Ordinal))
        {
            return;
        }

        var selected = new HashSet<string>(control.SelectedKeys, StringComparer.Ordinal);
        current = values.Select(value => new MangaFilterOption(value.Key, value.DisplayName)).ToList();
        control.Options = current;
        control.SetSelectedKeys(selected);
    }

    private void FiltersButton_Click(object sender, RoutedEventArgs e) =>
        FilterPanel.Visibility = FilterPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) ApplyPanelQuery(1);
    }

    private void ApplyFiltersButton_Click(object sender, RoutedEventArgs e) => ApplyPanelQuery(1);

    private void ResetFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        SearchField.Text = string.Empty;
        YearFromField.Text = string.Empty;
        YearToField.Text = string.Empty;
        MinChapterField.Text = string.Empty;
        SortBox.SelectedIndex = 0;
        RatingFilter.SetSelectedKeys([]);
        TypeFilter.SetSelectedKeys([]);
        StatusFilter.SetSelectedKeys([]);
        LanguageFilter.SetSelectedKeys([]);
        GenreFilter.SetSelectedKeys([]);
        ApplyPanelQuery(1);
    }

    private void LocalFilter_Changed(object? sender, EventArgs e) => ApplyPanelQuery(1);

    private void SearchButton_Click(object sender, RoutedEventArgs e) => ApplyPanelQuery(1);

    private void SearchField_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        ApplyPanelQuery(1);
    }

    private void PreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_feature is null) return;
        var page = Math.Max(1, _feature.Current.Results?.Page - 1 ?? 1);
        ApplyPanelQuery(page);
    }

    private void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_feature is null) return;
        var page = (_feature.Current.Results?.Page ?? 1) + 1;
        ApplyPanelQuery(page);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_feature is null) return;
        try
        {
            await _feature.RefreshAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            StatusLine.Text = "Refresh failed: " + exception.GetBaseException().Message;
        }
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_feature is null) return;

        LoadButton.IsEnabled = false;
        LoadButton.Content = "Loading…";
        _resetScrollPending = true;
        try
        {
            if (!await _feature.LoadCatalogAsync(CancellationToken.None))
            {
                StatusLine.Text = "No catalog data is available in the database yet.";
            }
        }
        catch (Exception exception)
        {
            StatusLine.Text = "Load failed: " + exception.GetBaseException().Message;
        }
        finally
        {
            LoadButton.Content = "Load Catalog";
            LoadButton.IsEnabled = true;
        }
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)    {
        if (_feature is null) return;
        try
        {
            if (_context is not null)
            {
                _context.Browser.ShowBrowser = ShowBrowserToggle.IsChecked == true;
            }
            var state = _feature.Current.Sync.State;
            if (state == CatalogSyncState.Syncing)
            {
                _feature.RequestStopSync();
            }
            else if (state is CatalogSyncState.Stopped or CatalogSyncState.Error)
            {
                await _feature.ResumeSyncAsync(CancellationToken.None);
            }
            else if (state is not CatalogSyncState.Stopping)
            {
                await _feature.StartSyncAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            StatusLine.Text = "Action failed: " + exception.GetBaseException().Message;
        }
    }

    private async void StartOnlineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _context is null || _onlineStart is not null) return;

        var cancellation = new CancellationTokenSource();
        _onlineStart = cancellation;
        StartOnlineButton.IsEnabled = false;
        StatusLine.Text = "Starting Catalog provider process…";
        try
        {
            await _context.StartOnlineAsync(
                ShowBrowserToggle.IsChecked == true,
                cancellation.Token);
            StatusLine.Text = ShowBrowserToggle.IsChecked == true
                ? "Catalog provider is running with a visible browser."
                : "Catalog provider is running headless.";
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "Catalog provider stopped.";
        }
        catch (Exception exception)
        {
            StatusLine.Text = "Start failed: " + exception.GetBaseException().Message;
        }
        finally
        {
            if (ReferenceEquals(_onlineStart, cancellation)) _onlineStart = null;
            cancellation.Dispose();
            if (!_disposed) StartOnlineButton.IsEnabled = true;
        }
    }

    private void StopOnlineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _context is null) return;
        _onlineStart?.Cancel();
        _context.StopOnline();
        StatusLine.Text = "Catalog provider process stopped.";
    }

    private void WarningsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_feature is null) return;
        var warnings = _feature.LastWarnings;
        if (warnings.Count == 0) return;

        const int maxShown = 20;
        var text = string.Join("\n\n", warnings.Take(maxShown));
        if (warnings.Count > maxShown)
        {
            text += $"\n\n…and {warnings.Count - maxShown} more.";
        }

        SettingDialog.Confirm(
            Window.GetWindow(this),
            "Sync warnings",
            text,
            "Close");
    }

    private void ApplyPanelQuery(int page)
    {
        if (!_ready || _feature is null || _disposed) return;
        var filter = ReadFilter();
        if (filter is null) return;

        _resetScrollPending = true;

        var sort = SortBox.SelectedIndex switch
        {
            1 => CatalogTitleSort.TitleAsc,
            2 => CatalogTitleSort.TitleDesc,
            3 => CatalogTitleSort.YearNewest,
            4 => CatalogTitleSort.YearOldest,
            5 => CatalogTitleSort.LatestChapterHighest,
            6 => CatalogTitleSort.LatestChapterLowest,
            _ => CatalogTitleSort.LatestUpdate,
        };

        var search = string.IsNullOrWhiteSpace(SearchField.Text) ? null : SearchField.Text;
        _ = _feature.QueryAsync(new CatalogMirrorQueryRequest(search, filter, sort, page), CancellationToken.None);
    }

    private CatalogMirrorFilter? ReadFilter()
    {
        var ratings = RatingFilter.SelectedKeys
            .Select(value => Enum.TryParse<CatalogContentRating>(value, true, out var rating)
                ? rating
                : (CatalogContentRating?)null)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();

        if (!TryNumber(YearFromField.Text, 1900, 2999, out var yearFrom)
            || !TryNumber(YearToField.Text, 1900, 2999, out var yearTo)
            || (yearFrom is not null && yearTo is not null && yearFrom > yearTo)
            || !TryNumber(MinChapterField.Text, 0, int.MaxValue, out var minChapter))
        {
            StatusLine.Text = "Invalid filter numbers; query not applied.";
            return null;
        }

        return new CatalogMirrorFilter(
            ratings,
            TypeFilter.SelectedKeys,
            StatusFilter.SelectedKeys,
            LanguageFilter.SelectedKeys,
            GenreFilter.IsEnabled ? GenreFilter.SelectedKeys : [],
            yearFrom,
            yearTo,
            minChapter);
    }

    private async void GenreSyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (_feature is null) return;
        var state = _feature.Current.GenreSync?.State;
        if (state == CatalogGenreSyncState.Syncing)
        {
            _feature.RequestStopGenreSync();
        }
        else if (state is CatalogGenreSyncState.Stopped or CatalogGenreSyncState.Error)
        {
            await _feature.ResumeGenreSyncAsync(CancellationToken.None);
        }
        else if (state != CatalogGenreSyncState.Stopping)
        {
            await _feature.StartGenreSyncAsync(CancellationToken.None);
        }
    }

    private static bool TryNumber(string text, int min, int max, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!int.TryParse(text.Trim(), out var parsed) || parsed < min || parsed > max) return false;
        value = parsed;
        return true;
    }
}
