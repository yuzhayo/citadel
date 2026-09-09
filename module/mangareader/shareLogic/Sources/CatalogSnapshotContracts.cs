using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Module.Mangareader.Sources;

// The provider-neutral snapshot contract: what one offline-catalog partition
// traversal exchanges. It lives beside MangaSourceContracts because the
// snapshot source is an optional facet of a registered source, not Downloader
// UI or feature state. It never knows Comix, WPF, the filesystem, the queue,
// the browser client, or any feature implementation. Raw request URLs, query
// strings, tokens, cookies, headers and provider JSON must never cross this
// boundary — only the normalized values below.

/// <summary>
/// Fixed limits for one snapshot traversal and its files (plan 5.3). The
/// store enforces them; exceeding a limit stops the sync while keeping the
/// checkpoint and the previous active snapshot.
/// </summary>
public static class CatalogSnapshotLimits
{
    public const int MaxPagesPerPartition = 5_000;
    public const long MaxUniqueTitles = 250_000;
    public const int MaxRecordBytes = 1_048_576;
}

/// <summary>Schema version of the snapshot checkpoint records.</summary>
public static class CatalogSnapshotSchema
{
    public const int Version = 1;
}

/// <summary>
/// One ordered traversal partition offered by a snapshot source. Both the key
/// and the rating label are source-defined; the mirror never invents them.
/// </summary>
public sealed record CatalogSnapshotPartition(string Key, string Rating);

/// <summary>
/// One immutable snapshot title. Identity is <c>SourceId + TitleId</c>; the
/// display title is never identity.
/// </summary>
public sealed record CatalogSnapshotItem(
    string SourceId,
    string TitleId,
    string TitleHid,
    string CanonicalUrl,
    string Title,
    IReadOnlyList<string> AlternateTitles,
    string? CoverUrl,
    long? LatestChapterValue,
    string? LatestChapterLabel,
    string Rating,
    string? Type,
    string? Status,
    string? Language,
    int? Year,
    string? Synopsis,
    DateTimeOffset CapturedAtUtc,
    bool IsTitlePlaceholder,
    DateTimeOffset? UpdatedAtUtc = null);

/// <summary>One provider-observed genre offered for background enrichment.</summary>
public sealed record CatalogSnapshotGenre(string Key, string DisplayName);

/// <summary>One genre traversal page containing only stable title identities.</summary>
public sealed record CatalogSnapshotGenrePage(
    IReadOnlyList<string> TitleIds,
    long? ProviderTotal,
    int Page,
    bool HasMore);

/// <summary>
/// Deterministic placeholder vocabulary for identity-valid items the provider
/// served without a display title (plan 5.2 revision 2026-09-08). One shared
/// definition so the adapter, the store and the tests can never drift apart.
/// </summary>
public static class CatalogSnapshotTitles
{
    public static string PlaceholderFor(string titleHid) => "(Untitled " + titleHid + ")";
}

/// <summary>
/// The single normalized search-text rule: trimmed, invariant-lowercase
/// display title plus alternate titles. The in-memory index, the database
/// column and the importer share it so search can never disagree per backend.
/// </summary>
public static class CatalogSnapshotText
{
    public static string NormalizeSearchText(string title, IEnumerable<string> alternateTitles)
    {
        var builder = new StringBuilder(Normalize(title));
        foreach (var alternate in alternateTitles)
        {
            builder.Append('\n');
            builder.Append(Normalize(alternate));
        }

        return builder.ToString();
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}

/// <summary>
/// One snapshot page. <see cref="ProviderTotal"/> is the provider's own
/// diagnostic count and never an activation requirement; <see cref="HasMore"/>
/// is the provider's own pagination verdict, never recomputed from the item
/// count.
/// </summary>
public sealed record CatalogSnapshotPage(
    IReadOnlyList<CatalogSnapshotItem> Items,
    long? ProviderTotal,
    int Page,
    bool HasMore);

/// <summary>
/// Optional facet a source implements when it can serve whole-catalog
/// snapshot traversal. Sources without it keep working in the online
/// Downloader but are never offered for Catalog sync.
/// </summary>
public interface ICatalogSnapshotSource
{
    string SourceId { get; }

    IReadOnlyList<CatalogSnapshotPartition> SnapshotPartitions { get; }

    Task<CatalogSnapshotPage> GetSnapshotPageAsync(
        CatalogSnapshotPartition partition,
        int page,
        CancellationToken cancellationToken,
        bool latestFirst = false);
}

/// <summary>
/// Optional second snapshot facet. Genre traversal is deliberately separate
/// from the base title sync because the browse response does not carry genres.
/// </summary>
public interface ICatalogGenreSnapshotSource
{
    string SourceId { get; }

    IReadOnlyList<CatalogSnapshotGenre> SnapshotGenres { get; }

    Task<CatalogSnapshotGenrePage> GetGenreSnapshotPageAsync(
        CatalogSnapshotGenre genre,
        int page,
        CancellationToken cancellationToken);
}
