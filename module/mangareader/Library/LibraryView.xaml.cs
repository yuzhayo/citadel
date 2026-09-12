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
        new("Latest added", nameof(MangaTitle.AddedUtc), ListSortDirection.Descending),
        new("Oldest added", nameof(MangaTitle.AddedUtc), ListSortDirection.Ascending),
        new("Title (A-Z)", nameof(MangaTitleCardModel.Title), ListSortDirection.Ascending),
        new("Title (Z-A)", nameof(MangaTitleCardModel.Title), ListSortDirection.Descending),
    ];

    private readonly LibraryScanner _scanner = new();
    private readonly MangaCoverLoader _coverLoader = new();
    private readonly ObservableCollection<MangaTitleCardModel> _cards = new();
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
            candidate is MangaTitleCardModel card && _grouping.IsVisible(card.Manga.Title);
        SortPicker.ItemsSource = SortOptions;
        SortPicker.SelectedIndex = 0;
        ApplySort(SortOptions[0]);
        TitleGrid.ItemsSource = _titlesView;
        TitleTable.ItemsSource = _titlesView;

        Grouping.UseGrouping(_grouping, LoadedTitleFolderNames);
        Grouping.InstallAddTitleAction(
            ChapterSelector.DetailActions,
            () => ChapterSelector.ActiveTitleFolderName);
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
    /// its popup; this screen only offers the reserved cover slot and a pull of
    /// the active local title, so no matching or checking rule lands here.
    /// </summary>
    public void UseUpdateChecker(UpdateCheckerFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (_disposed || _updateChecker is not null) return;

        _updateChecker = new UpdateCheckerEntry(feature);
        _updateChecker.Install(
            ChapterSelector.CoverActions,
            () => ChapterSelector.ActiveTitleFolderName);
    }

    public event EventHandler<OpenChapterRequestedEventArgs>? OpenChapterRequested;

    public event EventHandler<LibraryChangedEventArgs>? TitlesChanged;

    public IReadOnlyList<MangaTitleCardModel> Titles => _cards.ToArray();

    public Task RefreshAsync() => ScanLibraryAsync();

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
            await ScanLibraryAsync();
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
        await ScanLibraryAsync();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) =>
        await ScanLibraryAsync();

    private async Task ScanLibraryAsync()
    {
        if (_disposed || _root is null) return;

        var path = LibraryPath.Text.Trim();
        if (path.Length == 0)
        {
            ShowEmpty(
                "No library selected",
                "Choose the folder that contains one child folder per manga title.");
            StatusText.Text = "A library path is required.";
            return;
        }

        // The path is captured once, at scan start. Completion persists
        // this attempt, never whatever the field says later.
        var attempt = _root.BeginScan(path);

        var previous = _scanCancellation;
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        previous?.Cancel();

        ChapterSelector.Dismiss();
        SetBusy(true);
        StatusText.Text = "Scanning title folders...";

        try
        {
            var titles = await _scanner.ScanAsync(path, cancellation.Token);
            if (_disposed || !ReferenceEquals(_scanCancellation, cancellation)) return;

            _cards.Clear();
            foreach (var title in titles)
            {
                _cards.Add(new MangaTitleCardModel(title));
            }
            NotifyTitlesChanged(titles);

            if (titles.Count == 0)
            {
                ShowEmpty(
                    "No CBZ titles found",
                    "The selected folder must contain child folders with CBZ files inside them.");
                CompleteSuccessfulScan(cancellation, attempt, "Scan complete — no CBZ files were found.");
                return;
            }

            EmptyPanel.Visibility = Visibility.Collapsed;
            var chapterCount = titles.Sum(title => title.ChapterCount);
            CompleteSuccessfulScan(cancellation, attempt, $"{titles.Count} titles · {chapterCount} chapters");
            SetBusy(false);
            UpdateGroupFilter();

            await LoadCoversAsync(_cards.ToArray(), cancellation);
            if (!_disposed && ReferenceEquals(_scanCancellation, cancellation))
            {
                NotifyTitlesChanged(titles);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_disposed || !ReferenceEquals(_scanCancellation, cancellation)) return;

            _cards.Clear();
            NotifyTitlesChanged([]);
            var baseException = exception.GetBaseException();
            ShowEmpty("Could not scan the library", baseException.Message);
            StatusText.Text = baseException is DirectoryNotFoundException
                ? "The library folder is not available. Browse or Scan to choose another folder."
                : "Scan failed.";
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

    private async Task LoadCoversAsync(
        IReadOnlyList<MangaTitleCardModel> cards,
        CancellationTokenSource scan)
    {
        var options = new ParallelOptions
        {
            CancellationToken = scan.Token,
            MaxDegreeOfParallelism = 4,
        };

        try
        {
            await Parallel.ForEachAsync(cards, options, async (card, cancellationToken) =>
            {
                BitmapSource? cover;
                try
                {
                    cover = await _coverLoader.LoadAsync(
                        card.Manga,
                        MangaCoverLoader.PreviewPixelWidth,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return;
                }

                if (cover is null || _disposed || !ReferenceEquals(_scanCancellation, scan)) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_disposed && ReferenceEquals(_scanCancellation, scan))
                    {
                        card.Cover = cover;
                    }
                });
            });
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested)
        {
        }
    }

    private void TitleCard_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || sender is not FrameworkElement { Tag: MangaTitleCardModel card }) return;
        ChapterSelector.ShowTitle(card);
    }

    private void ChapterSelector_ChapterSelected(
        object? sender,
        OpenChapterRequestedEventArgs e)
    {
        ChapterSelector.Dismiss();
        OpenChapterRequested?.Invoke(this, e);
    }

    private void NotifyTitlesChanged(IReadOnlyList<MangaTitle> titles) =>
        TitlesChanged?.Invoke(this, new LibraryChangedEventArgs(titles));

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
        _cards.Select(card => card.Manga.Title).ToArray();

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
            if (option.PropertyPath == nameof(MangaTitle.AddedUtc))
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
        Loaded -= LibraryView_Loaded;
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
