using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Citadel.Setting.Components;
using Module.Mangareader.Features.Downloader.AutoCover;
using Module.Mangareader.Features.Downloader.Lister;
using Module.Mangareader.Features.Downloader.ManualUrl;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Sources;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.ShareLogic;

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
    private readonly ObservableCollection<ChapterRow> _chapterRows = [];
    private readonly StackPanel _detailCoverActions = new();
    private string? _chapterSetKey;
    private bool _syncingSelection;
    private RemoteTitleDetailAdapter? _detailAdapter;
    private string? _detailKey;
    private ImageSource? _detailCover;
    private string? _detailCoverKey;
    private ListerFeature? _lister;
    private ListerResult? _listerResult;
    private DownloaderContext? _context;
    private CatalogFeature? _catalog;
    private CancellationTokenSource? _actionCancellation;
    private CancellationTokenSource? _browseCancellation;
    private CancellationTokenSource? _coverFetchCancellation;
    private CancellationTokenSource? _coverCancellation;
    private CancellationTokenSource? _coverBatch;
    private int _coverGeneration;
    private bool _settingSource;
    private bool _settingGroup;
    private bool _browseActive;
    private bool _stopping;
    private bool _manualDetail;
    private bool _disposed;

    public CatalogScreen()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _cards;
        Detail.CoverActions = _detailCoverActions;
        ChapterTable.ItemsSource = _chapterRows;
        if (ChapterHeaderCheck is { } headerCheck) headerCheck.Click += ChapterHeaderCheck_Click;
        IsVisibleChanged += CatalogScreen_IsVisibleChanged;
    }

    private CheckBox? ChapterHeaderCheck =>
        ChapterTable.InteractiveColumns
            .OfType<DataGridTemplateColumn>()
            .FirstOrDefault()?.Header as CheckBox;

    /// <summary>
    /// Reserved region below the remote cover. The feature that owns a cover
    /// action places it here; this screen places nothing speculatively.
    /// </summary>
    internal Panel DetailCoverActions => _detailCoverActions;

    /// <summary>Whether the open detail's probed folder already holds this chapter.</summary>
    internal bool IsLocallyAvailable(RemoteChapterIdentity chapter) =>
        _listerResult is { } result && ListerFeature.IsLocallyAvailable(result, chapter);

    /// <summary>The only entry point to Download List.</summary>
    public event EventHandler? OpenDownloadList;

    /// <summary>Requests the route host to show the dedicated Manual URL input screen.</summary>
    public event EventHandler? OpenManualUrl;

    /// <summary>Returns from a manually opened detail to its dedicated input screen.</summary>
    public event EventHandler? ReturnToManualUrl;

    /// <summary>
    /// Announces that a queue item was added for this title, carrying one
    /// immutable cover candidate. This screen applies no cover rule and the route
    /// host only relays: deciding whether a cover is written belongs to Auto Cover.
    /// </summary>
    public event EventHandler<CoverCandidate>? CoverCandidateAvailable;

    public void UseContext(DownloaderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_disposed || _context is not null) return;

        _context = context;
        _catalog = new CatalogFeature(context.Sources, () => context.LibraryRoot.CurrentRoot is not null);
        _catalog.StateChanged += Catalog_StateChanged;
        _lister = context.Lister;
        InstallFetchCoverAction();

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
            HostFilterPanel();
        }

        Render(_catalog.State);
    }

    private void SourcePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_disposed || _settingSource || _catalog is null) return;
        if (SourcePicker.SelectedItem is not MangaSourceRegistration registration) return;

        // Selecting a provider performs no remote request and starts no browser.
        _catalog.SelectSource(registration.Id);
        HostFilterPanel();
    }

    /// <summary>
    /// Hosts the panel of the contribution the feature actually snapshots at
    /// Start. Asking the registration for a second contribution would let the
    /// user edit filters that never reach a request, so the hosted panel and the
    /// query draft are always the same instance.
    /// </summary>
    private void HostFilterPanel()
    {
        if (_disposed || _catalog is null) return;
        FilterHost.Content = _catalog.Filters?.CreatePanel();
    }

    private void SearchField_KeyDown(object sender, KeyEventArgs e)
    {
        // Typing never sends a request; Enter is the same explicit Search action.
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SearchButton_Click(sender, e);
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        await StartCatalogAsync();
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await StartCatalogAsync();
    }

    private void ManualButton_Click(object sender, RoutedEventArgs e) =>
        OpenManualUrl?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Opens a detail resolved by Manual URL while leaving the current browse
    /// state intact. The same detail/queue presentation is reused; only the
    /// provider controls are hidden for this route.
    /// </summary>
    public async Task<string?> OpenManualTitleAsync(ManualUrlResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (_disposed || _catalog is null) return "Downloader belum siap.";

        _manualDetail = true;
        await RunActionAsync(async token =>
        {
            await _catalog.OpenResolvedTitleAsync(resolution.Title, token);
            if (_catalog.State.IsDetailOpen)
            {
                await RefreshListerAsync(token);
                await LoadDetailCoverAsync(resolution.Title.Summary, token);
            }
        });

        var state = _catalog.State;
        if (state.IsDetailOpen) return null;

        _manualDetail = false;
        Render(state);
        return state.ErrorMessage ?? "URL tidak dapat membuka title.";
    }

    private async Task StartCatalogAsync()
    {
        if (_disposed || _catalog is null || _context is null || _browseActive) return;
        if (_catalog.State.IsActionBusy) return;

        // Preserve the established Start behavior: the requested mode is applied
        // by the provider's existing browse path. Switching the toggle and pressing
        // Start therefore replaces a headed session with headless (or vice versa)
        // and returns the catalog in the same operation.
        _context.Browser.ShowBrowser = ShowBrowserToggle.IsChecked == true;
        await RunBrowseAsync(token => _catalog.StartAsync(SearchField.Text, token));
    }

    /// <summary>Stops only the Downloader-owned online provider process.</summary>
    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping || _catalog is null || _context is null) return;

        _stopping = true;
        _catalog.AbandonActiveBrowse();
        _browseCancellation?.Cancel();
        _context.Browser.AbortSession();
        if (!_browseActive)
        {
            _stopping = false;
            SetStatus("Provider process stopped.", isError: false);
            Render(_catalog.State);
        }
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
            await RefreshListerAsync(token);
            await LoadDetailCoverAsync(card.Summary, token);
        });
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_catalog is null) return;
        var returnToManual = _manualDetail;
        var anchor = _catalog.State.ResultsScrollOffset;
        _catalog.Back();

        ClearDetailPresentation();
        _manualDetail = false;
        Render(_catalog.State);

        if (returnToManual)
        {
            ReturnToManualUrl?.Invoke(this, EventArgs.Empty);
            return;
        }

        Dispatcher.BeginInvoke(() => ResultsScroll.ScrollToVerticalOffset(anchor));
    }

    private void ClearDetailPresentation()
    {
        // The detail and its cover belong to the title being left; releasing them
        // here means a later title can never inherit this one's cover.
        _detailAdapter = null;
        _detailKey = null;
        _detailCover = null;
        _detailCoverKey = null;
        Detail.DataContext = null;
        _listerResult = null;

        foreach (var row in _chapterRows) row.SelectionChanged -= Row_SelectionChanged;
        _chapterRows.Clear();
        _chapterSetKey = null;

        RenderLocalAvailability();
    }

    private async void GroupPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_disposed || _settingGroup || _catalog is null) return;
        var state = _catalog.State;
        if (state.Detail is null || GroupPicker.SelectedItem is not RemoteSourceGroup group) return;

        await RunActionAsync(async token =>
        {
            await _catalog.SelectGroupAsync(state.Detail.Summary.Identity, group, token);
            await RefreshListerAsync(token);
        });
    }

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

        // One confirmation for the whole batch, never one dialog per row. The
        // answer is the replacement permission the queue needs: without it the
        // queue keeps skipping every already published chapter, so this promise
        // of an atomic replacement would queue nothing at all.
        var alreadyLocal = chapters.Count(chapter => IsLocallyAvailable(chapter.Identity));
        var replacePublished = false;
        if (alreadyLocal > 0)
        {
            replacePublished = SettingDialog.Confirm(
                Window.GetWindow(this),
                "Downloader",
                $"{alreadyLocal} dari {chapters.Count} chapter yang dipilih sudah ada di folder lokal.\n\n"
                + "Mengunduh ulang dari source/group yang sama menggantikan arsipnya secara atomik. "
                + "Chapter dari source/group lain tetap menjadi file tersendiri dan tidak ditimpa.\n\n"
                + "Lanjutkan?",
                "Download again");
            if (!replacePublished) return;
        }

        var folder = ResolveTargetFolder(state.Detail.Summary, root);
        if (folder is null) return;

        QueueAddResult result;
        try
        {
            result = _context.Queue.QueueChapters(
                state.Detail.Summary,
                group,
                chapters,
                folder,
                replacePublished);
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

        // The two skips are reported apart: one is a finished download the user may
        // choose to replace, the other is work still in flight that no confirmation
        // would queue a second time.
        var skips = new List<string>(2);
        if (result.SkippedAlreadyQueued > 0)
        {
            skips.Add($"{result.SkippedAlreadyQueued} masih berjalan di queue");
        }

        if (result.SkippedAlreadyPublished > 0)
        {
            skips.Add($"{result.SkippedAlreadyPublished} sudah pernah dipublikasikan");
        }

        SetStatus(
            skips.Count == 0
                ? $"{result.Queued} chapter di-queue ke '{folder}'."
                : $"{result.Queued} chapter di-queue ke '{folder}'; " + string.Join(", ", skips) + ".",
            isError: false);

        // A queue item was added, so the candidate is relayed. Whether a cover is
        // written is Auto Cover's decision, not this screen's and not the queue's.
        if (result.Queued > 0)
        {
            CoverCandidateAvailable?.Invoke(
                this,
                new CoverCandidate(state.Detail.Summary, root, folder));
        }

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

    private void Render(CatalogState state)
    {
        ProviderControlsPanel.Visibility = _manualDetail ? Visibility.Collapsed : Visibility.Visible;
        RenderRequestButtons(state);
        LoadMoreButton.IsEnabled = !state.IsBusy && !_stopping && state.CanLoadMore;
        SourcePicker.IsEnabled = !state.IsBusy;
        SearchField.IsEnabled = !state.IsBusy;

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

    /// <summary>
    /// Start and Search both use the established provider browse path. Stop is a
    /// separate process-lifetime action and remains available throughout a request.
    /// </summary>
    private void RenderRequestButtons(CatalogState state)
    {
        var processBusy = _browseActive || _stopping;
        var hasSession = !_browseActive && _context?.Browser.HasSession == true;
        ShowBrowserToggle.IsEnabled = !processBusy && !state.IsActionBusy;
        StartButton.Content = "Start";
        StartButton.IsEnabled = state.SelectedSourceId is not null
            && !processBusy
            && !state.IsActionBusy;
        SearchButton.Content = _browseActive ? "Searching…" : "Search";
        SearchButton.IsEnabled = !processBusy
            && !state.IsActionBusy;
        ManualButton.IsEnabled = !processBusy && !state.IsActionBusy;
        StopButton.IsEnabled = !_stopping
            && (hasSession || _browseActive);
    }

    private void RenderDetail(CatalogState state)
    {
        if (state.Detail is null) return;

        BindDetail(state.Detail);

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

        RenderChapters(state);

        QueueButton.IsEnabled = state.HasSelection && state.HasLibraryRoot && !state.IsBusy;
    }

    /// <summary>
    /// Keeps one row instance per chapter for as long as the same title and group
    /// are open. Selection and availability then update rows in place, so a
    /// checkbox click cannot rebuild the table, drop keyboard focus or reset the
    /// scroll position — all of which are session-only presentation state.
    /// </summary>
    private void RenderChapters(CatalogState state)
    {
        var key = state.Detail is null
            ? null
            : state.Detail.Summary.Identity.TitleHid
              + "|"
              + state.SelectedGroup?.GroupId
              + "|"
              + string.Join('|', state.Chapters.Select(chapter => chapter.Identity.ChapterId));

        if (string.Equals(_chapterSetKey, key, StringComparison.Ordinal))
        {
            ReconcileSelection(state);
        }
        else
        {
            _chapterSetKey = key;
            RebuildChapterRows(state);
        }

        UpdateChapterAvailability();
        UpdateSelectionUi();
    }

    private void RebuildChapterRows(CatalogState state)
    {
        foreach (var row in _chapterRows) row.SelectionChanged -= Row_SelectionChanged;
        _chapterRows.Clear();

        _syncingSelection = true;
        try
        {
            foreach (var chapter in state.Chapters)
            {
                var row = new ChapterRow(chapter)
                {
                    IsSelected = state.SelectedChapterIds.Contains(chapter.Identity.ChapterId),
                };
                row.SelectionChanged += Row_SelectionChanged;
                _chapterRows.Add(row);
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void ReconcileSelection(CatalogState state)
    {
        _syncingSelection = true;
        try
        {
            foreach (var row in _chapterRows)
            {
                row.IsSelected = state.SelectedChapterIds.Contains(row.Chapter.Identity.ChapterId);
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>
    /// Re-reads availability from Lister's last filesystem verdict, so a chapter
    /// published or deleted since the detail opened is dimmed or undimmed without
    /// a Library scan and without a new provider request.
    /// </summary>
    private void UpdateChapterAvailability()
    {
        foreach (var row in _chapterRows)
        {
            row.IsLocallyAvailable = _listerResult is { } result
                && ListerFeature.IsLocallyAvailable(result, row.Chapter.Identity);
        }
    }

    private void Row_SelectionChanged(object? sender, EventArgs e)
    {
        if (_disposed || _syncingSelection || _catalog is null) return;
        _catalog.SetSelectedChapterIds(SelectedChapterIds());
    }

    /// <summary>
    /// The header checkbox reaches exactly the rows the table currently holds, so
    /// a sort that reordered them cannot change which chapters Select All selects.
    /// </summary>
    private void ChapterHeaderCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || sender is not CheckBox box) return;

        var select = box.IsChecked == true;
        _syncingSelection = true;
        try
        {
            foreach (var row in _chapterRows) row.IsSelected = select;
        }
        finally
        {
            _syncingSelection = false;
        }

        _catalog?.SetSelectedChapterIds(SelectedChapterIds());
        UpdateSelectionUi();
    }

    private IReadOnlyList<string> SelectedChapterIds() =>
        _chapterRows
            .Where(row => row.IsSelected)
            .Select(row => row.Chapter.Identity.ChapterId)
            .ToList();

    private void UpdateSelectionUi()
    {
        if (_disposed || ChapterHeaderCheck is not { } box) return;

        var selected = _chapterRows.Count(row => row.IsSelected);
        box.IsChecked = selected == 0
            ? false
            : selected == _chapterRows.Count
                ? true
                : null;
    }

    /// <summary>
    /// Binds the shared detail composition through a Catalog-owned adapter. One
    /// adapter instance per remote identity survives a re-render, so a cover that
    /// already decoded is never discarded by a later state change and a stale
    /// cover can never paint a different title.
    /// </summary>
    private void BindDetail(RemoteTitleDetail detail)
    {
        var key = IdentityKey(detail.Summary.Identity);
        if (_detailAdapter is null || !string.Equals(_detailKey, key, StringComparison.Ordinal))
        {
            _detailKey = key;
            _detailAdapter = new RemoteTitleDetailAdapter(detail);
            Detail.DataContext = _detailAdapter.Presentation;
        }

        ApplyDetailCover();
    }

    /// <summary>
    /// Paints a decoded cover only onto the identity it was fetched for. Cover
    /// loading is cosmetic: a failure leaves the placeholder and cannot remove the
    /// title, the synopsis or the metadata rows.
    /// </summary>
    private void ApplyDetailCover()
    {
        if (_detailAdapter is null || _detailCover is null) return;
        if (!string.Equals(_detailKey, _detailCoverKey, StringComparison.Ordinal)) return;

        _detailAdapter.Presentation.Cover = _detailCover;
    }

    private static string IdentityKey(RemoteTitleIdentity identity) =>
        identity.SourceId + "|" + identity.TitleId + "|" + identity.TitleHid;

    /// <summary>
    /// The deterministic folder for one remote title, resolved through the rule
    /// the mapping index owns so a queue target, a Lister probe and a cover
    /// destination always name the same folder.
    /// </summary>
    private string ResolveDeterministicFolder(RemoteTitleSummary title)
    {
        var root = _context?.LibraryRoot.CurrentRoot;
        return root is null || _context is null
            ? DownloadQueueFeature.SanitizeFolder(title.DisplayName)
            : _context.Index.DeterministicFolderName(root, title);
    }

    /// <summary>
    /// Places Auto Cover's own manual action below the remote cover. This screen
    /// owns no cover rule: it invokes the same single command the automatic
    /// trigger uses, and supplies only the overwrite question.
    /// </summary>
    private void InstallFetchCoverAction()
    {
        var button = new SettingButton
        {
            Content = "Fetch Cover",
            MinWidth = 124,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(button, "Fetch this title's cover into its download folder");
        button.Click += FetchCoverButton_Click;
        _detailCoverActions.Children.Add(button);
    }

    private async void FetchCoverButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _context is null || _catalog is null) return;
        if (sender is not SettingButton button) return;

        var detail = _catalog.State.Detail;
        var root = _context.LibraryRoot.CurrentRoot;
        if (detail is null) return;
        if (string.IsNullOrWhiteSpace(root))
        {
            SetStatus("Library root belum diatur; pilih folder di tab Library lebih dulu.", isError: true);
            return;
        }

        var candidate = new CoverCandidate(detail.Summary, root, ResolveDeterministicFolder(detail.Summary));

        // Owned here and cancelled on dispose: a fetch still in flight when this
        // screen goes away must not keep running or report into a disposed surface.
        var previous = _coverFetchCancellation;
        var cancellation = new CancellationTokenSource();
        _coverFetchCancellation = cancellation;
        previous?.Cancel();

        button.IsEnabled = false;
        try
        {
            var outcome = await _context.AutoCover.SaveCoverAsync(
                candidate,
                AutoCoverTrigger.Manual,
                path => SettingDialog.Confirm(
                    Window.GetWindow(this),
                    "Downloader",
                    $"Cover sudah ada di:\n{path}\n\nTimpa dengan cover dari provider?",
                    "Overwrite"),
                cancellation.Token);

            if (_disposed) return;
            SetStatus(
                outcome.Succeeded
                    ? outcome.Kind == AutoCoverOutcomeKind.Published
                        ? $"Cover disimpan ke '{outcome.Path}'."
                        : outcome.Message
                    : outcome.Message,
                isError: !outcome.Succeeded);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_coverFetchCancellation, cancellation))
            {
                _coverFetchCancellation = null;
            }

            cancellation.Dispose();
            if (!_disposed) button.IsEnabled = true;
        }
    }

    /// <summary>
    /// Re-probes local storage for the open title. Bounded and read-only: one
    /// folder, only on an explicit trigger, never on a timer, and never the whole
    /// Library. A failure leaves availability unknown rather than failing the
    /// detail, because availability is advisory and the queue re-checks its own
    /// target when it publishes.
    /// </summary>
    private async Task RefreshListerAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _lister is null || _catalog is null) return;
        var detail = _catalog.State.Detail;
        if (detail is null) return;

        try
        {
            var result = await _lister.ListAsync(detail.Summary, cancellationToken).ConfigureAwait(true);
            if (_disposed) return;
            _listerResult = result;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            if (_disposed) return;
            _listerResult = ListerResult.Absent(_listerResult?.LocalFolderName ?? string.Empty);
        }

        RenderLocalAvailability();
    }

    private void RenderLocalAvailability()
    {
        var result = _listerResult;
        if (_disposed || _catalog?.State.Detail is null || result is null)
        {
            LocalAvailabilityText.Visibility = Visibility.Collapsed;
            return;
        }

        LocalAvailabilityText.Text = result.TitleExists
            ? $"{result.ChapterCount} chapter sudah ada di folder lokal '{result.LocalFolderName}'."
            : $"Title ini belum ada di Library. Folder target: '{result.LocalFolderName}'.";
        LocalAvailabilityText.Visibility = Visibility.Visible;
        UpdateChapterAvailability();
    }

    /// <summary>
    /// Returning to this screen with a detail open re-probes, because files may
    /// have been added or removed while the Download List was showing.
    /// </summary>
    private async void CatalogScreen_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (_disposed || e.NewValue is not true) return;
        await RefreshListerAsync(CancellationToken.None);
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
            var key = IdentityKey(identity);
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
            .Where(card => card.Cover is null && card.CoverUrls.Count > 0)
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
                    cover = await LoadCoverAsync(
                        card.CoverUrls,
                        CoverPixelWidth,
                        cancellationToken).ConfigureAwait(true);
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
        IReadOnlyList<string> urls,
        int decodePixelWidth,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        foreach (var url in urls)
        {
            try
            {
                var bytes = await CoverClient.GetByteArrayAsync(url, cancellationToken)
                    .ConfigureAwait(true);
                if (bytes.Length == 0) continue;

                using var stream = new MemoryStream(bytes, writable: false);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                if (decodePixelWidth > 0) image.DecodePixelWidth = decodePixelWidth;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
            }
        }

        if (lastFailure is not null) throw lastFailure;
        return null;
    }

    /// <summary>
    /// Loads the detail cover off the UI thread. A stale response is dropped, so
    /// selecting another title can never paint the previous cover.
    /// </summary>
    private async Task LoadDetailCoverAsync(RemoteTitleSummary title, CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _coverGeneration);
        var coverUrls = title.CoverCandidates().ToArray();
        if (coverUrls.Length == 0) return;

        var previous = _coverCancellation;
        var cancellation = new CancellationTokenSource();
        _coverCancellation = cancellation;
        previous?.Cancel();

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, cancellation.Token);
            var image = await LoadCoverAsync(coverUrls, decodePixelWidth: 0, linked.Token)
                .ConfigureAwait(true);
            if (_disposed || generation != Volatile.Read(ref _coverGeneration)) return;
            if (image is null) return;

            if (generation != Volatile.Read(ref _coverGeneration)) return;

            _detailCover = image;
            _detailCoverKey = IdentityKey(title.Identity);
            ApplyDetailCover();
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

    /// <summary>
    /// Runs one Browse under its own cancellation source. Only this lifecycle is
    /// reachable from Stop, and only it drives the request button's activity state.
    /// A later Browse supersedes the previous one, which is also what the feature's
    /// latest-request-wins generation guard expects.
    /// </summary>
    private async Task RunBrowseAsync(Func<CancellationToken, Task> browse)
    {
        var previous = _browseCancellation;
        var cancellation = new CancellationTokenSource();
        _browseCancellation = cancellation;
        previous?.Cancel();

        _browseActive = true;
        if (_catalog is not null) Render(_catalog.State);

        try
        {
            await browse(cancellation.Token);
        }
        finally
        {
            var current = ReferenceEquals(_browseCancellation, cancellation);
            if (current)
            {
                _browseCancellation = null;
            }

            cancellation.Dispose();

            if (current)
            {
                // The bounded inline command has now settled, so a parked Stop
                // returns to Start for every terminal Browse result.
                _browseActive = false;
                _stopping = false;
                if (!_disposed && _catalog is not null) Render(_catalog.State);
            }
        }
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

        IsVisibleChanged -= CatalogScreen_IsVisibleChanged;
        if (_catalog is not null) _catalog.StateChanged -= Catalog_StateChanged;

        var actionCancellation = _actionCancellation;
        _actionCancellation = null;
        actionCancellation?.Cancel();

        var browseCancellation = _browseCancellation;
        _browseCancellation = null;
        browseCancellation?.Cancel();

        var coverFetchCancellation = _coverFetchCancellation;
        _coverFetchCancellation = null;
        coverFetchCancellation?.Cancel();

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

        foreach (var row in _chapterRows) row.SelectionChanged -= Row_SelectionChanged;
        _chapterRows.Clear();
        _chapterSetKey = null;
    }

    /// <summary>
    /// One chapter row. Presentation only — the feature owns the selection the
    /// queue receives, and this model only mirrors it so the shared table can bind
    /// a checkbox and dim a chapter that already exists locally.
    /// </summary>
    private sealed class ChapterRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isLocallyAvailable;

        public ChapterRow(RemoteChapterSummary chapter) =>
            Chapter = chapter ?? throw new ArgumentNullException(nameof(chapter));

        public RemoteChapterSummary Chapter { get; }

        public string DisplayName => Chapter.DisplayName;

        public string ChapterNumber => Chapter.Identity.ChapterNumber;

        /// <summary>
        /// Zero-padded so a header sort on Number reads 2 before 10 while the cell
        /// keeps showing the provider's own label.
        /// </summary>
        public string ChapterSortKey => SortKey(Chapter.Identity.ChapterNumber, Chapter.OrderIndex);

        /// <summary>
        /// Dimmed, never disabled: re-downloading an existing chapter is allowed
        /// and only asks once for the whole batch.
        /// </summary>
        public bool IsLocallyAvailable
        {
            get => _isLocallyAvailable;
            set
            {
                if (_isLocallyAvailable == value) return;
                _isLocallyAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AvailabilityOpacity));
                OnPropertyChanged(nameof(AvailabilityText));
            }
        }

        public double AvailabilityOpacity => _isLocallyAvailable ? 0.45 : 1.0;

        public string AvailabilityText => _isLocallyAvailable ? "Already local" : "Not downloaded";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? SelectionChanged;

        public event PropertyChangedEventHandler? PropertyChanged;

        private static string SortKey(string chapterNumber, int orderIndex)
        {
            var normalized = ChapterNumberText.Normalize(chapterNumber);
            if (normalized is null)
            {
                return orderIndex.ToString("D10", CultureInfo.InvariantCulture);
            }

            var parts = normalized.Split('.', 2);
            var integer = long.TryParse(
                    parts[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value)
                ? value.ToString("D10", CultureInfo.InvariantCulture)
                : parts[0];
            return parts.Length == 2 ? integer + "." + parts[1] : integer;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
