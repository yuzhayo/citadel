using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Module.Mangareader.History;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

public partial class HistoryView : UserControl, IDisposable
{
    private readonly ObservableCollection<HistoryCardModel> _history = new();
    private readonly MangaCoverLoader _coverLoader = new();
    private readonly ChapterRenderCache _renderCache = new();
    private IReadOnlyList<MangaTitle> _titles = [];
    private ReadingHistory? _readingHistory;
    private CancellationTokenSource? _coverCancellation;
    private bool _disposed;

    public HistoryView()
    {
        InitializeComponent();
        HistoryGrid.ItemsSource = _history;
    }

    public event EventHandler<OpenChapterRequestedEventArgs>? OpenChapterRequested;

    /// <summary>
    /// Attaches the History feature's recording owner. The composition root
    /// holds it, so chapter events are recorded without routing through this
    /// screen and history is written even before this tab is opened.
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
    }

    public void SetLibrary(IReadOnlyList<MangaTitle> titles)
    {
        _titles = titles ?? [];
        Refresh();
    }

    public void Refresh()
    {
        var reusableCovers = SnapshotCovers();
        _history.Clear();

        if (_readingHistory is not null)
        {
            foreach (var entry in _readingHistory.Read())
            {
                var title = _titles.FirstOrDefault(candidate => string.Equals(
                    candidate.FolderPath,
                    entry.TitleFolderPath,
                    StringComparison.OrdinalIgnoreCase));
                if (title is null) continue;

                var chapter = title.Chapters.FirstOrDefault(candidate => string.Equals(
                    candidate.FilePath,
                    entry.ChapterFilePath,
                    StringComparison.OrdinalIgnoreCase));
                if (chapter is null) continue;

                var card = new HistoryCardModel(title, chapter, entry.LastOpenedUtc);
                var coverKey = CoverKey(title);
                if (coverKey is not null && reusableCovers.TryGetValue(coverKey, out var cover))
                {
                    card.Cover = cover;
                }

                _history.Add(card);
            }
        }

        EmptyPanel.Visibility = _history.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        LoadMissingCovers();
    }

    private void HistoryCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HistoryCardModel card }) return;
        OpenChapterRequested?.Invoke(
            this,
            new OpenChapterRequestedEventArgs(card.Manga, card.Chapter));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_readingHistory is not null)
        {
            _readingHistory.Changed -= ReadingHistory_Changed;
        }

        _coverCancellation?.Cancel();
        _history.Clear();
    }

    private void ReadingHistory_Changed(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Refresh();
    }

    private Dictionary<string, BitmapSource> SnapshotCovers()
    {
        var covers = new Dictionary<string, BitmapSource>(StringComparer.Ordinal);
        foreach (var card in _history)
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

                if (cover is null || _disposed
                    || !ReferenceEquals(_coverCancellation, cancellation)) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_disposed && ReferenceEquals(_coverCancellation, cancellation))
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
