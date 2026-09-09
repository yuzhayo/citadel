using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Module.Mangareader.ShareLogic;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.CatalogMirror;

// Presentation adapter for one snapshot title. It exposes exactly the names
// the module's MangaTitleCard binds — Title, Cover, ChapterCountText,
// ProgressText — so offline results render through the same card at the same
// size, while the snapshot item stays owned by this feature. It performs no
// I/O, network, cover caching, selection, and keeps no business state.
public sealed class CatalogMirrorCardModel : INotifyPropertyChanged
{
    private CatalogSnapshotItem _item;
    private BitmapSource? _cover;

    public CatalogMirrorCardModel(CatalogSnapshotItem item) =>
        _item = item ?? throw new ArgumentNullException(nameof(item));

    /// <summary>
    /// Refreshed in place so one adapter per identity survives a re-render: an
    /// already decoded cover is kept while newer labels replace the old ones.
    /// </summary>
    public CatalogSnapshotItem Item
    {
        get => _item;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (Equals(_item, value)) return;
            _item = value;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(CoverUrl));
            OnPropertyChanged(nameof(ChapterCountText));
            OnPropertyChanged(nameof(IsTitlePlaceholder));
        }
    }

    public string Title => MangaCardPresentation.NormalizeTitle(Item.Title);

    /// <summary>Empty until the screen's cover batch resolves it.</summary>
    public string CoverUrl => Item.CoverUrl ?? string.Empty;

    /// <summary>Card badge: the snapshot's own latest-chapter label.</summary>
    public string ChapterCountText => Item.LatestChapterLabel ?? string.Empty;

    /// <summary>
    /// A snapshot result has no local reading progress, and the card collapses
    /// an empty subtitle, so nothing is invented here.
    /// </summary>
    public string ProgressText => string.Empty;

    /// <summary>True when the title is the deterministic untitled placeholder.</summary>
    public bool IsTitlePlaceholder => Item.IsTitlePlaceholder;

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
