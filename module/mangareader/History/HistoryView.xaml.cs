using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Module.Mangareader.History;
using Module.Mangareader.Library;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>
/// History presentation. It hosts the shared action bar and both presentations
/// over one collection, and forwards every command to the feature that owns it.
/// No clear, pin, retention, ordering or persistence rule lives here.
/// </summary>
public partial class HistoryView : UserControl, IDisposable
{
    private readonly ObservableCollection<HistoryCardModel> _history = new();
    private readonly MangaCoverLoader _coverLoader = new();
    private readonly ChapterRenderCache _renderCache = new();
    private readonly HistoryViewModeFeature _viewMode = new();
    private readonly LibraryTitleLoader _titles = new();
    private readonly Dictionary<string, MangaTitle> _resolved =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<LibraryIndexEntry> _entries = [];
    private ReadingHistory? _readingHistory;
    private ClearHistoryFeature? _clearHistory;
    private PinnedHistoryFeature? _pinnedHistory;
    private CancellationTokenSource? _coverCancellation;
    private CancellationTokenSource? _refreshCancellation;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _needsRefresh = true;
    private bool _restored;
    private bool _disposed;

    public HistoryView()
    {
        InitializeComponent();

        // One collection feeds both presentations, so switching between them
        // cannot lose data, order or pin state and never mutates history.
        HistoryGrid.ItemsSource = _history;
        HistoryTable.ItemsSource = _history;

        ViewModeSelector.Mode = _viewMode.Mode;
        ViewModeSelector.ModeRequested += ViewModeSelector_ModeRequested;
        _viewMode.Changed += ViewMode_Changed;
        ApplyViewMode();
        UpdateActionBar();

        Loaded += HistoryView_Loaded;
        IsVisibleChanged += HistoryView_IsVisibleChanged;
    }

    public event EventHandler<OpenChapterRequestedEventArgs>? OpenChapterRequested;

    /// <summary>
    /// Attaches the History feature's recording owner. The composition root
    /// holds it, so chapter events are recorded without routing through this
    /// screen and history is written even before this tab is opened. Clear and
    /// Pin are built here because they mutate through that same single owner.
    /// </summary>
    public void UseHistory(ReadingHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (ReferenceEquals(_readingHistory, history)) return;

        if (_readingHistory is not null)
        {
            _readingHistory.Changed -= ReadingHistory_Changed;
        }

        _readingHistory = history;
        _readingHistory.Changed += ReadingHistory_Changed;
        _clearHistory = new ClearHistoryFeature(history);
        _pinnedHistory = new PinnedHistoryFeature(history);
        UpdateActionBar();
    }

    /// <summary>
    /// Receives the index snapshot. History resolves full titles lazily —
    /// one folder read per recorded title actually on screen — so a large
    /// library never pays for chapters nobody revisits.
    /// </summary>
    public void SetLibrary(IReadOnlyList<LibraryIndexEntry> entries)
    {
        _entries = entries ?? [];
        _resolved.Clear();
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }
        _needsRefresh = true;
        _refreshCancellation?.Cancel();
        _coverCancellation?.Cancel();
        if (!IsVisible) return;
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        _ = RefreshAsync(cancellation);
    }

    private async Task RefreshAsync(CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var owner = _readingHistory;
        var entries = _entries.ToDictionary(entry => entry.FolderPath, StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, MangaTitle>(_resolved, StringComparer.OrdinalIgnoreCase);
        var oldCards = _history.ToArray();
        var entered = false;
        try
        {
            await _refreshGate.WaitAsync(token);
            entered = true;
            var cards = await Task.Run(() =>
            {
                var result = new List<HistoryCardModel>();
                var reusableCovers = SnapshotCovers(oldCards);
                foreach (var entry in owner?.Read() ?? [])
                {
                    token.ThrowIfCancellationRequested();
                    if (!entries.TryGetValue(entry.TitleFolderPath, out var indexed)) continue;
                    if (!resolved.TryGetValue(indexed.FolderPath, out var title))
                    {
                        try
                        {
                            title = _titles.LoadTitle(indexed.FolderPath, cancellationToken: token);
                        }
                        catch (Exception exception) when (exception is IOException
                            or UnauthorizedAccessException or ArgumentException)
                        {
                            continue;
                        }
                        if (title is null) continue;
                        resolved[indexed.FolderPath] = title;
                    }

                    var chapter = title.Chapters.FirstOrDefault(candidate => string.Equals(
                        candidate.FilePath, entry.ChapterFilePath, StringComparison.OrdinalIgnoreCase));
                    if (chapter is null) continue;
                    var card = new HistoryCardModel(title, chapter, entry.LastOpenedUtc, entry.Pinned);
                    var coverKey = CoverKey(title);
                    if (coverKey is not null && reusableCovers.TryGetValue(coverKey, out var cover))
                        card.Cover = cover;
                    result.Add(card);
                }
                return result;
            }, token);

            if (_disposed || !IsVisible || token.IsCancellationRequested
                || !ReferenceEquals(_refreshCancellation, cancellation)) return;
            _resolved.Clear();
            foreach (var pair in resolved) _resolved[pair.Key] = pair.Value;
            _history.Clear();
            foreach (var card in cards) _history.Add(card);
            _needsRefresh = false;
            EmptyPanel.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateActionBar();
            LoadMissingCovers();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_disposed && IsVisible && ReferenceEquals(_refreshCancellation, cancellation))
                SetStatus($"Could not load history: {exception.GetBaseException().Message}");
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation)) _refreshCancellation = null;
            cancellation.Dispose();
            if (entered) _refreshGate.Release();
        }
    }

    private void HistoryView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || _restored) return;
        _restored = true;
        _viewMode.Restore();
    }

    private void HistoryView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (IsVisible)
        {
            if (_needsRefresh) Refresh();
            else LoadMissingCovers();
        }
        else
        {
            _refreshCancellation?.Cancel();
            _coverCancellation?.Cancel();
        }
    }

    private void HistoryCard_Click(object sender, RoutedEventArgs e) => OpenCard(sender);

    /// <summary>The List row action opens the same entry as the Grid card.</summary>
    private void HistoryRow_OpenClick(object sender, RoutedEventArgs e) => OpenCard(sender);

    private void OpenCard(object sender)
    {
        if (sender is not FrameworkElement { Tag: HistoryCardModel card }) return;
        OpenChapterRequested?.Invoke(
            this,
            new OpenChapterRequestedEventArgs(card.Manga, card.Chapter));
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _pinnedHistory is null) return;
        if (sender is not FrameworkElement { Tag: HistoryCardModel card }) return;

        var result = _pinnedHistory.Toggle(card.Manga.FolderPath);
        if (result.Succeeded)
        {
            SetStatus(null);
            return;
        }

        // The failure stays inside History; the durable file is untouched.
        SetStatus(result.Error);
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _clearHistory is null) return;

        var result = _clearHistory.Clear();
        SetStatus(result.Succeeded
            ? $"{result.Removed} history dihapus. Entri yang di-pin dipertahankan."
            : result.Error);
        UpdateActionBar();
    }

    private void ViewModeSelector_ModeRequested(object? sender, MangaViewMode mode) =>
        _viewMode.Select(mode);

    private void ViewMode_Changed(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ApplyViewMode);
            return;
        }

        ApplyViewMode();
    }

    /// <summary>
    /// Switches presentation only. Both panels stay in the tree over the same
    /// collection, so data, order, pin state and scroll position survive.
    /// </summary>
    private void ApplyViewMode()
    {
        if (_disposed) return;

        var list = _viewMode.Mode == MangaViewMode.List;
        ViewModeSelector.Mode = _viewMode.Mode;
        GridScroll.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        HistoryTable.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateActionBar()
    {
        if (_disposed) return;
        ClearHistoryButton.IsEnabled = _clearHistory?.CanClear == true;
    }

    private void SetStatus(string? message)
    {
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Loaded -= HistoryView_Loaded;
        IsVisibleChanged -= HistoryView_IsVisibleChanged;
        _viewMode.Changed -= ViewMode_Changed;
        ViewModeSelector.ModeRequested -= ViewModeSelector_ModeRequested;

        if (_readingHistory is not null)
        {
            _readingHistory.Changed -= ReadingHistory_Changed;
        }

        _coverCancellation?.Cancel();
        _refreshCancellation?.Cancel();
        _history.Clear();
    }

    private void ReadingHistory_Changed(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Refresh();
    }

    private Dictionary<string, BitmapSource> SnapshotCovers(IReadOnlyList<HistoryCardModel> cards)
    {
        var covers = new Dictionary<string, BitmapSource>(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            if (card.Cover is null) continue;
            var key = CoverKey(card.Manga);
            if (key is not null) covers[key] = card.Cover;
        }

        return covers;
    }

    /// <summary>
    /// Identity of the rendered cover behind a title: the render cache folder
    /// of its first chapter, which is derived from that file's path, length
    /// and write time. A baked or replaced chapter therefore produces a
    /// different key, so its old cover is never carried over. Null when no
    /// chapter file can be read.
    /// </summary>
    private string? CoverKey(MangaTitle title)
    {
        var chapter = title.Chapters.FirstOrDefault();
        return chapter is not null && File.Exists(chapter.FilePath)
            ? _renderCache.GetChapterFolder(chapter)
            : null;
    }

    private void LoadMissingCovers()
    {
        var pending = _history.Where(card => card.Cover is null).ToArray();

        var previous = _coverCancellation;
        var cancellation = new CancellationTokenSource();
        _coverCancellation = cancellation;
        previous?.Cancel();

        _ = LoadCoversAsync(pending, cancellation);
    }

    private async Task LoadCoversAsync(
        IReadOnlyList<HistoryCardModel> cards,
        CancellationTokenSource cancellation)
    {
        var options = new ParallelOptions
        {
            CancellationToken = cancellation.Token,
            MaxDegreeOfParallelism = 4,
        };

        try
        {
            await Parallel.ForEachAsync(cards, options, async (card, token) =>
            {
                BitmapSource? cover;
                try
                {
                    cover = await _coverLoader.LoadAsync(
                        card.Manga,
                        MangaCoverLoader.PreviewPixelWidth,
                        token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return;
                }

                if (cover is null || _disposed || token.IsCancellationRequested
                    || !ReferenceEquals(_coverCancellation, cancellation)) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_disposed && !token.IsCancellationRequested && IsVisible
                        && ReferenceEquals(_coverCancellation, cancellation))
                    {
                        card.Cover = cover;
                    }
                });
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Covers are best effort: a card keeps its placeholder icon.
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
}
