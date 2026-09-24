using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Module.Mangareader.Library;
using Module.Mangareader.Library.Grouping;
using Module.Mangareader.Library.UpdateChecker;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

public partial class LibraryView : UserControl, IDisposable
{
    private static readonly LibrarySortOption[] SortOptions =
    [
        new("Latest added", nameof(MangaTitleCardModel.AddedUtc), ListSortDirection.Descending),
        new("Oldest added", nameof(MangaTitleCardModel.AddedUtc), ListSortDirection.Ascending),
        new("Title (A-Z)", nameof(MangaTitleCardModel.Title), ListSortDirection.Ascending),
        new("Title (Z-A)", nameof(MangaTitleCardModel.Title), ListSortDirection.Descending),
    ];

    private readonly LibraryIndexCoordinator _coordinator = new(covers: new LibraryCoverCache());
    private readonly ObservableCollection<MangaTitleCardModel> _cards = new();
    private readonly HashSet<string> _opening = new(StringComparer.OrdinalIgnoreCase);
    private LibraryIndexWatcher? _watcher;
    private long _coverGeneration;
    private readonly GroupingStore _groupingStore = new();
    private readonly GroupingFeature _grouping;
    private readonly LibraryViewModeFeature _viewMode = new();
    private readonly ICollectionView _titlesView;
    private UpdateCheckerEntry? _updateChecker;
    private LibraryRootContext? _root;
    private CancellationTokenSource? _scanCancellation;
    private bool _autoRestored;
    private bool _disposed;

    public LibraryView()
    {
        _grouping = new GroupingFeature(_groupingStore);
        InitializeComponent();

        // One view over the loaded cards carries the Grouping filter and the
        // column sort. Both presentations bind that same view, so switching
        // between them cannot lose a filter, a sort order or a loaded title,
        // and it never needs a scan.
        _titlesView = CollectionViewSource.GetDefaultView(_cards);
        _titlesView.Filter = candidate =>
            candidate is MangaTitleCardModel card && _grouping.IsVisible(card.Title);
        SortPicker.ItemsSource = SortOptions;
        SortPicker.SelectedIndex = 0;
        ApplySort(SortOptions[0]);
        TitleGrid.ItemsSource = _titlesView;
        TitleTable.ItemsSource = _titlesView;

        Grouping.UseGrouping(_grouping, LoadedTitleFolderNames);
        Grouping.InstallAddTitleAction(
            ChapterSelector.DetailActions,
            () => ChapterSelector.ActiveTitleFolderName);
        ChapterSelector.CoverBuilderRequested += ChapterSelector_CoverBuilderRequested;
        ChapterSelector.ResumeRequested += ChapterSelector_ResumeRequested;
        _grouping.Changed += Grouping_Changed;

        ViewModeSelector.Mode = _viewMode.Mode;
        ViewModeSelector.ModeRequested += ViewModeSelector_ModeRequested;
        _viewMode.Changed += ViewMode_Changed;
        ApplyViewMode();

        Loaded += LibraryView_Loaded;
    }

    /// <summary>
    /// Attaches the module-lifetime root owner. The parent injects this before
    /// Loaded, so the single restore runs against the shared context instead of
    /// a second reader of the preference file.
    /// </summary>
    public void UseLibraryRoot(LibraryRootContext root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (ReferenceEquals(_root, root)) return;

        _root = root;
    }

    /// <summary>
    /// Attaches the Update Checker entry point. The feature owns its action and
    /// its popup; this screen only offers the reserved header slot and a pull of
    /// the active local title, so no matching or checking rule lands here.
    /// </summary>
    public void UseUpdateChecker(UpdateCheckerFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (_disposed || _updateChecker is not null) return;

        _updateChecker = new UpdateCheckerEntry(feature);
        _updateChecker.Install(
            ChapterSelector.HeaderActions,
            () => ChapterSelector.ActiveTitleFolderName);
    }

    public event EventHandler<OpenChapterRequestedEventArgs>? OpenChapterRequested;

    public event EventHandler<OpenChapterRequestedEventArgs>? ResumeRequested;

    public event EventHandler<CoverBuilderRequestedEventArgs>? CoverBuilderRequested;

    public event EventHandler<LibraryChangedEventArgs>? TitlesChanged;

    public IReadOnlyList<MangaTitleCardModel> Titles => _cards.ToArray();

    public Task RefreshAsync() => RefreshLibraryAsync(LibraryPath.Text.Trim(), manual: true);

    private async void LibraryView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || _autoRestored || _root is null) return;
        _autoRestored = true;

        // Groups are independent of the root: a library path that is missing or
        // damaged must not cost the user their stored group definitions.
        _grouping.Restore();
        _viewMode.Restore();

        var loaded = _root.Restore();
        if (loaded.Path is not null)
        {
            LibraryPath.Text = loaded.Path;
            await RefreshLibraryAsync(loaded.Path, manual: false);
            return;
        }

        if (loaded.Warning is not null)
        {
            StatusText.Text = loaded.Warning;
        }
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var currentLibrary = LibraryPath.Text.Trim();
        var dialog = new OpenFolderDialog
        {
            Title = "Choose manga library folder",
            Multiselect = false,
            // Browse is a change-of-root action. When the current root still
            // exists, start there instead of the system's last/default folder.
            InitialDirectory = Directory.Exists(currentLibrary) ? currentLibrary : string.Empty,
        };

        var owner = Window.GetWindow(this);
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted != true) return;

        LibraryPath.Text = dialog.FolderName;
        await RefreshLibraryAsync(dialog.FolderName, manual: true);
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshLibraryAsync(LibraryPath.Text.Trim(), manual: true);

    /// <summary>
    /// Index-first refresh. Phase 1 paints the grid from the stored index —
    /// one small file, no chapter reads — so startup stays fast. Phase 2
    /// reconciles quietly in the background (the primary freshness source);
    /// only real grid movement repaints and re-notifies. A failed background
    /// pass keeps the painted grid and reports a warning instead of clearing
    /// anything. Covers are not loaded here; the cover cache increment
    /// restores them from thumbnails without opening archives.
    /// </summary>
    private async Task RefreshLibraryAsync(string path, bool manual)
    {
        if (_disposed || _root is null) return;

        if (path.Length == 0)
        {
            ShowEmpty(
                "No library selected",
                "Choose the folder that contains one child folder per manga title.");
            StatusText.Text = "A library path is required.";
            return;
        }

        // The path is captured once, at refresh start. Completion persists
        // this attempt, never whatever the field says later.
        var attempt = _root.BeginScan(path);

        var previous = _scanCancellation;
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        previous?.Cancel();

        // A refresh owns a new root view of the world: stale-root watcher
        // events must never land after this point.
        _watcher?.Dispose();
        _watcher = null;

        ChapterSelector.Dismiss();
        SetBusy(true);
        StatusText.Text = manual ? "Updating library index…" : "Loading indexed titles…";

        try
        {
            _coordinator.Reload(path);
            if (_disposed || !ReferenceEquals(_scanCancellation, cancellation)) return;

            FillCards(_coordinator.Entries);
            NotifyTitlesChanged(_coordinator.Entries);
            var painted = _coordinator.Entries.Count;
            CompleteSuccessfulScan(cancellation, attempt, StatusForCount(painted));
            UpdateGroupFilter();
            SetBusy(false);
            _ = LoadCachedCoversAsync(_cards.ToArray(), cancellation.Token);

            if (painted == 0)
            {
                StatusText.Text = "Building library index…";
            }
            else
            {
                StatusText.Text = "Checking library changes…";
            }

            var reconcile = await Task.Run(
                () => _coordinator.Reconcile(path, cancellation.Token),
                cancellation.Token);
            if (_disposed || !ReferenceEquals(_scanCancellation, cancellation)) return;

            if (reconcile.Added > 0 || reconcile.Updated > 0 || reconcile.Removed > 0)
            {
                FillCards(_coordinator.Entries);
                NotifyTitlesChanged(_coordinator.Entries);
                UpdateGroupFilter();
                _ = LoadCachedCoversAsync(_cards.ToArray(), cancellation.Token);
            }

            if (_coordinator.Entries.Count == 0)
            {
                ShowEmpty(
                    "No CBZ titles found",
                    "The selected folder must contain child folders with CBZ files inside them.");
                CompleteSuccessfulScan(cancellation, attempt, "Scan complete — no CBZ files were found.");
                return;
            }

            StatusText.Text = reconcile.Warning ?? StatusForCount(_coordinator.Entries.Count);
            RestartWatcher(path);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (DirectoryNotFoundException)
        {
            if (_disposed || !ReferenceEquals(_scanCancellation, cancellation)) return;

            // The folder is gone: a painted grid stays (fast startup from the
            // index even while unavailable); only a fresh root with nothing
            // painted goes empty.
            if (_cards.Count == 0)
            {
                NotifyTitlesChanged([]);
                ShowEmpty(
                    "The library folder is not available",
                    "Browse or Scan to choose another folder.");
                StatusText.Text = "The library folder is not available. Browse or Scan to choose another folder.";
            }
            else
            {
                StatusText.Text = "The library folder is not available. Showing the saved index.";
            }
        }
        catch (Exception exception)
        {
            if (_disposed || !ReferenceEquals(_scanCancellation, cancellation)) return;

            // A failed pass keeps the previous grid (plan §2); only a fresh
            // root with nothing painted goes empty.
            var message = exception.GetBaseException().Message;
            if (_cards.Count == 0)
            {
                NotifyTitlesChanged([]);
                ShowEmpty("Could not read the library", message);
                StatusText.Text = "Library update failed.";
            }
            else
            {
                StatusText.Text = $"Library update failed; showing the saved index. {message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanCancellation = null;
                if (!_disposed) SetBusy(false);
            }

            cancellation.Dispose();
        }
    }

    private static string StatusForCount(int count) =>
        count == 1 ? "Loaded 1 indexed title." : $"Loaded {count:N0} indexed titles.";

    /// <summary>
    /// (Re)starts the watcher's bonus freshness for the reconciled root. The
    /// watcher never replaces reconciliation: it only shortens the delay
    /// between a filesystem change and the grid while the module is up.
    /// </summary>
    private void RestartWatcher(string path)
    {
        _watcher?.Dispose();
        _watcher = null;

        if (_disposed) return;

        try
        {
            var watcher = new LibraryIndexWatcher(path);
            watcher.TitleChanged += Watcher_TitleChanged;
            watcher.ResyncRequested += Watcher_ResyncRequested;
            _watcher = watcher;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            // No watcher on this root (network shares often refuse one).
            // Reconciliation stays the freshness source; nothing else changes.
        }
    }

    /// <summary>
    /// Watcher bonus freshness. Filesystem IO and thumbnail imaging run on
    /// the pool thread; only the grid sync marshals to the UI thread. A late
    /// event from a disposed or superseded watcher touches nothing: liveness
    /// is checked on both threads.
    /// </summary>
    private void Watcher_TitleChanged(object? sender, LibraryTitleChangedEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _watcher)) return;

        try
        {
            _coordinator.ReindexOne(e.FolderPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return;
        }

        if (_disposed || !ReferenceEquals(sender, _watcher)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_disposed && ReferenceEquals(sender, _watcher))
                {
                    SyncCardsFromWatcher(e.FolderPath);
                }
            });
            return;
        }

        SyncCardsFromWatcher(e.FolderPath);
    }

    /// <summary>
    /// Quiet recovery after a watcher overflow: the lost events are
    /// unknowable, so one reconciliation re-establishes the truth, then the
    /// same quiet sync as a watcher update. Pool thread for IO, UI thread
    /// for the grid; a superseded watcher touches nothing on either.
    /// </summary>
    private async void Watcher_ResyncRequested(object? sender, EventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _watcher)) return;

        var root = _coordinator.Root;
        if (root is null) return;

        LibraryReconcileResult reconcile;
        try
        {
            reconcile = await Task.Run(() => _coordinator.Reconcile(root));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or DirectoryNotFoundException)
        {
            if (_disposed || !ReferenceEquals(sender, _watcher)) return;
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (!_disposed && ReferenceEquals(sender, _watcher))
                    {
                        StatusText.Text = "Library folder is not available. Showing the saved index.";
                    }
                });
                return;
            }

            StatusText.Text = "Library folder is not available. Showing the saved index.";
            return;
        }

        if (_disposed || !ReferenceEquals(sender, _watcher)) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!_disposed && ReferenceEquals(sender, _watcher))
                {
                    SyncCardsFromResync(reconcile);
                }
            });
            return;
        }

        SyncCardsFromResync(reconcile);
    }

    /// <summary>
    /// Quiet grid sync after a blind resync: repaint + re-filter + notify,
    /// without the manual-refresh dismiss. A resync cannot name the affected
    /// folders, so the open detail closes only when its title is gone from
    /// the index; otherwise it keeps reading possibly-stale chapters until
    /// the next open — the same staleness a quiet watcher update allows.
    /// </summary>
    private void SyncCardsFromResync(LibraryReconcileResult reconcile)
    {
        FillCards(_coordinator.Entries);
        NotifyTitlesChanged(_coordinator.Entries);
        _titlesView.Refresh();
        _ = LoadCachedCoversAsync(_cards.ToArray(), CancellationToken.None);

        var open = ChapterSelector.ActiveTitleFolderPath;
        if (open is not null && _cards.All(card => !string.Equals(
            card.FolderPath, open, StringComparison.OrdinalIgnoreCase)))
        {
            ChapterSelector.Dismiss();
            StatusText.Text = "That title was removed; detail closed.";
            return;
        }

        StatusText.Text = reconcile.Warning ?? StatusForCount(_coordinator.Entries.Count);
    }

    /// <summary>
    /// Quiet grid sync for watcher updates: repaint + re-filter + notify,
    /// without the manual-refresh dismiss. Only an open detail showing the
    /// affected title itself is closed; everything else keeps reading.
    /// </summary>
    private void SyncCardsFromWatcher(string folderPath)
    {
        FillCards(_coordinator.Entries);
        NotifyTitlesChanged(_coordinator.Entries);
        _titlesView.Refresh();
        _ = LoadCachedCoversAsync(_cards.ToArray(), CancellationToken.None);

        var open = ChapterSelector.ActiveTitleFolderPath;
        if (open is not null && string.Equals(open, folderPath, StringComparison.OrdinalIgnoreCase))
        {
            ChapterSelector.Dismiss();
            StatusText.Text = "That title changed on disk; reopen it for fresh chapters.";
            return;
        }

        StatusText.Text = StatusForCount(_coordinator.Entries.Count);
    }

    private void FillCards(IReadOnlyList<LibraryIndexEntry> entries)
    {
        // Decoded covers ride along by folder path: a reconcile that changes
        // nothing visible must not blank every card and re-decode the world.
        var covers = _cards
            .Where(card => card.Cover is not null)
            .ToDictionary(card => card.FolderPath, card => card.Cover!, StringComparer.OrdinalIgnoreCase);

        _cards.Clear();
        foreach (var entry in entries)
        {
            var card = new MangaTitleCardModel(entry);
            if (covers.TryGetValue(card.FolderPath, out var cover))
            {
                card.Cover = cover;
            }

            _cards.Add(card);
        }

        EmptyPanel.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        System.Threading.Interlocked.Increment(ref _coverGeneration);
    }

    /// <summary>
    /// Fills coverless cards from the thumbnail cache — small local files,
    /// never archives. Best effort per card; a stale refresh generation
    /// assigns nothing. Fire-and-forget: covers catch up behind the grid.
    /// </summary>
    private async Task LoadCachedCoversAsync(
        IReadOnlyList<MangaTitleCardModel> cards,
        CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _coverGeneration);
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = 4,
        };

        try
        {
            await Parallel.ForEachAsync(cards, options, async (card, cancellation) =>
            {
                if (card.Cover is not null || card.Entry.CoverThumbnailPath.Length == 0) return;

                BitmapSource? cover;
                try
                {
                    cover = await Task.Run(
                        () => LibraryCoverCache.DecodeFile(card.Entry.CoverThumbnailPath, cancellation),
                        cancellation);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return;
                }

                if (cover is null || _disposed
                    || generation != Volatile.Read(ref _coverGeneration)) return;
                try
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!_disposed
                            && generation == Volatile.Read(ref _coverGeneration)
                            && _cards.Contains(card)
                            && card.Cover is null)
                        {
                            card.Cover = cover;
                        }
                    });
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The dispatcher is going away (shutdown/dispose) while a
                    // fire-and-forget fill is in flight: drop the cover, not
                    // the process.
                    return;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Ends a successful scan: the path captured at scan start is persisted
    /// only here, after the scanner completed without exception or
    /// cancellation. The field is never re-read, so a field edited mid-scan
    /// cannot change what gets saved. A persistence failure never hides the
    /// scan results — it is appended as a warning.
    /// </summary>
    private void CompleteSuccessfulScan(
        CancellationTokenSource scan,
        LibraryScanAttempt attempt,
        string status)
    {
        if (_disposed || _root is null || !ReferenceEquals(_scanCancellation, scan)) return;

        var save = _root.CompleteScan(
            attempt,
            succeeded: true,
            cancelled: scan.IsCancellationRequested);
        StatusText.Text = save.Saved
            ? status
            : $"{status} Warning: {save.Warning}";
    }

    /// <summary>
    /// Lazy open: an entry-backed card resolves its full title on demand —
    /// one folder read, never a library scan. Double activation while the
    /// load is in flight is ignored; a title that vanished since indexing
    /// drops its card instead of failing the grid.
    /// </summary>
    private async void TitleCard_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || sender is not FrameworkElement { Tag: MangaTitleCardModel card }) return;
        if (card.IsFull)
        {
            ChapterSelector.ShowTitle(card);
            return;
        }

        if (!_opening.Add(card.FolderPath)) return;

        var control = sender as Control;
        if (control is not null) control.IsEnabled = false;
        StatusText.Text = "Loading chapters…";

        try
        {
            var title = await Task.Run(() => _coordinator.Titles.LoadTitle(card.FolderPath));
            if (_disposed || !_cards.Contains(card)) return;

            if (title is null)
            {
                StatusText.Text = "That title is no longer on disk.";
                // Coordinator IO stays off the UI thread; the grid edit below
                // stays on it.
                await Task.Run(() => _coordinator.ReindexOne(card.FolderPath));
                if (!_disposed)
                {
                    _cards.Remove(card);
                    NotifyTitlesChanged(_coordinator.Entries);
                    UpdateGroupFilter();
                }

                return;
            }

            card.AttachFullTitle(title);
            if (_disposed || !_cards.Contains(card)) return;
            ChapterSelector.ShowTitle(card);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!_disposed)
            {
                StatusText.Text = $"Could not open that title: {exception.GetBaseException().Message}";
            }
        }
        finally
        {
            _opening.Remove(card.FolderPath);
            if (control is not null) control.IsEnabled = true;
        }
    }

    private void ChapterSelector_ChapterSelected(
        object? sender,
        OpenChapterRequestedEventArgs e)
    {
        ChapterSelector.Dismiss();
        OpenChapterRequested?.Invoke(this, e);
    }

    private void ChapterSelector_ResumeRequested(
        object? sender,
        OpenChapterRequestedEventArgs e)
    {
        ChapterSelector.Dismiss();
        ResumeRequested?.Invoke(this, e);
    }

    private void ChapterSelector_CoverBuilderRequested(
        object? sender,
        CoverBuilderRequestedEventArgs e)
    {
        if (!_disposed) CoverBuilderRequested?.Invoke(this, e);
    }

    private void NotifyTitlesChanged(IReadOnlyList<LibraryIndexEntry> entries) =>
        TitlesChanged?.Invoke(this, new LibraryChangedEventArgs(entries));

    private void Grouping_Changed(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateGroupFilter);
            return;
        }

        UpdateGroupFilter();
    }

    /// <summary>
    /// Re-applies the active group filter to the already loaded titles. A group
    /// whose recorded titles are all missing from this library is an empty
    /// result rather than a silent blank grid; the scan's own empty messaging
    /// still owns the case where nothing was loaded at all.
    /// </summary>
    private void UpdateGroupFilter()
    {
        if (_disposed) return;

        // Group navigation changes the result scope. A detail belongs to the
        // previous scope, so dismiss it before revealing the refreshed grid;
        // otherwise the filter changes behind the still-visible detail overlay.
        ChapterSelector.Dismiss();
        _titlesView.Refresh();
        if (_cards.Count == 0) return;

        if (_titlesView.IsEmpty)
        {
            ShowEmpty(
                "No titles in this group",
                "None of the titles recorded in the selected group are in the current library.");
            return;
        }

        EmptyPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Pulled by Grouping when it needs the current title list, so the feature
    /// never caches a Library snapshot and never triggers a scan.
    /// </summary>
    private IReadOnlyList<string> LoadedTitleFolderNames() =>
        _cards.Select(card => card.Title).ToArray();

    private void ViewModeSelector_ModeRequested(object? sender, MangaViewMode mode) =>
        _viewMode.Select(mode);

    private void SortPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_disposed || SortPicker.SelectedItem is not LibrarySortOption option) return;
        ApplySort(option);
    }

    private void ApplySort(LibrarySortOption option)
    {
        using (_titlesView.DeferRefresh())
        {
            _titlesView.SortDescriptions.Clear();
            _titlesView.SortDescriptions.Add(new SortDescription(option.PropertyPath, option.Direction));

            // Added timestamps may match when folders were copied together. A
            // deterministic title tie-breaker prevents cards from shuffling.
            if (option.PropertyPath == nameof(MangaTitleCardModel.AddedUtc))
            {
                _titlesView.SortDescriptions.Add(
                    new SortDescription(nameof(MangaTitleCardModel.Title), ListSortDirection.Ascending));
            }
        }
    }

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
    /// Switches presentation and nothing else. Both panels stay in the tree and
    /// keep their own scroll position and selection, and both read the same
    /// collection view, so a switch never rescans, refetches, refilters or
    /// re-sorts.
    /// </summary>
    private void ApplyViewMode()
    {
        if (_disposed) return;

        var list = _viewMode.Mode == MangaViewMode.List;
        ViewModeSelector.Mode = _viewMode.Mode;
        GridScroll.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        TitleTable.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The List row action and the Grid card click resolve the same binding tag,
    /// so both presentations open the same local title identity.
    /// </summary>
    private void TitleRow_OpenClick(object sender, RoutedEventArgs e) => TitleCard_Click(sender, e);

    private void ShowEmpty(string title, string detail)
    {
        EmptyTitle.Text = title;
        EmptyDetail.Text = detail;
        EmptyPanel.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        LibraryPath.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy;
    }

    public void Dispose()
    {
        if (!Dispatcher.CheckAccess())
        {
            _scanCancellation?.Cancel();
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Dispose);
            return;
        }

        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
        Loaded -= LibraryView_Loaded;
        ChapterSelector.CoverBuilderRequested -= ChapterSelector_CoverBuilderRequested;
        ChapterSelector.ResumeRequested -= ChapterSelector_ResumeRequested;
        _grouping.Changed -= Grouping_Changed;
        _viewMode.Changed -= ViewMode_Changed;
        ViewModeSelector.ModeRequested -= ViewModeSelector_ModeRequested;
        _scanCancellation?.Cancel();
        _scanCancellation = null;
        ChapterSelector.Dismiss();
        _cards.Clear();
    }

    private sealed record LibrarySortOption(
        string Label,
        string PropertyPath,
        ListSortDirection Direction);
}
