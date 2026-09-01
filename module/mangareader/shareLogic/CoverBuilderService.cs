using Module.Mangareader.Archive;

namespace Module.Mangareader.ShareLogic;

public sealed record CoverBuilderResult(
    CoverSourceResult Source,
    CoverBakeResult Bake);

/// <summary>
/// Owns the conversion from a <see cref="MangaTitle"/> to the earliest
/// chapter path; the archive subsystem itself stays path-based and free of
/// MangaTitle and WPF.
/// </summary>
public sealed class CoverBuilderService
{
    private readonly CoverSourceLoader _sourceLoader = new();
    private readonly ArchiveReplacementTransaction _transaction = new();

    public Task<FetchedCoverResult> FetchAsync(
        string sourceUrl,
        CancellationToken cancellationToken) =>
        _sourceLoader.FetchAsync(sourceUrl, cancellationToken);

    public async Task<CoverBuilderResult> BuildFromLocalAsync(
        MangaTitle title,
        string localPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);
        var chapter = title.Chapters.FirstOrDefault()
            ?? throw new InvalidOperationException("The title has no chapter to receive a cover.");

        var loaded = await _sourceLoader.LoadLocalAsync(localPath, cancellationToken);
        var baked = await _transaction.BakeAsync(
            chapter.FilePath,
            loaded.PngBytes,
            cancellationToken);
        return new CoverBuilderResult(loaded, baked);
    }
}
