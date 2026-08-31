namespace Module.Mangareader.ShareLogic;

public sealed record CoverBuilderResult(
    CoverSourceResult Source,
    CoverBakeResult Bake);

public sealed class CoverBuilderService
{
    private readonly CoverSourceLoader _sourceLoader = new();
    private readonly CbzCoverWriter _writer = new();

    public Task<FetchedCoverResult> FetchAsync(
        string sourceUrl,
        CancellationToken cancellationToken) =>
        _sourceLoader.FetchAsync(sourceUrl, cancellationToken);

    public async Task<CoverBuilderResult> BuildFromLocalAsync(
        MangaTitle title,
        string localPath,
        CancellationToken cancellationToken)
    {
        var loaded = await _sourceLoader.LoadLocalAsync(localPath, cancellationToken);
        var baked = await _writer.BakeAsync(title, loaded.PngBytes, cancellationToken);
        return new CoverBuilderResult(loaded, baked);
    }
}
