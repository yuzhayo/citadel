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
///
/// The pin glyph and its accessible name are derived here so both the Grid card
/// and the List row render the same state without a second copy of the rule.
/// </summary>
public sealed class HistoryCardModel : INotifyPropertyChanged
{
    private const string PinGlyph = "\uE718";
    private const string PinnedGlyph = "\uE77A";

    private BitmapSource? _cover;
    private bool _isPinned;

    public HistoryCardModel(
        MangaTitle manga,
        ChapterInfo chapter,
        DateTimeOffset lastOpenedUtc,
        bool isPinned)
    {
        Manga = manga ?? throw new ArgumentNullException(nameof(manga));
        Chapter = chapter ?? throw new ArgumentNullException(nameof(chapter));
        LastOpenedUtc = lastOpenedUtc;
        _isPinned = isPinned;
    }

    public MangaTitle Manga { get; }

    public ChapterInfo Chapter { get; }

    public DateTimeOffset LastOpenedUtc { get; }

    public string Title => MangaCardPresentation.NormalizeTitle(Manga.Title);

    public string ChapterCountText => Manga.ChapterSummary;

    public string ProgressText => $"Last read · {Chapter.Title}";

    public string LastReadText => Chapter.Title;

    public string LastOpenedText => LastOpenedUtc.ToLocalTime().ToString("g");

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PinGlyphText));
            OnPropertyChanged(nameof(PinActionName));
        }
    }

    public string PinGlyphText => _isPinned ? PinnedGlyph : PinGlyph;

    public string PinActionName => _isPinned ? "Unpin from history" : "Pin to history";

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
