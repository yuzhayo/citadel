using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Module.Mangareader.Library;

namespace Module.Mangareader.ShareLogic;

/// <summary>
/// Grid/picker display card for one title. Entry-first: every display
/// property (title, folder, added date, counts, labels) is available from
/// the index entry alone, so the grid never needs chapter lists. The full
/// <see cref="MangaTitle"/> attaches later, exactly once, when the title is
/// opened (or otherwise resolved); detail surfaces require it and fail fast
/// without it.
/// </summary>
public sealed class MangaTitleCardModel : INotifyPropertyChanged
{
    private MangaTitle? _full;
    private BitmapSource? _cover;

    public MangaTitleCardModel(LibraryIndexEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Title = MangaCardPresentation.NormalizeTitle(entry.TitleFolderName);
        FolderPath = entry.FolderPath;
        AddedUtc = entry.AddedUtc;
        ChapterCount = Math.Max(0, entry.ChapterCount);
        ChapterSummary = FormatCount(ChapterCount);
        LatestChapterLabel = entry.LatestChapterLabel;
    }

    public MangaTitleCardModel(MangaTitle manga)
        : this(EntryFromManga(manga))
    {
        AttachFullTitle(manga);
    }

    public LibraryIndexEntry Entry { get; }

    public string Title { get; }

    public string FolderPath { get; }

    public DateTime AddedUtc { get; }

    public int ChapterCount { get; private set; }

    public string ChapterSummary { get; private set; }

    public string LatestChapterLabel { get; private set; }

    public string ChapterCountText => ChapterSummary;

    public string ProgressText => string.Empty;

    /// <summary>
    /// The loaded title, or null while the card is display-only. Detail
    /// surfaces (chapter list, bake, open) require this; check
    /// <see cref="IsFull"/> or resolve through
    /// <see cref="Library.LibraryTitleLoader"/> first.
    /// </summary>
    public MangaTitle? FullTitle => _full;

    public bool IsFull => _full is not null;

    /// <summary>
    /// Attaches the lazily loaded title. The folder must match this card;
    /// counts and labels refresh to the authoritative full values.
    /// </summary>
    public void AttachFullTitle(MangaTitle manga)
    {
        ArgumentNullException.ThrowIfNull(manga);
        if (!string.Equals(manga.FolderPath, FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The attached title belongs to another folder.", nameof(manga));
        }

        _full = manga;
        ChapterCount = manga.ChapterCount;
        ChapterSummary = manga.ChapterSummary;
        LatestChapterLabel = manga.LatestChapterTitle;
        OnPropertyChanged(nameof(FullTitle));
        OnPropertyChanged(nameof(IsFull));
        OnPropertyChanged(nameof(ChapterCount));
        OnPropertyChanged(nameof(ChapterSummary));
        OnPropertyChanged(nameof(ChapterCountText));
        OnPropertyChanged(nameof(LatestChapterLabel));
    }

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

    private static LibraryIndexEntry EntryFromManga(MangaTitle manga)
    {
        ArgumentNullException.ThrowIfNull(manga);
        return new LibraryIndexEntry(
            manga.Title,
            manga.FolderPath,
            manga.AddedUtc,
            manga.ChapterCount,
            manga.FirstChapterTitle,
            manga.LatestChapterTitle,
            DateTime.MinValue,
            string.Empty,
            string.Empty);
    }

    private static string FormatCount(int count) =>
        count == 1 ? "1 chapter" : $"{count} chapters";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
