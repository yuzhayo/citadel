using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace Module.Mangareader.ShareLogic;

public sealed class HistoryCardModel : INotifyPropertyChanged, IDisposable
{
    public HistoryCardModel(
        MangaTitleCardModel title,
        ChapterInfo chapter,
        DateTimeOffset lastOpenedUtc)
    {
        TitleCard = title ?? throw new ArgumentNullException(nameof(title));
        Chapter = chapter ?? throw new ArgumentNullException(nameof(chapter));
        LastOpenedUtc = lastOpenedUtc;
        TitleCard.PropertyChanged += TitleCard_PropertyChanged;
    }

    public MangaTitleCardModel TitleCard { get; }

    public MangaTitle Manga => TitleCard.Manga;

    public ChapterInfo Chapter { get; }

    public DateTimeOffset LastOpenedUtc { get; }

    public string Title => Manga.Title;

    public BitmapSource? Cover => TitleCard.Cover;

    public string BadgeText => $"Last read · {Chapter.Title}";

    public string DetailLine1 => LastOpenedUtc.ToLocalTime().ToString("dd MMM yyyy");

    public string DetailLine2 => LastOpenedUtc.ToLocalTime().ToString("HH:mm");

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose() => TitleCard.PropertyChanged -= TitleCard_PropertyChanged;

    private void TitleCard_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MangaTitleCardModel.Cover))
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(Cover)));
        }
    }
}
