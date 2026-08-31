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

    public string Title => MangaCardPresentation.NormalizeTitle(Manga.Title);

    public string FolderPath => Manga.FolderPath;

    public string ChapterSummary => Manga.ChapterSummary;

    public string ChapterCountText => ChapterSummary;

    public string ProgressText => string.Empty;

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

    public override string ToString() => Title;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
