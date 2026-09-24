using System.Net.Http;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citadel.Setting.Components;
using Module.Mangareader.Features.Downloader;
using Module.Mangareader.Features.Downloader.AutoCover;
using Module.Mangareader.Features.Downloader.Lister;
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
    private readonly DownloadQueueFeature _queue;
    private ReaderWindow? _readerWindow;
    private bool _disposed;

    internal MangaReaderView(
        Lifetime lifetime,
        ReadingHistory history,
        LibraryRootContext libraryRoot,
        DownloaderContext downloader,
        ProxyLeaseRegistry proxyReservations)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _libraryRoot = libraryRoot ?? throw new ArgumentNullException(nameof(libraryRoot));
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(proxyReservations);
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

        // The queue screen is a top-level tab with the narrow dependency only:
        // it sees the queue owner, never the whole Downloader context.
        DownloadQueueTab.UseQueue(_queue);
        DownloaderTab.OpenDownloadQueue += DownloaderTab_OpenDownloadQueue;

        lifetime.Add(DisposeView);
    }

    private void LibraryTab_TitlesChanged(object? sender, LibraryChangedEventArgs e)
    {
        HistoryTab.SetLibrary(e.Entries);
        CoverBuilderTab.SetLibrary(e.Entries);
    }

    private void LibraryTab_CoverBuilderRequested(
        object? sender,
        CoverBuilderRequestedEventArgs e)
    {
        if (_disposed) return;

        CoverBuilderTab.SelectTitle(e.Title);
        CoverBuilderTabItem.IsSelected = true;
    }

    private void OpenChapterRequested(object? sender, OpenChapterRequestedEventArgs e) =>
        OpenReader(e.Title, e.Chapter);

    private void LibraryTab_ResumeRequested(object? sender, OpenChapterRequestedEventArgs e) =>
        OpenReader(e.Title, e.Chapter, ReadingPositionStore.Shared.Get(e.Chapter.FilePath));

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

    private void OpenReader(MangaTitle title, ChapterInfo chapter, double? resumeProgress = null)
    {
        if (_disposed) return;

        _readerWindow?.Close();
        _history.Record(title, chapter);

        var reader = new ReaderWindow(title, chapter, resumeProgress);
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
        DownloadQueueTab.Dispose();
        LibraryTab.Dispose();
        HistoryTab.Dispose();
        CoverBuilderTab.Dispose();
        DownloaderTab.Dispose();
        _readerWindow?.Close();
        _readerWindow = null;

    }
}
