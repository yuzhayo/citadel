using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citadel.Setting.Components;
using Module.Mangareader.History;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

public partial class MangaReaderView : UserControl, IContentHeaderActionProvider
{
    private readonly ReadingHistory _history = new();
    private ReaderWindow? _readerWindow;
    private bool _disposed;

    public MangaReaderView(Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        InitializeComponent();
        // Recording is owned here, not by the History screen, so a chapter is
        // recorded whether or not that tab has ever been opened.
        HistoryTab.UseHistory(_history);
        lifetime.Add(DisposeView);
    }

    private void LibraryTab_TitlesChanged(object? sender, LibraryChangedEventArgs e)
    {
        HistoryTab.SetLibrary(e.Titles);
        CoverBuilderTab.SetLibrary(e.Titles);
    }

    private void OpenChapterRequested(object? sender, OpenChapterRequestedEventArgs e) =>
        OpenReader(e.Title, e.Chapter);

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

        LibraryTab.Dispose();
        HistoryTab.Dispose();
        CoverBuilderTab.Dispose();
        _readerWindow?.Close();
        _readerWindow = null;
    }
}
