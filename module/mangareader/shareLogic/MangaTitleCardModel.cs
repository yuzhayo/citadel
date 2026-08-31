using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace Module.Mangareader.ShareLogic;

public sealed class MangaTitleCardModel : INotifyPropertyChanged
{
    private BitmapSource? _cover;

    public MangaTitleCardModel(MangaTitle manga)
    {
        Manga = manga ?? throw new ArgumentNullException(nameof(manga));
    }

    public MangaTitle Manga { get; }

    public string Title => Manga.Title;

    public string FolderPath => Manga.FolderPath;

    public string ChapterSummary => Manga.ChapterSummary;

    public string BadgeText => ChapterSummary;

    public string FirstChapterTitle => Manga.FirstChapterTitle;

    public string LatestChapterTitle => Manga.LatestChapterTitle;

    public string DetailLine1 => $"First: {FirstChapterTitle}";

    public string DetailLine2 => $"Latest: {LatestChapterTitle}";

    public BitmapSource? Cover
    {
        get => _cover;
        set
        {
            if (ReferenceEquals(_cover, value)) return;
            _cover = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
