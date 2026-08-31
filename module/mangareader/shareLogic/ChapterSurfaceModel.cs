using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Module.Mangareader.ShareLogic;

public enum ChapterSurfaceRole
{
    Previous,
    Active,
    Next,
}

public sealed class ChapterSurfaceModel : INotifyPropertyChanged
{
    private LoadedChapter _content;
    private ChapterSurfaceRole _role;

    public ChapterSurfaceModel(
        int chapterIndex,
        LoadedChapter content,
        ChapterSurfaceRole role)
    {
        ChapterIndex = chapterIndex;
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _role = role;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int ChapterIndex { get; }
    public ChapterInfo Chapter => _content.Chapter;
    public IReadOnlyList<LoadedPage> Pages => _content.Pages;
    public double SurfaceWidth => _content.SurfaceWidth;
    public double SurfaceHeight => _content.SurfaceHeight;
    public bool IsFullQuality => _content.Quality == PageRenderQuality.Full;
    public ChapterSurfaceRole Role => _role;

    public int ZIndex => _role switch
    {
        ChapterSurfaceRole.Active => 30,
        ChapterSurfaceRole.Next => 20,
        _ => 10,
    };

    public void ReplaceContent(LoadedChapter content)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        OnPropertyChanged(nameof(Chapter));
        OnPropertyChanged(nameof(Pages));
        OnPropertyChanged(nameof(SurfaceWidth));
        OnPropertyChanged(nameof(SurfaceHeight));
        OnPropertyChanged(nameof(IsFullQuality));
    }

    public void SetRole(ChapterSurfaceRole role)
    {
        if (_role == role) return;
        _role = role;
        OnPropertyChanged(nameof(Role));
        OnPropertyChanged(nameof(ZIndex));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
