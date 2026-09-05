using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.History;

/// <summary>
/// History's own card presentation, built from domain data. It owns its Cover
/// rather than forwarding one from another feature's card model, so History
/// keeps showing covers without holding a Library instance. There is no
/// subscription to release, which is why this is not disposable.
/// </summary>
public sealed class HistoryCardModel : INotifyPropertyChanged
{
    private BitmapSource? _cover;

    public HistoryCardModel(
        MangaTitle manga,
        ChapterInfo chapter,
        DateTimeOffset lastOpenedUtc)
    {
        Manga = manga ?? throw new ArgumentNullException(nameof(manga));
        Chapter = chapter ?? throw new ArgumentNullException(nameof(chapter));
        LastOpenedUtc = lastOpenedUtc;
    }

    public MangaTitle Manga { get; }

    public ChapterInfo Chapter { get; }

    public DateTimeOffset LastOpenedUtc { get; }

    public string Title => MangaCardPresentation.NormalizeTitle(Manga.Title);

    public string ChapterCountText => Manga.ChapterSummary;

    public string ProgressText => $"Last read · {Chapter.Title}";

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
