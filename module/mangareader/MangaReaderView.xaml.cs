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
    private readonly ReadingHistory _history;
    private readonly LibraryRootContext _libraryRoot;
    private readonly HttpClient _coverClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ProxyPoolAdapter _catalogProxyPool;
    private readonly ProxyHttpTransport _catalogHttpTransport;
    private readonly DownloadQueueFeature _queue;
    private readonly CatalogContext _catalogContext;
    private ReaderWindow? _readerWindow;
    private bool _disposed;

    internal MangaReaderView(
        Lifetime lifetime,
        ReadingHistory history,
        LibraryRootContext libraryRoot,
        DownloaderContext downloader)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _libraryRoot = libraryRoot ?? throw new ArgumentNullException(nameof(libraryRoot));
        ArgumentNullException.ThrowIfNull(downloader);
        _queue = downloader.Queue;
        InitializeComponent();
        // Recording is owned here, not by the History screen, so a chapter is
        // recorded whether or not that tab has ever been opened.
        HistoryTab.UseHistory(_history);

        // Library owns one root for the whole application lifetime. Injected before
        // either child receives Loaded, so the single restore happens through
        // the shared owner instead of a second reader of the preference file.
        LibraryTab.UseLibraryRoot(_libraryRoot);

        var sources = downloader.Sources;
        var index = downloader.Index;
        var lister = downloader.Lister;

        // The same registered source directory serves both feature entry points.
        // The root only translates record shapes and forwards results: it never
        // interprets provider behavior and owns no update-checking rule.
        var probe = new LocalTitleProbe();
        LibraryTab.UseUpdateChecker(new UpdateCheckerFeature(
            new SourceBindingStore(),
            new UpdateMatcher(
                probe,
                sources,
                folderName => MangaReaderHandoffs.ReadConfirmedMapping(index, folderName)),
            probe,
            sources,
            () => _libraryRoot.CurrentRoot,
            request => MangaReaderHandoffs.EnqueueUpdate(request, _queue)));

        DownloaderTab.UseContext(downloader);

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
        _catalogProxyPool = new ProxyPoolAdapter("MangaReader Catalog");
        _catalogHttpTransport = new ProxyHttpTransport(_catalogProxyPool);
        var catalogBrowser = new CatalogBrowserClient(
            Path.Combine(catalogRoot, "browser-staging"),
            _catalogProxyPool);
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

        var catalogDetail = new CatalogMirrorDetailFeature(
            catalogSources,
            new CatalogEnrichmentStore(catalogPaths),
            new CatalogMirrorCoverCache(catalogPaths, _coverClient, _catalogHttpTransport),
            (request, token) => MangaReaderHandoffs.CheckCatalogAvailability(lister, request, token));
        var catalogLoad = new CatalogMirrorLoadFeature(catalogStore);
        var catalogFeature = new CatalogMirrorFeature(
            catalogLoad, catalogSync, catalogGenreSync, catalogDetail);
        _catalogContext = new CatalogContext(
            catalogFeature,
            catalogBrowser,
            catalogSources,
            _catalogProxyPool,
            _catalogHttpTransport);
        CatalogTab.UseContext(_catalogContext);
        CatalogTab.UseQueueTarget(request => MangaReaderHandoffs.ConfirmQueueTarget(
            _libraryRoot, index, () => Window.GetWindow(CatalogTab), request));
        CatalogTab.QueueHandoffRequested += CatalogTab_QueueHandoffRequested;

        // The queue screen is a top-level tab with the narrow dependency only:
        // it sees the queue owner, never the whole Downloader context.
        DownloadQueueTab.UseQueue(_queue);
        DownloaderTab.OpenDownloadQueue += DownloaderTab_OpenDownloadQueue;

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

        var error = MangaReaderHandoffs.QueueHandoff(handoff, _queue);
        if (error is null)
        {
            QueueTabItem.IsSelected = true;
            return;
        }

        SettingDialog.Confirm(
            Window.GetWindow(CatalogTab),
            "Queue",
            "Queue tidak dapat disimpan: " + error,
            "OK");
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

    private void DisposeView()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(DisposeView);
            return;
        }

        if (_disposed) return;
        _disposed = true;

        // Routed-view cleanup owns presentation only. Queue, provider session,
        // and Downloader transports belong to the application background
        // service and must survive navigation and close-to-tray.
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

        _catalogHttpTransport.Dispose();
        _coverClient.Dispose();
    }
}
