using System.Net.Http;
using System.IO;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citadel.Setting.Components;
using Module.Mangareader.Features.Catalog;
using Module.Mangareader.Features.Catalog.Runtime;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.AutoCover;
using Module.Mangareader.Features.Downloader.Lister;
using Module.Mangareader.Features.CatalogMirror;
using Module.Mangareader.Features.Downloader.Queue;
using Module.Mangareader.Sources;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.History;
using Module.Mangareader.Library;
using Module.Mangareader.Library.UpdateChecker;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

public partial class MangaReaderView : UserControl, IContentHeaderActionProvider
{
    private readonly ReadingHistory _history = new();
    private readonly LibraryRootContext _libraryRoot = new();
    private readonly HttpClient _coverClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly DownloaderPyHostClient _downloaderBrowser;
    private readonly DownloadQueueFeature _queue;
    private readonly CatalogContext _catalogContext;
    private ReaderWindow? _readerWindow;
    private bool _disposed;

    public MangaReaderView(Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        InitializeComponent();
        // Recording is owned here, not by the History screen, so a chapter is
        // recorded whether or not that tab has ever been opened.
        HistoryTab.UseHistory(_history);

        // Library owns one root for the whole module lifetime. Injected before
        // either child receives Loaded, so the single restore happens through
        // the shared owner instead of a second reader of the preference file.
        LibraryTab.UseLibraryRoot(_libraryRoot);

        var stagingRoot = DownloaderJson.DefaultRoot();
        _downloaderBrowser = new DownloaderPyHostClient(stagingRoot);
        var sources = MangaSourceRegistry.CreateDefault(_downloaderBrowser);
        var index = new DownloadSourceIndex(stagingRoot);
        _queue = new DownloadQueueFeature(
            _libraryRoot,
            sources,
            _downloaderBrowser,
            new DownloadQueueStore(stagingRoot),
            index);

        // Lister and Auto Cover both resolve the destination through the one
        // deterministic folder rule the index owns, so a probe, a queue target and
        // a cover destination always name the same folder for the same title.
        var lister = new ListerFeature(
            new LocalTitleProbe(),
            () => _libraryRoot.CurrentRoot,
            (root, title) => index.DeterministicFolderName(root, title));

        // Auto Cover owns its own command and its own transport seam. The root
        // supplies the fetch delegate only; it never decides when a cover is
        // written, and cover success is connected to nothing else.
        var autoCover = new AutoCoverFeature(
            (url, token) => _coverClient.GetByteArrayAsync(url, token),
            lister);

        // The same registered source directory serves both feature entry points.
        // The root only translates record shapes and forwards results: it never
        // interprets provider behavior and owns no update-checking rule.
        var probe = new LocalTitleProbe();
        LibraryTab.UseUpdateChecker(new UpdateCheckerFeature(
            new SourceBindingStore(),
            new UpdateMatcher(
                probe,
                sources,
                folderName => ReadConfirmedMapping(index, folderName)),
            probe,
            sources,
            () => _libraryRoot.CurrentRoot,
            request => EnqueueUpdate(request, _queue)));

        DownloaderTab.UseContext(
            new DownloaderContext(sources, _queue, _downloaderBrowser, _libraryRoot, index, lister, autoCover));

        // Catalog Mirror composes the same source directory read-only through
        // its neutral projection, plus its own snapshot store. When no source
        // offers snapshots, the tab reports it instead of failing the module.
        var catalogRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "catalog");
        var catalogPaths = new CatalogMirrorPaths(catalogRoot);
        var catalogStore = new CatalogSnapshotStore(catalogPaths);
        var catalogBrowser = new CatalogBrowserClient(
            Path.Combine(catalogRoot, "browser-staging"));
        var catalogSources = new CatalogSourceDirectory(catalogBrowser);
        var snapshotSource = catalogSources.AvailableSources
            .OfType<ICatalogSnapshotSource>()
            .FirstOrDefault();
        CatalogMirrorSyncFeature? catalogSync = snapshotSource is null
            ? null
            : new CatalogMirrorSyncFeature(snapshotSource, catalogStore);
        var catalogGenreSync = new CatalogGenreSyncFeature(
            catalogSources,
            new CatalogGenreStore(catalogStore.Database));

        async Task<CatalogLocalAvailabilityResult> CheckCatalogAvailability(
            CatalogLocalAvailabilityRequest request, CancellationToken token)
        {
            var summary = new RemoteTitleSummary(
                new RemoteTitleIdentity(
                    request.SourceId, request.TitleId, request.TitleHid, Slug: string.Empty),
                request.TitleDisplayName,
                CoverUrl: null,
                LatestChapterLabel: null);
            var listed = await lister.ListAsync(summary, token).ConfigureAwait(false);
            return new CatalogLocalAvailabilityResult(request.Chapters.Select(chapter =>
                new CatalogChapterAvailability(
                    chapter.ChapterId,
                    ListerFeature.IsLocallyAvailable(
                        listed,
                        new RemoteChapterIdentity(
                            request.SourceId,
                            summary.Identity,
                            chapter.ChapterId,
                            chapter.ChapterNumber,
                            new RemoteGroupIdentity(request.SourceId, request.GroupId))))).ToArray());
        }

        string? ConfirmQueueTarget(CatalogQueueTargetRequest request)
        {
            var root = _libraryRoot.CurrentRoot;
            if (string.IsNullOrWhiteSpace(root)) return null;
            var identity = new RemoteTitleIdentity(
                request.SourceId, request.TitleId, request.TitleHid, Slug: string.Empty);
            var existing = index.FindUsableMapping(root, identity);
            if (existing is not null) return existing.FolderName;

            var suggested = DownloadQueueFeature.SanitizeFolder(request.TitleDisplayName);
            if (!Directory.Exists(Path.Combine(root, suggested))) return suggested;

            var claimed = SettingDialog.Confirm(
                Window.GetWindow(CatalogTab),
                "Catalog",
                $"Folder '{suggested}' sudah ada di Library tetapi tidak dipetakan ke title ini.\n\nGunakan folder itu untuk '{request.TitleDisplayName}'?",
                "Use folder");
            if (claimed) return suggested;

            var distinct = $"{suggested} [{request.TitleHid}]";
            return Directory.Exists(Path.Combine(root, distinct)) ? null : distinct;
        }

        var catalogDetail = new CatalogMirrorDetailFeature(
            catalogSources,
            new CatalogEnrichmentStore(catalogPaths),
            new CatalogMirrorCoverCache(catalogPaths, _coverClient),
            CheckCatalogAvailability);
        var catalogLoad = new CatalogMirrorLoadFeature(catalogStore);
        var catalogFeature = new CatalogMirrorFeature(
            catalogLoad, catalogSync, catalogGenreSync, catalogDetail);
        _catalogContext = new CatalogContext(catalogFeature, catalogBrowser, catalogSources);
        CatalogTab.UseContext(_catalogContext);
        CatalogTab.UseQueueTarget(ConfirmQueueTarget);
        CatalogTab.QueueHandoffRequested += CatalogTab_QueueHandoffRequested;

        // The queue screen is a top-level tab with the narrow dependency only:
        // it sees the queue owner, never the whole Downloader context.
        DownloadQueueTab.UseQueue(_queue);
        DownloaderTab.OpenDownloadQueue += DownloaderTab_OpenDownloadQueue;

        // The module lifetime owns queue execution independently of which routed
        // screen is visible. A restart parks every job, so starting here causes
        // no automatic network activity.
        _queue.Start();

        lifetime.Add(DisposeView);
    }

    private void LibraryTab_TitlesChanged(object? sender, LibraryChangedEventArgs e)
    {
        HistoryTab.SetLibrary(e.Titles);
        CoverBuilderTab.SetLibrary(e.Titles);
    }

    private void OpenChapterRequested(object? sender, OpenChapterRequestedEventArgs e) =>
        OpenReader(e.Title, e.Chapter);

    /// <summary>
    /// The Downloader asks for the standalone Queue tab; the Queue screen asks
    /// to go back to the Downloader tab. This parent only selects tabs — queue
    /// and provider state live in their owners and survive the switch.
    /// </summary>
    private void DownloaderTab_OpenDownloadQueue(object? sender, EventArgs e)
    {
        if (!_disposed) QueueTabItem.IsSelected = true;
    }

    private void DownloadQueueTab_BackRequested(object? sender, EventArgs e)
    {
        if (!_disposed) DownloaderTabItem.IsSelected = true;
    }

    /// <summary>
    /// Translates one immutable Catalog handoff into the existing queue call
    /// and shows the same queue. The queue's own answer is returned as-is:
    /// dedupe, persistence and collision rules are never reinterpreted here.
    /// A queue that cannot be written down reports its own message instead of
    /// pretending the chapters were queued.
    /// </summary>
    private void CatalogTab_QueueHandoffRequested(object? sender, CatalogQueueHandoffRequest handoff)
    {
        if (_disposed || handoff is null) return;

        try
        {
            var title = new RemoteTitleSummary(
                new RemoteTitleIdentity(
                    handoff.SourceId, handoff.TitleId, handoff.TitleHid, Slug: string.Empty),
                handoff.TitleDisplayName,
                CoverUrl: null,
                LatestChapterLabel: null);
            var group = new RemoteSourceGroup(
                new RemoteGroupIdentity(handoff.SourceId, handoff.GroupId),
                handoff.GroupDisplayName);
            // OrderIndex is the provider list order, preserved through the
            // selection: the handoff chapters arrive in state order.
            var chapters = handoff.Chapters.Select((candidate, order) => new RemoteChapterSummary(
                new RemoteChapterIdentity(
                    handoff.SourceId,
                    title.Identity,
                    candidate.ChapterId,
                    candidate.ChapterNumber,
                    group.Identity),
                candidate.DisplayName,
                OrderIndex: order)).ToArray();
            _queue.QueueChapters(title, group, chapters, handoff.TargetFolder);
            QueueTabItem.IsSelected = true;
        }
        catch (QueuePersistenceException exception)
        {
            SettingDialog.Confirm(
                Window.GetWindow(CatalogTab),
                "Queue",
                "Queue tidak dapat disimpan: " + exception.Message,
                "OK");
        }
    }

    public FrameworkElement CreateContentHeaderAction()
    {
        var button = new SettingButton
        {
            Width = 34,
            Height = 30,
            Padding = new Thickness(0),
            ToolTip = "Refresh Manga Reader library",
            Content = new TextBlock
            {
                Text = "\uE72C",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
            },
        };
        AutomationProperties.SetName(button, "Refresh Manga Reader library");
        button.Click += RefreshButton_Click;
        return button;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || sender is not Button refresh) return;
        refresh.IsEnabled = false;
        try
        {
            await LibraryTab.RefreshAsync();
            HistoryTab.Refresh();
        }
        finally
        {
            if (!_disposed) refresh.IsEnabled = true;
        }
    }

    private async void CoverBuilderTab_CoverBaked(object? sender, CoverBakedEventArgs e)
    {
        if (_disposed) return;
        await LibraryTab.RefreshAsync();
        HistoryTab.Refresh();
    }

    private void OpenReader(MangaTitle title, ChapterInfo chapter)
    {
        if (_disposed) return;

        _readerWindow?.Close();
        _history.Record(title, chapter);

        var reader = new ReaderWindow(title, chapter);
        var owner = Window.GetWindow(this);
        if (owner is not null) reader.Owner = owner;

        reader.ActiveChapterChanged += (_, change) =>
        {
            if (!_disposed) _history.Record(change.Title, change.Chapter);
        };
        reader.Closed += (_, _) =>
        {
            if (ReferenceEquals(_readerWindow, reader)) _readerWindow = null;
        };

        _readerWindow = reader;
        reader.Show();
    }

    /// <summary>
    /// Routes one confirmed folder mapping from the Downloader's index into the
    /// neutral shape Library's Update Checker reads. Translation only — the root
    /// never decides which mapping is correct.
    /// </summary>
    private static ConfirmedTitleMapping? ReadConfirmedMapping(
        DownloadSourceIndex index,
        string folderName)
    {
        var mapping = index.Load().Mappings.FirstOrDefault(candidate =>
            string.Equals(candidate.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
        return mapping is null
            ? null
            : new ConfirmedTitleMapping(
                mapping.SourceId,
                mapping.TitleId,
                mapping.TitleHid,
                mapping.FolderName);
    }

    /// <summary>
    /// Routes one immutable update selection to the existing queue command and
    /// reports the queue's own answer. A queue that cannot be written down must
    /// not accept the job, so that failure is reported rather than swallowed.
    /// </summary>
    private static UpdateHandoffResult EnqueueUpdate(
        UpdateDownloadRequest request,
        DownloadQueueFeature queue)
    {
        try
        {
            var result = queue.QueueChapters(
                new RemoteTitleSummary(
                    request.Title,
                    request.TitleDisplayName,
                    CoverUrl: null,
                    LatestChapterLabel: null),
                new RemoteSourceGroup(request.Group, request.GroupDisplayName),
                request.Chapters,
                request.LocalFolderName);
            // The neutral handoff carries one skip total. Both of the queue's skip
            // reasons mean "this chapter was not queued", so the translation adds them
            // rather than dropping one on the floor.
            return new UpdateHandoffResult(
                result.Queued,
                result.SkippedAlreadyPublished + result.SkippedAlreadyQueued,
                result.Blocked);
        }
        catch (QueuePersistenceException exception)
        {
            return new UpdateHandoffResult(
                0,
                0,
                "Queue tidak dapat disimpan: " + exception.Message);
        }
    }

    private void DisposeView()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(DisposeView);
            return;
        }

        if (_disposed) return;
        _disposed = true;

        // Unsubscribe order is locked: the Queue screen drops its queue
        // subscription first so no late event reaches a disposed view, then
        // feature views, then the queue owner and shared clients last.
        DownloadQueueTab.BackRequested -= DownloadQueueTab_BackRequested;
        DownloaderTab.OpenDownloadQueue -= DownloaderTab_OpenDownloadQueue;
        CatalogTab.QueueHandoffRequested -= CatalogTab_QueueHandoffRequested;
        DownloadQueueTab.Dispose();
        CatalogTab.Dispose();
        _catalogContext.Dispose();
        LibraryTab.Dispose();
        HistoryTab.Dispose();
        CoverBuilderTab.Dispose();
        DownloaderTab.Dispose();
        _readerWindow?.Close();
        _readerWindow = null;

        _queue.Dispose();
        _downloaderBrowser.Dispose();
        _coverClient.Dispose();
    }
}
