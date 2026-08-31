namespace Module.Mangareader.ShareLogic;

public sealed record CoverBuilderResult(
    CoverSourceResult Source,
    CoverBakeResult Bake);

public sealed class CoverBuilderService
{
    private readonly CoverSourceLoader _sourceLoader = new();
    private readonly CbzCoverWriter _writer = new();

    public async Task<CoverBuilderResult> BuildAsync(
        MangaTitle title,
        string source,
        CancellationToken cancellationToken)
    {
        var loaded = await _sourceLoader.LoadAsync(source, cancellationToken);
        var baked = await _writer.BakeAsync(title, loaded.PngBytes, cancellationToken);
        return new CoverBuilderResult(loaded, baked);
    }
}
