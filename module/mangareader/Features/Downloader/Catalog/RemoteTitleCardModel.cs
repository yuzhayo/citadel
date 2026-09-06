using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Module.Mangareader.Sources;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.Features.Downloader.Catalog;

/// <summary>
/// Presentation adapter for one remote catalog result. It exposes exactly the
/// names the module's <c>MangaTitleCard</c> binds — Title, Cover,
/// ChapterCountText, ProgressText — so catalog results and Library titles render
/// through the same card at the same size, while the remote summary and its
/// actions stay owned by this feature. The card never learns about Comix.
/// </summary>
public sealed class RemoteTitleCardModel : INotifyPropertyChanged
{
    private RemoteTitleSummary _summary;
    private BitmapSource? _cover;

    public RemoteTitleCardModel(RemoteTitleSummary summary) =>
        _summary = summary ?? throw new ArgumentNullException(nameof(summary));

    /// <summary>
    /// Refreshed in place so one adapter per identity survives a re-render: an
    /// already decoded cover is kept while the provider's newer labels replace
    /// the old ones. <see cref="RemoteTitleSummary"/> is a record, so assigning
    /// an equal value changes nothing.
    /// </summary>
    public RemoteTitleSummary Summary
    {
        get => _summary;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (Equals(_summary, value)) return;
            _summary = value;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(CoverUrl));
            OnPropertyChanged(nameof(ChapterCountText));
        }
    }

    public string Title => MangaCardPresentation.NormalizeTitle(Summary.DisplayName);

    /// <summary>Empty until the screen's cover batch resolves it.</summary>
    public string CoverUrl => Summary.CoverUrl ?? string.Empty;

    /// <summary>Card badge: the provider's own latest-chapter label.</summary>
    public string ChapterCountText => Summary.LatestChapterLabel ?? string.Empty;

    /// <summary>
    /// A remote result has no local reading progress, and the card collapses an
    /// empty subtitle, so nothing is invented here.
    /// </summary>
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
