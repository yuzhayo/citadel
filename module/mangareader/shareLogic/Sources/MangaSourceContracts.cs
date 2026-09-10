namespace Module.Mangareader.Sources;

// The neutral remote-source contract: what one provider can do, the immutable
// values it exchanges, and the read surface used to resolve one. It lives at
// module level rather than inside the Downloader because Library's Update Checker
// is a second real consumer, and a Library feature must not have to import a
// Downloader namespace in order to talk to a provider.
//
// Deliberately still owned by the Downloader: the explicit registration list, the
// UI-flavored filter contribution, and every concrete adapter such as Comix. This
// file declares the contract; it never interprets provider behavior.

/// <summary>
/// What one registered source can actually do. Catalog reads this to decide
/// which controls exist; it never probes a provider to find out.
/// </summary>
public sealed record MangaSourceCapabilities(
    bool SupportsSearch,
    bool SupportsAdvancedFilters,
    IReadOnlyList<RemoteLookupKind> LookupKinds,
    bool TransformsPages);

public enum RemoteLookupKind
{
    Author,
    Artist,
    Genre,
    Format,
}

/// <summary>
/// A provider-owned, UI-free browse filter. Catalog carries it opaquely from
/// the filter feature to the source that produced it and never switches on a
/// concrete implementation.
/// </summary>
public interface IRemoteBrowseFilter
{
    string SourceId { get; }
}

/// <summary>
/// One browse request. <see cref="Page"/> is one-based; the source rejects
/// anything else at its boundary.
/// </summary>
public sealed record RemoteBrowseRequest(string? Query, int Page, IRemoteBrowseFilter? Filter);

/// <summary>
/// One catalog response. <see cref="Total"/> is null when the provider did not
/// report a count, which is distinct from a reported zero.
/// </summary>
public sealed record RemoteCatalogPage(
    IReadOnlyList<RemoteTitleSummary> Items,
    long? Total,
    int Page,
    bool HasMore);

/// <summary>
/// Remote title identity. A display title is never identity: the provider's
/// internal id and hid are what a mapping is confirmed against.
/// </summary>
public sealed record RemoteTitleIdentity(
    string SourceId,
    string TitleId,
    string TitleHid,
    string Slug);

public sealed record RemoteTitleSummary(
    RemoteTitleIdentity Identity,
    string DisplayName,
    string? CoverUrl,
    string? LatestChapterLabel)
{
    /// <summary>
    /// Provider-owned alternatives for the same cover, tried only when the
    /// primary URL cannot be fetched or decoded. Existing providers keep the
    /// empty default and therefore retain their single-request behavior.
    /// </summary>
    public IReadOnlyList<string> CoverFallbackUrls { get; init; } = [];

    public IEnumerable<string> CoverCandidates()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(CoverUrl) && seen.Add(CoverUrl)) yield return CoverUrl;
        foreach (var fallback in CoverFallbackUrls)
        {
            if (!string.IsNullOrWhiteSpace(fallback) && seen.Add(fallback)) yield return fallback;
        }
    }

    public override string ToString() => DisplayName;
}

public sealed record RemoteTitleDetail(
    RemoteTitleSummary Summary,
    string? Description,
    IReadOnlyList<RemoteOption> Genres,
    IReadOnlyList<RemoteOption> Metadata);

/// <summary>
/// One selectable provider option. <see cref="DisplayName"/> is always
/// non-empty and is what object-backed lists expose, so an accessible name
/// never falls back to a record's default ToString.
/// </summary>
public sealed record RemoteOption(string Key, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record RemoteGroupIdentity(string SourceId, string GroupId);

public sealed record RemoteSourceGroup(RemoteGroupIdentity Identity, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Chapter number is display and matching metadata only. Identity is the
/// provider's chapter key plus its group, because several groups publish the
/// same number as distinct variants.
/// </summary>
public sealed record RemoteChapterIdentity(
    string SourceId,
    RemoteTitleIdentity Title,
    string ChapterId,
    string ChapterNumber,
    RemoteGroupIdentity Group);

public sealed record RemoteChapterSummary(
    RemoteChapterIdentity Identity,
    string DisplayName,
    int OrderIndex)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// An opaque, source-owned transform descriptor. The pipeline hands it back to
/// the same source and receives validated bytes; it never interprets the value.
/// </summary>
public sealed record RemotePageTransform(
    string Kind,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record RemotePage(
    int Ordinal,
    string RemoteKey,
    string Url,
    long? ExpectedBytes,
    RemotePageTransform? Transform);

/// <summary>
/// The ordered page manifest is the completeness authority for one chapter.
/// <see cref="ManifestHash"/> stabilizes identity, so a refreshed manifest that
/// changes order or content is a visible conflict rather than a silent merge.
/// </summary>
public sealed record RemoteChapterManifest(
    RemoteChapterIdentity Chapter,
    IReadOnlyList<RemotePage> Pages,
    string ManifestHash,
    IReadOnlyDictionary<string, string> RequestHeaders)
{
    public int PageCount => Pages.Count;
}

public sealed record RemoteLookupOption(string Key, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// One exact chapter match in a different source group. A cross-group fallback
/// always replaces the whole chapter job; it can never supply a single page to
/// the originally selected group's archive.
/// </summary>
public sealed record RemoteAlternateChapter(
    RemoteGroupIdentity Group,
    string GroupDisplayName,
    string ChapterId,
    string Reason);

/// <summary>Decoded page bytes plus the format actually detected from them.</summary>
public sealed record RemotePageImage(byte[] Bytes, string Format);

/// <summary>
/// The neutral read surface over the one explicit source registry. A consumer
/// that only needs to resolve a provider depends on this instead of on the
/// registry type, so it never has to import a filter contribution, a queue or a
/// screen. It resolves sources; it does not interpret what they return.
/// </summary>
public interface IMangaSourceDirectory
{
    IReadOnlyList<IMangaSource> AvailableSources { get; }

    IMangaSource? FindSource(string? sourceId);
}

/// <summary>
/// One remote manga source. UI-free and queue-free: the adapter owns routes,
/// signing, parsing and normalization, and nothing else.
/// </summary>
public interface IMangaSource
{
    string Id { get; }

    string DisplayName { get; }

    MangaSourceCapabilities Capabilities { get; }

    Task<RemoteCatalogPage> BrowseAsync(
        RemoteBrowseRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(
        RemoteLookupKind kind,
        string query,
        CancellationToken cancellationToken);

    Task<RemoteTitleDetail> GetTitleAsync(
        RemoteTitleIdentity title,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteSourceGroup>> GetGroupsAsync(
        RemoteTitleIdentity title,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteChapterSummary>> GetChaptersAsync(
        RemoteTitleIdentity title,
        RemoteGroupIdentity group,
        CancellationToken cancellationToken);

    Task<RemoteChapterManifest> GetManifestAsync(
        RemoteChapterIdentity chapter,
        CancellationToken cancellationToken);

    /// <summary>
    /// The single callable seam for source-owned page transforms. The pipeline
    /// passes the manifest page and the bytes it downloaded; the source returns
    /// validated image bytes. A source without transforms returns the payload
    /// with its detected format.
    /// </summary>
    Task<RemotePageImage> TransformPageAsync(
        RemotePage page,
        byte[] payload,
        CancellationToken cancellationToken);

    /// <summary>
    /// Searches the other groups of the same title for an exact match of this
    /// chapter number. Matching is by the provider's chapter number, which is
    /// display metadata — the returned identity always carries the alternate
    /// group, so a variant is never merged into the original group's archive.
    /// </summary>
    Task<IReadOnlyList<RemoteAlternateChapter>> FindAlternateGroupsAsync(
        RemoteChapterIdentity chapter,
        CancellationToken cancellationToken);
}
