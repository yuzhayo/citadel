using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>Production adapter from the Reader engine to the CBZ decoder.</summary>
public sealed class CbzReaderChapterLoader : IReaderChapterLoader
{
    private readonly CbzChapterLoader _inner = new();

    public Task<LoadedChapter> LoadAsync(
        ChapterInfo chapter,
        ChapterRenderRequest request,
        IProgress<ChapterLoadProgress>? progress,
        CancellationToken cancellationToken) =>
        _inner.LoadAsync(chapter, request, progress, cancellationToken);
}
