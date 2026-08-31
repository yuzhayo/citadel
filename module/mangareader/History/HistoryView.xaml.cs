using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

public partial class HistoryView : UserControl, IDisposable
{
    private readonly ObservableCollection<HistoryCardModel> _history = new();
    private readonly ReadingHistoryStore _store = new();
    private IReadOnlyList<MangaTitleCardModel> _titles = Array.Empty<MangaTitleCardModel>();

    public HistoryView()
    {
        InitializeComponent();
        HistoryGrid.ItemsSource = _history;
    }

    public event EventHandler<OpenChapterRequestedEventArgs>? OpenChapterRequested;

    public void SetLibrary(IReadOnlyList<MangaTitleCardModel> titles)
    {
        _titles = titles ?? Array.Empty<MangaTitleCardModel>();
        Refresh();
    }

    public void Refresh()
    {
        ClearHistory();

        foreach (var entry in _store.Read())
        {
            var title = _titles.FirstOrDefault(candidate => string.Equals(
                candidate.FolderPath,
                entry.TitleFolderPath,
                StringComparison.OrdinalIgnoreCase));
            if (title is null) continue;

            var chapter = title.Manga.Chapters.FirstOrDefault(candidate => string.Equals(
                candidate.FilePath,
                entry.ChapterFilePath,
                StringComparison.OrdinalIgnoreCase));
            if (chapter is null) continue;

            _history.Add(new HistoryCardModel(title, chapter, entry.LastOpenedUtc));
        }

        EmptyPanel.Visibility = _history.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public void Record(MangaTitle title, ChapterInfo chapter)
    {
        try
        {
            _store.Record(title, chapter);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        Refresh();
    }

    private void HistoryCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HistoryCardModel card }) return;
        OpenChapterRequested?.Invoke(
            this,
            new OpenChapterRequestedEventArgs(card.Manga, card.Chapter));
    }

    public void Dispose() => ClearHistory();

    private void ClearHistory()
    {
        foreach (var card in _history) card.Dispose();
        _history.Clear();
    }
}
