using Module.Mangareader.Archive;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.CoverBuilder;

public sealed record CoverBuilderResult(
    CoverSourceResult Source,
    CoverBakeResult Bake);

/// <summary>
/// Cover Builder's single bake operation and the only owner of its source
/// policy. A caller states what kind of source it has; resolving a remote URL
/// into Citadel-owned storage is an internal prerequisite of the bake, not
/// something each caller re-implements.
///
/// Also owns the conversion from a <see cref="MangaTitle"/> to the earliest
/// chapter path; the archive subsystem itself stays path-based and free of
/// MangaTitle and WPF.
/// </summary>
public sealed class CoverBuilderService
{
    private readonly CoverSourceLoader _sourceLoader = new();
    private readonly ArchiveReplacementTransaction _transaction = new();

    /// <summary>
    /// Explicit fetch to Citadel-owned storage. Optional for callers: it is a
    /// preview and cache-warm action, never a prerequisite for baking.
    /// </summary>
    public Task<FetchedCoverResult> FetchAsync(
        string sourceUrl,
        CancellationToken cancellationToken) =>
        _sourceLoader.FetchAsync(sourceUrl, cancellationToken);

    public async Task<CoverBuilderResult> BakeAsync(
        MangaTitle title,
        CoverSourceReference source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(source);

        var localPath = source switch
        {
            CoverSourceReference.LocalPath local => local.Path,
            CoverSourceReference.RemoteUrl remote =>
                await ResolveRemoteAsync(remote.Url, cancellationToken),
            _ => throw new ArgumentException("Unknown cover source kind.", nameof(source)),
        };

        return await BakeLocalAsync(title, localPath, cancellationToken);
    }

    /// <summary>
    /// A remote source is resolved before any archive mutation, so a failed
    /// download can never enter the transaction. A prior matching artifact is
    /// reused only while it still validates.
    /// </summary>
    private async Task<string> ResolveRemoteAsync(
        string url,
        CancellationToken cancellationToken)
    {
        if (_sourceLoader.TryGetFetchedArtifact(url, out var cached))
        {
            return cached;
        }

        var fetched = await _sourceLoader.FetchAsync(url, cancellationToken);
        return fetched.LocalPath;
    }

    private async Task<CoverBuilderResult> BakeLocalAsync(
        MangaTitle title,
        string localPath,
        CancellationToken cancellationToken)
    {
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
