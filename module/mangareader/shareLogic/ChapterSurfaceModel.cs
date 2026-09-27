using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Module.Mangareader.ShareLogic;

public enum ChapterSurfaceRole
{
    Previous,
    Active,
    Next,
}

/// <summary>
/// One chapter inside the reader's rolling window.
/// <para>
/// The surface has two phases. While live it owns its decoded <see cref="BitmapSource"/>
/// pages. <see cref="Evict"/> drops that ownership while keeping every page box, so
/// the last owner of the unmanaged pixel buffers can be collected without changing
/// this surface's geometry. Page height is bound explicitly in ReaderWindow.xaml, so
/// a null source leaves the layout box intact.
/// </para>
/// </summary>
public sealed class ChapterSurfaceModel : INotifyPropertyChanged
{
    private LoadedChapter? _content;
    private IReadOnlyList<LoadedPage>? _evictedPages;
    private ChapterInfo _chapter;
    private double _surfaceWidth;
    private double _surfaceHeight;
    private PageRenderQuality _quality;
    private ChapterSurfaceRole _role;

    public ChapterSurfaceModel(
        int chapterIndex,
        LoadedChapter content,
        ChapterSurfaceRole role)
    {
        ChapterIndex = chapterIndex;
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
        _chapter = content.Chapter;
        _surfaceWidth = content.SurfaceWidth;
        _surfaceHeight = content.SurfaceHeight;
        _quality = content.Quality;
        _role = role;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int ChapterIndex { get; }
    public ChapterInfo Chapter => _chapter;
    public IReadOnlyList<LoadedPage> Pages => _evictedPages ?? _content!.Pages;
    public double SurfaceWidth => _surfaceWidth;
    public double SurfaceHeight => _surfaceHeight;
    public long EstimatedBitmapBytes => _content?.EstimatedBitmapBytes ?? 0L;

    /// <summary>False once evicted, so consumers never reuse a surface whose pixels are gone.</summary>
    public bool IsFullQuality => _content is not null && _quality == PageRenderQuality.Full;
    public bool IsEvicted => _content is null;
    public ChapterSurfaceRole Role => _role;

    public int ZIndex => _role switch
    {
        ChapterSurfaceRole.Active => 30,
        ChapterSurfaceRole.Next => 20,
        _ => 10,
    };

    public void SetRole(ChapterSurfaceRole role)
    {
        if (_role == role) return;
        _role = role;
        OnPropertyChanged(nameof(Role));
        OnPropertyChanged(nameof(ZIndex));
    }

    /// <summary>
    /// Releases the decoded bitmaps and keeps the page boxes, geometry, and identity.
    /// Intended for a surface already detached from the reader's surface collection, so
    /// that no layout invalidation is involved. Idempotent.
    /// </summary>
    public void Evict()
    {
        if (_content is null) return;
        _evictedPages = _content.Pages
            .Select(page => page with { Bitmap = null })
            .ToArray();
        _content = null;
        OnPropertyChanged(nameof(Pages));
        OnPropertyChanged(nameof(IsFullQuality));
        OnPropertyChanged(nameof(IsEvicted));
        OnPropertyChanged(nameof(EstimatedBitmapBytes));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
