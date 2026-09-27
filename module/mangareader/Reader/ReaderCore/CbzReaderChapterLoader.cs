using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.ReaderCore;

/// <summary>
/// Production adapter that satisfies the Reader's chapter-loader contract with the
/// shared CBZ decoder. Lives in ReaderCore rather than the citizen-shared
/// shareLogic payload because it is Reader-owned: shipping it to every citizen
/// would widen their surface for nothing.
/// </summary>
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
