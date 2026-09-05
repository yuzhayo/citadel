using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Citadel.Setting.Components;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Features.Downloader.Sources;

namespace Module.Mangareader.Features.Downloader.Catalog;

/// <summary>
/// Catalog presentation. It translates input into <see cref="CatalogFeature"/>
/// calls and renders the feature's immutable snapshot; it owns no query,
/// result, detail or selection state of its own, and it never builds provider
/// URLs.
/// </summary>
public partial class CatalogScreen : UserControl, IDisposable
{
    private static readonly HttpClient CoverClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>
    /// Library decodes its card covers at 320 px; matching that keeps the two
    /// grids visually identical. This is a presentation value of this screen,
    /// not a reference to the Library loader, whose constant documents a local
    /// render-cache invariant that does not apply to remote covers.
    /// </summary>
    private const int CoverPixelWidth = 320;

    private readonly ObservableCollection<RemoteTitleCardModel> _cards = [];
    private readonly Dictionary<string, RemoteTitleCardModel> _cardsByIdentity = new(StringComparer.Ordinal);
    private DownloaderContext? _context;
    private CatalogFeature? _catalog;
    private CancellationTokenSource? _actionCancellation;
    private CancellationTokenSource? _coverCancellation;
    private CancellationTokenSource? _coverBatch;
    private int _coverGeneration;
    private bool _settingSource;
    private bool _settingGroup;
    private bool _settingChapters;
    private bool _disposed;

    public CatalogScreen()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _cards;
    }

    /// <summary>The only entry point to Download List.</summary>
    public event EventHandler? OpenDownloadList;

    public void UseContext(DownloaderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_disposed || _context is not null) return;

        _context = context;
        _catalog = new CatalogFeature(context.Sources, () => context.LibraryRoot.CurrentRoot is not null);
        _catalog.StateChanged += Catalog_StateChanged;
        context.Queue.QueueSummaryChanged += Queue_QueueSummaryChanged;

        _settingSource = true;
        try
        {
            SourcePicker.ItemsSource = _catalog.AvailableSources;
            SourcePicker.SelectedItem = _catalog.AvailableSources.FirstOrDefault();
        }
        finally
        {
            _settingSource = false;
        }

        if (SourcePicker.SelectedItem is MangaSourceRegistration selected)
        {
            _catalog.SelectSource(selected.Id);
        }

        Render(_catalog.State);
        RenderBadge(context.Queue.Summary());
    }

    private void SourcePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_disposed || _settingSource || _catalog is null) return;
        if (SourcePicker.SelectedItem is not MangaSourceRegistration registration) return;

        // Selecting a provider performs no remote request and starts no browser.
        _catalog.SelectSource(registration.Id);
        HostFilterPanel(registration);
    }

    private void HostFilterPanel(MangaSourceRegistration registration)
    {
        var composition = registration.CreateFilters();
        FilterHost.Content = composition.CreatePanel();
        FilterHost.Visibility = FiltersToggle.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void FiltersToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        FilterHost.Visibility = FiltersToggle.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SearchField_KeyDown(object sender, KeyEventArgs e)
    {
        // Typing never sends a request; an explicit Enter is a Start.
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            StartButton_Click(sender, e);
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _catalog is null) return;
        await RunActionAsync(token => _catalog.StartAsync(SearchField.Text, token));
    }

    private async void LoadMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _catalog is null) return;
        await RunActionAsync(_catalog.LoadMoreAsync);
    }

    private async void TitleCard_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _catalog is null) return;
        if ((sender as FrameworkElement)?.Tag is not RemoteTitleCardModel card) return;

        // Remember the anchor so Back restores the exact grid position without
        // a new fetch.
        _catalog.RememberScrollOffset(ResultsScroll.VerticalOffset);
        await RunActionAsync(async token =>
        {
            await _catalog.OpenTitleAsync(card.Summary, token);
            await LoadDetailCoverAsync(card.Summary, token);
        });
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_catalog is null) return;
        var anchor = _catalog.State.ResultsScrollOffset;
        _catalog.Back();
        Dispatcher.BeginInvoke(() => ResultsScroll.ScrollToVerticalOffset(anchor));
    }

    private async void GroupPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_disposed || _settingGroup || _catalog is null) return;
        var state = _catalog.State;
        if (state.Detail is null || GroupPicker.SelectedItem is not RemoteSourceGroup group) return;

        await RunActionAsync(token =>
            _catalog.SelectGroupAsync(state.Detail.Summary.Identity, group, token));
    }

    private void ChapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_disposed || _settingChapters || _catalog is null) return;

        // The feature owns the selection; this handler only reports what the
        // shared list control now holds.
        var selected = ChapterList.SelectedItems
            .OfType<RemoteChapterSummary>()
            .Select(chapter => chapter.Identity.ChapterId)
            .ToList();
        foreach (var chapter in _catalog.State.Chapters)
        {
            var shouldBeSelected = selected.Contains(chapter.Identity.ChapterId);
            var isSelected = _catalog.State.SelectedChapterIds.Contains(chapter.Identity.ChapterId);
            if (shouldBeSelected && !isSelected) _catalog.ToggleChapter(chapter);
        }
    }

    private void DownloadListButton_Click(object sender, RoutedEventArgs e) =>
        OpenDownloadList?.Invoke(this, EventArgs.Empty);

    private void QueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null || _catalog is null) return;
        var state = _catalog.State;
        if (state.Detail is null || state.SelectedGroup is null) return;

        var root = _context.LibraryRoot.CurrentRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            SetStatus("Library root belum diatur; pilih folder di tab Library lebih dulu.", isError: true);
            return;
        }

        var group = state.Groups.FirstOrDefault(candidate =>
            candidate.Identity == state.SelectedGroup);
        if (group is null) return;

        var chapters = state.Chapters
            .Where(chapter => state.SelectedChapterIds.Contains(chapter.Identity.ChapterId))
            .ToList();
        if (chapters.Count == 0) return;

        var folder = ResolveTargetFolder(state.Detail.Summary, root);
        if (folder is null) return;

        QueueAddResult result;
        try
        {
            result = _context.Queue.QueueChapters(state.Detail.Summary, group, chapters, folder);
        }
        catch (QueuePersistenceException exception)
        {
            // A queue that cannot be written down must not accept the job.
            SetStatus("Queue tidak dapat disimpan: " + exception.Message, isError: true);
            return;
        }

        if (result.Blocked is not null)
        {
            SetStatus(result.Blocked, isError: true);
            return;
        }

        _catalog.ClearSelection();
        SetStatus(
            result.SkippedAlreadyPublished > 0
                ? $"{result.Queued} chapter di-queue; {result.SkippedAlreadyPublished} sudah pernah dipublikasikan."
                : $"{result.Queued} chapter di-queue ke '{folder}'.",
            isError: false);
        OpenDownloadList?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Confirms one local folder for this remote title. An existing mapping is
    /// reused while its folder still exists; a same-named folder that is not
    /// mapped is never claimed silently, and declining produces a distinct,
    /// identity-bearing folder name rather than a "(2)" suffix.
    /// </summary>
    private string? ResolveTargetFolder(RemoteTitleSummary title, string root)
    {
        if (_context is null) return null;

        var existing = _context.Index.FindUsableMapping(root, title.Identity);
        if (existing is not null) return existing.FolderName;

        var suggested = DownloadQueueFeature.SanitizeFolder(title.DisplayName);
        if (!Directory.Exists(Path.Combine(root, suggested))) return suggested;

        var claimed = SettingDialog.Confirm(
            Window.GetWindow(this),
            "Downloader",
            $"Folder '{suggested}' sudah ada di Library tetapi tidak dipetakan ke title remote ini.\n\nGunakan folder itu untuk '{title.DisplayName}'?",
            "Use folder");
        if (claimed) return suggested;

        var distinct = $"{suggested} [{title.Identity.TitleHid}]";
        if (Directory.Exists(Path.Combine(root, distinct)))
        {
            SetStatus(
                $"Folder '{distinct}' juga sudah ada. Pilih nama lain dari tab Library lalu ulangi.",
                isError: true);
            return null;
        }

        return distinct;
    }

    private void Catalog_StateChanged(object? sender, EventArgs e)
    {
        if (_disposed || _catalog is null) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Catalog_StateChanged(sender, e));
            return;
        }

        Render(_catalog.State);
    }

    private void Queue_QueueSummaryChanged(object? sender, EventArgs e)
    {
        if (_disposed || _context is null) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Queue_QueueSummaryChanged(sender, e));
            return;
        }

        RenderBadge(_context.Queue.Summary());
    }

    private void RenderBadge(QueueSummary summary) =>
        DownloadListButton.Content = $"Download List ({summary.BadgeCount})";

    private void Render(CatalogState state)
    {
        StartButton.IsEnabled = !state.IsBusy && state.SelectedSourceId is not null;
        LoadMoreButton.IsEnabled = !state.IsBusy && state.CanLoadMore;
        SourcePicker.IsEnabled = !state.IsBusy;
        SearchField.IsEnabled = !state.IsBusy;
        FiltersToggle.IsEnabled = !state.IsBusy;

        RemoteStateText.Text = state.IsBusy
            ? "Requesting…"
            : state.Results.Count > 0 || state.IsDetailOpen
                ? "Idle — no request"
                : "Idle — no request";

        ResultsPanel.Visibility = state.IsDetailOpen ? Visibility.Collapsed : Visibility.Visible;
        DetailPanel.Visibility = state.IsDetailOpen ? Visibility.Visible : Visibility.Collapsed;

        if (!state.IsDetailOpen)
        {
            RenderCards(state.Results);
            var empty = _cards.Count == 0;
            ResultsEmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            ResultsEmptyTitle.Text = state.ErrorMessage is null && empty
                ? "No results yet"
                : "No results";
        }
        else
        {
            RenderDetail(state);
        }

        SetStatus(
            state.ErrorMessage ?? state.StatusMessage,
            isError: state.ErrorMessage is not null);
    }

    private void RenderDetail(CatalogState state)
    {
        if (state.Detail is null) return;

        DetailHeader.Children.Clear();
        DetailHeader.Children.Add(Heading(state.Detail.Summary.DisplayName));
        if (!string.IsNullOrWhiteSpace(state.Detail.Description))
        {
            DetailHeader.Children.Add(Body(state.Detail.Description!));
        }

        if (state.Detail.Genres.Count > 0)
        {
            DetailHeader.Children.Add(Body(
                "Genres: " + string.Join(", ", state.Detail.Genres.Select(genre => genre.DisplayName))));
        }

        foreach (var metadata in state.Detail.Metadata)
        {
            DetailHeader.Children.Add(Body(metadata.DisplayName + ": " + metadata.Key));
        }

        _settingGroup = true;
        try
        {
            GroupPicker.ItemsSource = state.Groups;
            GroupPicker.SelectedItem = state.Groups.FirstOrDefault(group =>
                group.Identity == state.SelectedGroup) ?? state.Groups.FirstOrDefault();
        }
        finally
        {
            _settingGroup = false;
        }

        _settingChapters = true;
        try
        {
            ChapterList.ItemsSource = state.Chapters;
            ChapterList.SelectedItems.Clear();
            foreach (var chapter in state.Chapters)
            {
                if (state.SelectedChapterIds.Contains(chapter.Identity.ChapterId))
                {
                    ChapterList.SelectedItems.Add(chapter);
                }
            }
        }
        finally
        {
            _settingChapters = false;
        }

        QueueButton.IsEnabled = state.HasSelection && state.HasLibraryRoot && !state.IsBusy;
    }

    /// <summary>
    /// Syncs the bound card collection in place: one adapter instance per remote
    /// identity survives a re-render, so Back and Load more never re-download a
    /// cover that already arrived and appending never resets the scroll anchor.
    /// </summary>
    private void RenderCards(IReadOnlyList<RemoteTitleSummary> results)
    {
        var desired = new List<RemoteTitleCardModel>(results.Count);
        foreach (var summary in results)
        {
            var identity = summary.Identity;
            var key = identity.SourceId + "|" + identity.TitleId + "|" + identity.TitleHid;
            if (_cardsByIdentity.TryGetValue(key, out var card))
            {
                card.Summary = summary;
            }
            else
            {
                card = new RemoteTitleCardModel(summary);
                _cardsByIdentity[key] = card;
            }

            desired.Add(card);
        }

        for (var index = 0; index < desired.Count; index++)
        {
            if (index < _cards.Count)
            {
                if (!ReferenceEquals(_cards[index], desired[index])) _cards[index] = desired[index];
            }
            else
            {
                _cards.Add(desired[index]);
            }
        }

        while (_cards.Count > desired.Count) _cards.RemoveAt(_cards.Count - 1);

        LoadCovers();
    }

    /// <summary>
    /// Covers load only after results exist, never on open or on filter edits,
    /// and only one batch runs at a time.
    /// </summary>
    private void LoadCovers()
    {
        if (_disposed || _coverBatch is not null) return;

        var pending = _cards
            .Where(card => card.Cover is null && card.CoverUrl.Length > 0)
            .ToList();
        if (pending.Count == 0) return;

        var batch = new CancellationTokenSource();
        _coverBatch = batch;
        _ = RunCoverBatchAsync(pending, batch);
    }

    private async Task RunCoverBatchAsync(
        IReadOnlyList<RemoteTitleCardModel> pending,
        CancellationTokenSource batch)
    {
        var options = new ParallelOptions
        {
            CancellationToken = batch.Token,
            MaxDegreeOfParallelism = 4,
        };

        try
        {
            await Parallel.ForEachAsync(pending, options, async (card, cancellationToken) =>
            {
                BitmapSource? cover;
                try
                {
                    cover = await LoadCoverAsync(card.CoverUrl, cancellationToken).ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One unreachable cover leaves its card on the placeholder;
                    // the rest of the catalog is unaffected.
                    return;
                }

                if (cover is null || _disposed || !ReferenceEquals(_coverBatch, batch)) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_disposed && ReferenceEquals(_coverBatch, batch)) card.Cover = cover;
                });
            });
        }
        catch (Exception)
        {
            // Covers are cosmetic: a superseded, cancelled or unexpected batch
            // must not escape as an unobserved exception.
        }
        finally
        {
            if (ReferenceEquals(_coverBatch, batch)) _coverBatch = null;
            batch.Dispose();
        }

        // A second Start or a Load more may have added cards while this batch
        // ran; one follow-up pass picks them up without a concurrent batch.
        if (!_disposed) LoadCovers();
    }

    /// <summary>
    /// Decodes a remote cover at the same width Library uses for its card
    /// covers, so the two grids render identically.
    /// </summary>
    private static async Task<BitmapSource?> LoadCoverAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var bytes = await CoverClient.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(true);
        if (bytes.Length == 0) return null;

        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.DecodePixelWidth = CoverPixelWidth;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Loads the detail cover off the UI thread. A stale response is dropped, so
    /// selecting another title can never paint the previous cover.
    /// </summary>
    private async Task LoadDetailCoverAsync(RemoteTitleSummary title, CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _coverGeneration);
        if (string.IsNullOrWhiteSpace(title.CoverUrl)) return;

        var previous = _coverCancellation;
        var cancellation = new CancellationTokenSource();
        _coverCancellation = cancellation;
        previous?.Cancel();

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, cancellation.Token);
            var bytes = await CoverClient.GetByteArrayAsync(title.CoverUrl, linked.Token)
                .ConfigureAwait(true);
            if (_disposed || generation != Volatile.Read(ref _coverGeneration)) return;

            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            if (generation != Volatile.Read(ref _coverGeneration)) return;
            var cover = new Image
            {
                Source = image,
                Width = 132,
                Height = 180,
                Stretch = System.Windows.Media.Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10),
            };
            DetailHeader.Children.Insert(0, cover);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A missing cover is cosmetic; the detail stays usable without it.
        }
        finally
        {
            if (ReferenceEquals(_coverCancellation, cancellation))
            {
                _coverCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private static TextBlock Heading(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        return block;
    }

    private static TextBlock Body(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Body");
        return block;
    }

    private async Task RunActionAsync(Func<CancellationToken, Task> action)
    {
        var previous = _actionCancellation;
        var cancellation = new CancellationTokenSource();
        _actionCancellation = cancellation;
        previous?.Cancel();

        try
        {
            await action(cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_actionCancellation, cancellation))
            {
                _actionCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void SetStatus(string? message, bool isError)
    {
        StatusText.Text = isError && !string.IsNullOrWhiteSpace(message)
            ? "Error: " + message
            : message ?? string.Empty;
        StatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusText.SetResourceReference(
            TextBlock.ForegroundProperty,
            isError ? "Dim" : "Accent");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_catalog is not null) _catalog.StateChanged -= Catalog_StateChanged;
        if (_context is not null) _context.Queue.QueueSummaryChanged -= Queue_QueueSummaryChanged;

        var actionCancellation = _actionCancellation;
        _actionCancellation = null;
        actionCancellation?.Cancel();

        var coverCancellation = _coverCancellation;
        _coverCancellation = null;
        coverCancellation?.Cancel();
        Interlocked.Increment(ref _coverGeneration);

        // Same ownership rule as the CTS fix: take the field, clear it, then
        // cancel. The batch's own finally disposes it, so nothing here can call
        // into a disposed source.
        var coverBatch = _coverBatch;
        _coverBatch = null;
        coverBatch?.Cancel();

        _cards.Clear();
        _cardsByIdentity.Clear();
    }
}
