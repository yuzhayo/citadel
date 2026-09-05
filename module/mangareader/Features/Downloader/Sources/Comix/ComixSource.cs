using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Module.Mangareader.Features.Downloader.Sources.Comix;

/// <summary>
/// The whole Comix wire contract in one place: routes, query keys and response
/// field names. Frozen from one captured live request that answered 200
/// (2026-09-05, `GET https://comix.ws/api/v1/manga`). Anything not captured is
/// listed in <see cref="UncapturedFilterKeys"/> and is refused visibly rather
/// than sent under an invented name.
/// </summary>
public static class ComixContract
{
    public const int Version = 2;

    public const string SourceId = "comix";
    public const string DisplayName = "Comix";

    /// <summary>The host that answers. Captured live: 200 in ~0.4 s.</summary>
    public const string BaseUrl = "https://comix.ws";

    /// <summary>Referer required by image downloads.</summary>
    public const string ImageReferer = "https://comix.ws/";

    /// <summary>Captured: the browse list the Catalog Start button drives.</summary>
    public const string RouteBrowse = "/api/v1/manga";

    /// <summary>
    /// Captured live through the page client: 200, decrypted, and it carries the
    /// detail fields the browse item does not (genres, authors, first/latest
    /// chapter url). The rendered title page never calls it, which is why an
    /// earlier network-only capture missed it.
    /// </summary>
    public const string RouteTitle = "/api/v1/manga/{0}";

    /// <summary>
    /// Captured live through the page client: 200 and decrypted
    /// (<c>{items, meta}</c>). The raw network body is encrypted (<c>{"e":…}</c>);
    /// the site's own interceptor is what decrypts it, so this route is only
    /// usable through that client.
    /// </summary>
    public const string RouteChapters = "/api/v1/manga/{0}/chapters";

    /// <summary>
    /// Captured live through the page client: 200, decrypted, and it carries the
    /// page manifest under <c>pages.items</c>.
    /// </summary>
    public const string RouteChapter = "/api/v1/chapters/{0}";

    /// <summary>Captured query keys, exactly as the site sent them.</summary>
    public const string KeyOrder = "order[{0}]";
    public const string KeyPage = "page";
    public const string KeyLimit = "limit";
    public const string KeyContentRating = "content_rating[]";
    public const string KeyTypes = "types[]";
    public const string KeyKeyword = "keyword";

    /// <summary>The captured order column for "latest update".</summary>
    public const string OrderLatestColumn = "chapter_updated_at";
    public const string OrderDescending = "desc";

    /// <summary>The captured chapter order column and page size.</summary>
    public const string OrderNumberColumn = "number";
    public const int ChapterPageSize = 20;

    /// <summary>Captured browse page size.</summary>
    public const int PageSize = 28;

    /// <summary>
    /// Captured browse payload root. The site's own client already unwrapped the
    /// envelope, so C# reads <c>items</c> and <c>meta</c> from the root.
    /// </summary>
    public const string FieldItems = "items";
    public const string FieldMeta = "meta";

    /// <summary>The two captured pagination fields the adapter reads.</summary>
    public const string MetaTotal = "total";
    public const string MetaHasNext = "hasNext";

    /// <summary>Captured title item fields.</summary>
    public const string FieldId = "id";
    public const string FieldHid = "hid";
    public const string FieldTitle = "title";
    public const string FieldAltTitles = "altTitles";
    public const string FieldType = "type";
    public const string FieldItemStatus = "status";
    public const string FieldOriginalLanguage = "originalLanguage";
    public const string FieldPoster = "poster";
    public const string PosterMedium = "medium";
    public const string PosterLarge = "large";
    public const string FieldLatestChapter = "latestChapter";
    public const string FieldYear = "year";
    public const string FieldSynopsis = "synopsis";

    /// <summary>Captured detail-only fields on <c>/manga/{hid}</c>.</summary>
    public const string FieldUrl = "url";
    public const string FieldGenres = "genres";
    public const string FieldAuthors = "authors";
    public const string FieldArtists = "artists";

    /// <summary>
    /// Every captured option list entry is the same triple, so one set of names
    /// reads genres, demographics, formats, authors, artists and publishers.
    /// </summary>
    public const string OptionId = "id";
    public const string OptionTitle = "title";
    public const string OptionSlug = "slug";

    /// <summary>Captured chapter item fields.</summary>
    public const string FieldChapterId = "id";
    public const string FieldNumber = "number";
    public const string FieldChapterName = "name";
    public const string FieldGroupId = "groupId";
    public const string FieldGroup = "group";
    public const string FieldGroupName = "name";

    /// <summary>Captured manifest page container on <c>/chapters/{id}</c>.</summary>
    public const string FieldPages = "pages";
    public const string FieldPagesBaseUrl = "baseUrl";
    public const string FieldPageUrl = "url";
}

/// <summary>
/// A Comix wire-contract failure. Distinct from a network failure so the UI can
/// say "the provider shape changed" instead of "the network is down".
/// </summary>
public sealed class ComixContractException(string message) : InvalidOperationException(message);

public enum ComixGenreMode
{
    And,
    Or,
}

/// <summary>
/// One immutable browse query, produced by the Comix filter feature and carried
/// opaquely by Catalog. Nothing outside Comix reads its fields.
/// </summary>
public sealed record ComixBrowseQuery : IRemoteBrowseFilter
{
    public static readonly ComixBrowseQuery Default = new()
    {
        SortKey = ComixOptions.DefaultSortKey,
        Ratings = ComixOptions.DefaultRatingKeys,
    };

    public string SourceId => ComixContract.SourceId;

    public string? Search { get; init; }

    public string SortKey { get; init; } = ComixOptions.DefaultSortKey;

    public IReadOnlyList<string> Ratings { get; init; } = [];

    public IReadOnlyList<string> Types { get; init; } = [];

    public IReadOnlyList<string> Genres { get; init; } = [];

    public IReadOnlyList<string> Formats { get; init; } = [];

    public ComixGenreMode GenreMode { get; init; } = ComixGenreMode.And;

    public IReadOnlyList<string> Demographics { get; init; } = [];

    public IReadOnlyList<string> Statuses { get; init; } = [];

    public int? MinimumChapter { get; init; }

    public int? YearFrom { get; init; }

    public int? YearTo { get; init; }

    public string? AuthorKey { get; init; }

    public string? ArtistKey { get; init; }

    /// <summary>
    /// Local validation only. An invalid range blocks Start; it never reaches
    /// the provider and never produces a silent default. A filter whose live
    /// query key was never captured also blocks Start, because sending it under
    /// a guessed name is how this contract drifted before.
    /// </summary>
    public string? ValidationError
    {
        get
        {
            if (MinimumChapter is < 0)
            {
                return "Minimum chapter cannot be negative.";
            }

            if (YearFrom is not null && (YearFrom < 1900 || YearFrom > 2999))
            {
                return "Release year must be between 1900 and 2999.";
            }

            if (YearTo is not null && (YearTo < 1900 || YearTo > 2999))
            {
                return "Release year must be between 1900 and 2999.";
            }

            if (YearFrom is { } from && YearTo is { } to && from > to)
            {
                return "The 'from' release year cannot be later than the 'to' year.";
            }

            var uncaptured = UncapturedFilters();
            if (uncaptured.Count > 0)
            {
                return "Filter ini belum punya key live yang terbukti: "
                    + string.Join(", ", uncaptured)
                    + ". Kosongkan dulu, atau tangkap kontraknya dari web Comix.";
            }

            return null;
        }
    }

    /// <summary>Which chosen filters have no captured live query key.</summary>
    public IReadOnlyList<string> UncapturedFilters()
    {
        var chosen = new List<string>();
        if (Genres.Count > 0) chosen.Add("genre");
        if (Formats.Count > 0) chosen.Add("format");
        if (Demographics.Count > 0) chosen.Add("demographic");
        if (Statuses.Count > 0) chosen.Add("status");
        if (MinimumChapter is not null) chosen.Add("minimum chapter");
        if (YearFrom is not null || YearTo is not null) chosen.Add("release year");
        if (!string.IsNullOrWhiteSpace(AuthorKey)) chosen.Add("author");
        if (!string.IsNullOrWhiteSpace(ArtistKey)) chosen.Add("artist");
        return chosen;
    }

    /// <summary>
    /// The captured browse wire form: <c>order[chapter_updated_at]=desc</c> plus
    /// one repeated <c>content_rating[]</c> per rating and <c>types[]</c> per
    /// type. Page, limit and keyword are request-level and added by the adapter.
    /// </summary>
    public string ToQueryString()
    {
        var query = new StringBuilder();
        Append(
            query,
            string.Format(CultureInfo.InvariantCulture, ComixContract.KeyOrder, OrderColumn),
            ComixContract.OrderDescending);
        AppendMany(query, ComixContract.KeyContentRating, Ratings);
        AppendMany(query, ComixContract.KeyTypes, Types);
        return query.ToString();
    }

    /// <summary>
    /// Only the captured order column is offered, so the sort picker cannot
    /// produce a key the site has never been seen accepting.
    /// </summary>
    private string OrderColumn => ComixContract.OrderLatestColumn;

    private static void Append(StringBuilder query, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (query.Length > 0) query.Append('&');
        query.Append(Uri.EscapeDataString(key))
            .Append('=')
            .Append(Uri.EscapeDataString(value));
    }

    private static void AppendMany(StringBuilder query, string key, IReadOnlyList<string> values)
    {
        foreach (var value in values)
        {
            Append(query, key, value);
        }
    }
}

/// <summary>
/// Provider option catalogs. Only values with recorded evidence are listed;
/// display titles are always non-empty so an object-backed list never falls
/// back to a record's ToString for its accessible name. Options that the
/// provider resolves remotely (genres, formats, authors, artists) are fetched
/// through <see cref="IMangaSource.LookupAsync"/> instead of being hardcoded.
/// </summary>
public static class ComixOptions
{
    /// <summary>
    /// The provider exposes more sort choices than research captured. Only the
    /// observed default is listed here; the remaining options must be captured
    /// live and added to this table rather than invented.
    /// </summary>
    public const string DefaultSortKey = "latest";

    public static readonly IReadOnlyList<RemoteOption> Sorts =
    [
        new(DefaultSortKey, "Latest update"),
    ];

    public static readonly IReadOnlyList<RemoteOption> Ratings =
    [
        new("safe", "Safe"),
        new("suggestive", "Suggestive"),
        new("erotica", "Erotica"),
        new("pornographic", "Pornographic"),
    ];

    /// <summary>
    /// The ratings the site's own first browse request carried: safe and
    /// suggestive. Captured live, not assumed.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultRatingKeys = ["safe", "suggestive"];

    public static readonly IReadOnlyList<RemoteOption> Types =
    [
        new("manga", "Manga"),
        new("manhwa", "Manhwa"),
        new("manhua", "Manhua"),
        new("other", "Other"),
    ];

    public static readonly IReadOnlyList<RemoteOption> Demographics =
    [
        new("josei", "Josei"),
        new("seinen", "Seinen"),
        new("shoujo", "Shoujo"),
        new("shounen", "Shounen"),
    ];

    public static readonly IReadOnlyList<RemoteOption> Statuses =
    [
        new("releasing", "Releasing"),
        new("finished", "Finished"),
        new("on_hiatus", "On hiatus"),
        new("discontinued", "Discontinued"),
        new("not_yet_released", "Not yet released"),
    ];
}

/// <summary>
/// The Comix adapter: routes, page-context calls, parsing and normalization.
/// No WPF, no queue, no archive writing.
/// </summary>
public sealed class ComixSource(DownloaderPyHostClient client) : IMangaSource
{
    /// <summary>
    /// Bound on one chapter listing: 100 captured pages of 20 is 2000 chapters,
    /// so a provider that keeps reporting <c>hasNext</c> cannot turn one Details
    /// click into unbounded requests.
    /// </summary>
    private const int MaximumChapterPages = 100;

    private readonly DownloaderPyHostClient _client = client;

    public string Id => ComixContract.SourceId;

    public string DisplayName => ComixContract.DisplayName;

    public MangaSourceCapabilities Capabilities { get; } = new(
        SupportsSearch: true,
        SupportsAdvancedFilters: true,
        LookupKinds:
        [
            RemoteLookupKind.Author,
            RemoteLookupKind.Artist,
            RemoteLookupKind.Genre,
            RemoteLookupKind.Format,
        ],
        TransformsPages: true);

    public async Task<RemoteCatalogPage> BrowseAsync(
        RemoteBrowseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "page is one-based");
        }

        var filter = request.Filter as ComixBrowseQuery ?? ComixBrowseQuery.Default;
        var validation = filter.ValidationError;
        if (validation is not null)
        {
            throw new ComixContractException("Filter is invalid: " + validation);
        }

        var query = new StringBuilder(filter.ToQueryString());
        AppendKey(query, ComixContract.KeyPage, request.Page.ToString(CultureInfo.InvariantCulture));
        AppendKey(query, ComixContract.KeyLimit, ComixContract.PageSize.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            AppendKey(query, ComixContract.KeyKeyword, request.Query.Trim());
        }

        var result = await GetResultAsync(ComixContract.RouteBrowse, query.ToString(), cancellationToken)
            .ConfigureAwait(false);
        return ReadCatalogPage(result, request.Page);
    }

    /// <summary>
    /// Decodes the captured browse result: <c>items</c> plus the provider's own
    /// <c>meta</c> pagination verdict, which is never recomputed from the item
    /// count because that would guess at the page size.
    /// </summary>
    internal static RemoteCatalogPage ReadCatalogPage(JsonObject result, int page)
    {
        var items = result[ComixContract.FieldItems] as JsonArray ?? throw Missing(ComixContract.FieldItems);

        var summaries = new List<RemoteTitleSummary>(items.Count);
        foreach (var item in items)
        {
            if (item is JsonObject entry)
            {
                summaries.Add(ReadSummary(entry));
            }
        }

        var meta = result[ComixContract.FieldMeta] as JsonObject;
        var total = meta is null ? null : ReadLong(meta, ComixContract.MetaTotal);
        return new RemoteCatalogPage(summaries, total, page, HasMore: HasNextPage(result));
    }

    public Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(
        RemoteLookupKind kind,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        // No lookup endpoint was captured live, and every one of these filters is
        // listed as uncaptured. Inventing a route or an option array here is how
        // the previous contract drifted, so the panel shows a real refusal.
        throw new ComixContractException(
            $"Lookup '{kind}' belum punya endpoint live yang ditangkap dari web Comix.");
    }

    public async Task<RemoteTitleDetail> GetTitleAsync(
        RemoteTitleIdentity title,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);
        var route = string.Format(
            CultureInfo.InvariantCulture,
            ComixContract.RouteTitle,
            Uri.EscapeDataString(title.TitleHid));
        var entry = await GetResultAsync(route, string.Empty, cancellationToken).ConfigureAwait(false);
        return ReadTitleDetail(entry, title);
    }

    /// <summary>Decodes the captured title detail payload.</summary>
    internal static RemoteTitleDetail ReadTitleDetail(JsonObject entry, RemoteTitleIdentity title)
    {
        // The caller's identity is kept: a mapping confirmed from the browse
        // result and a job queued from the detail must name the same title.
        var summary = ReadSummary(entry, title);
        var genres = ReadOptions(entry, ComixContract.FieldGenres);

        var metadata = new List<RemoteOption>();
        AddMetadata(metadata, "Type", ReadString(entry, ComixContract.FieldType));
        AddMetadata(metadata, "Status", ReadString(entry, ComixContract.FieldItemStatus));
        AddMetadata(metadata, "Year", ReadString(entry, ComixContract.FieldYear));
        AddMetadata(metadata, "Language", ReadString(entry, ComixContract.FieldOriginalLanguage));
        AddMetadata(metadata, "Content rating", ReadString(entry, "contentRating"));
        AddMetadata(metadata, "Authors", JoinOptions(entry, ComixContract.FieldAuthors));
        AddMetadata(metadata, "Artists", JoinOptions(entry, ComixContract.FieldArtists));

        return new RemoteTitleDetail(
            summary,
            ReadString(entry, ComixContract.FieldSynopsis),
            genres,
            metadata);
    }

    public async Task<IReadOnlyList<RemoteSourceGroup>> GetGroupsAsync(
        RemoteTitleIdentity title,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);
        var chapters = await ReadChaptersAsync(title, cancellationToken).ConfigureAwait(false);
        return ReadGroups(chapters);
    }

    /// <summary>
    /// Groups come from the captured chapter list: one entry per distinct
    /// groupId in first-seen order, so no translation variant is dropped and two
    /// groups that share a chapter number never merge.
    /// </summary>
    internal static IReadOnlyList<RemoteSourceGroup> ReadGroups(IReadOnlyList<JsonObject> chapters)
    {
        var seen = new Dictionary<string, RemoteSourceGroup>(StringComparer.Ordinal);
        foreach (var chapter in chapters)
        {
            var groupId = ReadString(chapter, ComixContract.FieldGroupId);
            if (groupId is null || seen.ContainsKey(groupId)) continue;

            var name = chapter[ComixContract.FieldGroup] is JsonObject group
                ? ReadString(group, ComixContract.FieldGroupName)
                : null;
            seen[groupId] = new RemoteSourceGroup(
                new RemoteGroupIdentity(ComixContract.SourceId, groupId),
                name ?? "Group " + groupId);
        }

        return [.. seen.Values];
    }

    public async Task<IReadOnlyList<RemoteChapterSummary>> GetChaptersAsync(
        RemoteTitleIdentity title,
        RemoteGroupIdentity group,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(group);
        var chapters = await ReadChaptersAsync(title, cancellationToken).ConfigureAwait(false);
        return ReadGroupChapters(chapters, title, group);
    }

    /// <summary>
    /// One group's chapters only, in the provider's own captured order. The
    /// captured name is empty for most chapters, so the number is the label.
    /// </summary>
    internal static IReadOnlyList<RemoteChapterSummary> ReadGroupChapters(
        IReadOnlyList<JsonObject> chapters,
        RemoteTitleIdentity title,
        RemoteGroupIdentity group)
    {
        var results = new List<RemoteChapterSummary>();
        foreach (var chapter in chapters)
        {
            var groupId = ReadString(chapter, ComixContract.FieldGroupId);
            if (!string.Equals(groupId, group.GroupId, StringComparison.Ordinal)) continue;

            var chapterId = ReadString(chapter, ComixContract.FieldChapterId)
                ?? throw Missing(ComixContract.FieldChapterId);
            var number = ReadNumberText(chapter, ComixContract.FieldNumber) ?? string.Empty;
            var identity = new RemoteChapterIdentity(
                ComixContract.SourceId,
                title,
                chapterId,
                number,
                group);

            var name = ReadString(chapter, ComixContract.FieldChapterName)
                ?? (number.Length == 0 ? "Chapter" : "Chapter " + number);
            results.Add(new RemoteChapterSummary(identity, name, results.Count));
        }

        return results;
    }

    /// <summary>
    /// The captured chapter list, paged until the provider's own meta says there
    /// is no next page. The page bound keeps one user action from turning into
    /// unbounded requests.
    /// </summary>
    private async Task<IReadOnlyList<JsonObject>> ReadChaptersAsync(
        RemoteTitleIdentity title,
        CancellationToken cancellationToken)
    {
        var route = string.Format(
            CultureInfo.InvariantCulture,
            ComixContract.RouteChapters,
            Uri.EscapeDataString(title.TitleHid));

        var chapters = new List<JsonObject>();
        for (var page = 1; page <= MaximumChapterPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var query = new StringBuilder();
            AppendKey(
                query,
                string.Format(CultureInfo.InvariantCulture, ComixContract.KeyOrder, ComixContract.OrderNumberColumn),
                ComixContract.OrderDescending);
            AppendKey(query, ComixContract.KeyPage, page.ToString(CultureInfo.InvariantCulture));
            AppendKey(query, ComixContract.KeyLimit, ComixContract.ChapterPageSize.ToString(CultureInfo.InvariantCulture));

            var result = await GetResultAsync(route, query.ToString(), cancellationToken).ConfigureAwait(false);
            if (result[ComixContract.FieldItems] is not JsonArray items)
            {
                throw Missing(ComixContract.FieldItems);
            }

            foreach (var item in items)
            {
                if (item is JsonObject entry) chapters.Add(entry);
            }

            if (!HasNextPage(result)) break;
        }

        return chapters;
    }

    public async Task<RemoteChapterManifest> GetManifestAsync(
        RemoteChapterIdentity chapter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        var route = string.Format(
            CultureInfo.InvariantCulture,
            ComixContract.RouteChapter,
            Uri.EscapeDataString(chapter.ChapterId));

        var entry = await GetResultAsync(route, string.Empty, cancellationToken).ConfigureAwait(false);
        return ReadManifest(entry, chapter);
    }

    /// <summary>
    /// Decodes the captured manifest: <c>pages.items</c> in array order, each
    /// with an absolute url. No scramble descriptor was captured on this
    /// payload, so Transform stays null and fetched bytes are kept as they
    /// arrive.
    /// </summary>
    internal static RemoteChapterManifest ReadManifest(JsonObject entry, RemoteChapterIdentity chapter)
    {
        if (entry[ComixContract.FieldPages] is not JsonObject container
            || container[ComixContract.FieldItems] is not JsonArray items)
        {
            throw Missing(ComixContract.FieldPages);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = ComixContract.ImageReferer,
        };

        var pages = new List<RemotePage>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            // A non-object entry would shift every later ordinal, so it fails
            // instead of being skipped.
            if (items[index] is not JsonObject item)
            {
                throw new ComixContractException(
                    $"Comix manifest page {index} bukan object; kontrak page berubah.");
            }

            var url = ReadString(item, ComixContract.FieldPageUrl);
            if (url is null || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // pages.baseUrl was captured empty and every captured page url is
                // absolute, so a relative url means the contract changed. Joining
                // it here would be a guess.
                throw new ComixContractException(
                    $"Comix manifest page {index} tidak punya URL absolut (baseUrl='{ReadString(container, ComixContract.FieldPagesBaseUrl) ?? string.Empty}').");
            }

            pages.Add(new RemotePage(
                index,
                RemoteKey: index.ToString(CultureInfo.InvariantCulture),
                Url: url,
                ExpectedBytes: null,
                Transform: null));
        }

        if (pages.Count == 0)
        {
            throw new ComixContractException("Comix manifest tidak berisi page sama sekali.");
        }

        return new RemoteChapterManifest(chapter, pages, ManifestHash(chapter, pages), headers);
    }

    /// <summary>
    /// Identity of one resolved manifest, stable across a refresh unless the
    /// chapter or its page order actually changed.
    /// </summary>
    private static string ManifestHash(RemoteChapterIdentity chapter, IReadOnlyList<RemotePage> pages)
    {
        var builder = new StringBuilder();
        builder.Append(ComixContract.Version).Append('|')
            .Append(chapter.SourceId).Append('|')
            .Append(chapter.Title.TitleHid).Append('|')
            .Append(chapter.ChapterId).Append('|')
            .Append(chapter.Group.GroupId).Append('|')
            .Append(pages.Count);
        foreach (var page in pages)
        {
            builder.Append('|').Append(page.Ordinal).Append('=').Append(page.Url);
        }

        return "sha256:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public Task<RemotePageImage> TransformPageAsync(
        RemotePage page,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(payload);

        // Scramble descriptors arrive with the page response and travel inside
        // the manifest page's transform; the queue never interprets them.
        if (page.Transform is not { } transform)
        {
            return Task.FromResult(new RemotePageImage(payload, DetectFormat(payload)));
        }

        if (!string.Equals(transform.Kind, "comix-scramble", StringComparison.Ordinal))
        {
            throw new ComixContractException("Unknown Comix page transform: " + transform.Kind);
        }

        var header = new ComixScrambleHeader(
            ReadParameter(transform, "seed"),
            (int)ReadParameter(transform, "grid"),
            (int)ReadParameter(transform, "algo"),
            transform.Parameters.TryGetValue("hash", out var hash) ? (int)long.Parse(hash, CultureInfo.InvariantCulture) : null);
        if (!ComixPageDecoder.IsSupported(header))
        {
            throw new ComixContractException(
                $"Unsupported Comix scramble variant: algorithm {header.Algorithm}, grid {header.Grid}.");
        }

        var decoded = new ComixPageDecoder().Descramble(payload, header);
        return Task.FromResult(new RemotePageImage(decoded, "png"));
    }

    /// <summary>
    /// Whole-chapter cross-group fallback: search the title's other groups for
    /// the same chapter number. Composed from the same group/chapter calls the
    /// UI uses, so there is no second discovery path.
    /// </summary>
    public async Task<IReadOnlyList<RemoteAlternateChapter>> FindAlternateGroupsAsync(
        RemoteChapterIdentity chapter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var groups = await GetGroupsAsync(chapter.Title, cancellationToken).ConfigureAwait(false);
        var candidates = new List<RemoteAlternateChapter>();
        foreach (var group in groups)
        {
            if (string.Equals(group.Identity.GroupId, chapter.Group.GroupId, StringComparison.Ordinal))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<RemoteChapterSummary> chapters;
            try
            {
                chapters = await GetChaptersAsync(chapter.Title, group.Identity, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is ComixContractException or IOException)
            {
                // One unreachable group must not hide the other candidates.
                continue;
            }

            var match = chapters.FirstOrDefault(candidate => string.Equals(
                candidate.Identity.ChapterNumber,
                chapter.ChapterNumber,
                StringComparison.Ordinal));
            if (match is null) continue;

            candidates.Add(new RemoteAlternateChapter(
                group.Identity,
                group.DisplayName,
                match.Identity.ChapterId,
                $"Chapter {chapter.ChapterNumber} tersedia dari {group.DisplayName}."));
        }

        return candidates;
    }

    /// <summary>
    /// One captured title item, using the live field names: <c>hid</c>,
    /// <c>id</c>, <c>title</c>, <c>poster.medium</c>/<c>poster.large</c>,
    /// <c>latestChapter</c> and <c>url</c>. A missing hid or title is a contract
    /// failure, never an empty card. <paramref name="identity"/> keeps an
    /// identity the caller already established, so a mapping confirmed from the
    /// browse result and a job queued from the detail name the same title.
    /// </summary>
    private static RemoteTitleSummary ReadSummary(
        JsonObject entry,
        RemoteTitleIdentity? identity = null)
    {
        var hid = ReadString(entry, ComixContract.FieldHid) ?? throw Missing(ComixContract.FieldHid);
        var name = ReadString(entry, ComixContract.FieldTitle) ?? throw Missing(ComixContract.FieldTitle);
        var id = ReadLong(entry, ComixContract.FieldId)?.ToString(CultureInfo.InvariantCulture) ?? hid;

        // The captured url is the provider's own title page path,
        // /title/{hid}-{slug}; it is kept as-is rather than re-derived.
        var resolved = identity ?? new RemoteTitleIdentity(
            ComixContract.SourceId,
            id,
            hid,
            ReadString(entry, ComixContract.FieldUrl) ?? string.Empty);

        var poster = entry[ComixContract.FieldPoster] as JsonObject;
        var cover = poster is null
            ? null
            : ReadString(poster, ComixContract.PosterMedium) ?? ReadString(poster, ComixContract.PosterLarge);

        var latest = ReadLong(entry, ComixContract.FieldLatestChapter);
        return new RemoteTitleSummary(
            resolved,
            name,
            cover,
            latest is > 0 ? "Ch. " + latest.Value.ToString(CultureInfo.InvariantCulture) : null);
    }

    /// <summary>The provider's own pagination verdict, never a recomputed guess.</summary>
    private static bool HasNextPage(JsonObject result) =>
        result[ComixContract.FieldMeta] is JsonObject meta
        && meta[ComixContract.MetaHasNext] is JsonValue flag
        && flag.TryGetValue<bool>(out var more)
        && more;

    /// <summary>
    /// Numbers arrive as JSON numbers and a chapter number may carry a decimal
    /// part, so it is read as text without losing "10.5" or inventing a format.
    /// </summary>
    private static string? ReadNumberText(JsonObject entry, string key)
    {
        if (entry[key] is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var integer))
        {
            return integer.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<double>(out var number))
        {
            return number.ToString("0.##", CultureInfo.InvariantCulture);
        }

        return ReadString(entry, key);
    }

    /// <summary>
    /// Reads one captured option list. Genres, demographics, formats, tags,
    /// authors, artists and publishers all arrive as the same
    /// <c>{id, title, slug}</c> triple.
    /// </summary>
    private static IReadOnlyList<RemoteOption> ReadOptions(JsonObject entry, string field)
    {
        var results = new List<RemoteOption>();
        if (entry[field] is not JsonArray array) return results;

        foreach (var item in array)
        {
            if (item is not JsonObject option) continue;
            var key = ReadString(option, ComixContract.OptionId)
                ?? ReadString(option, ComixContract.OptionSlug);
            var name = ReadString(option, ComixContract.OptionTitle);
            if (key is null || name is null) continue;
            results.Add(new RemoteOption(key, name));
        }

        return results;
    }

    private static string? JoinOptions(JsonObject entry, string field)
    {
        var options = ReadOptions(entry, field);
        return options.Count == 0
            ? null
            : string.Join(", ", options.Select(option => option.DisplayName));
    }

    /// <summary>
    /// The detail header renders <c>DisplayName + ": " + Key</c>, so the label
    /// goes in DisplayName and the captured value in Key.
    /// </summary>
    private static void AddMetadata(List<RemoteOption> target, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target.Add(new RemoteOption(value, label));
    }

    /// <summary>
    /// One API call through the browser page context. The plugin returns the
    /// configured Axios client's own <c>response.data</c>, so the root object is
    /// already the provider's payload — there is no envelope left to unwrap here.
    /// </summary>
    private async Task<JsonObject> GetResultAsync(
        string route,
        string queryString,
        CancellationToken cancellationToken)
    {
        await _client.EnsureSessionAsync(
            ComixContract.SourceId,
            ComixContract.BaseUrl + "/browse",
            headless: true,
            cancellationToken).ConfigureAwait(false);

        var url = ComixContract.BaseUrl + route
            + (queryString.Length == 0 ? string.Empty : "?" + queryString);
        var response = await _client.ApiAsync(url, cancellationToken).ConfigureAwait(false);
        return ReadApiResponse(response, route);
    }

    /// <summary>
    /// The boundary contract with the plugin: HTTP status first, then the JSON
    /// root the site's own client produced (<c>{items, meta}</c> for browse).
    /// Reading <c>status</c>/<c>result</c> here would be a second unwrap of a
    /// payload Axios already unwrapped, and it fails as an empty result.
    /// </summary>
    internal static JsonObject ReadApiResponse(JsonObject response, string route)
    {
        var status = response["status"]?.GetValue<int>() ?? 0;
        if (status is 401 or 403)
        {
            throw new ComixContractException(
                $"Comix menolak request dengan HTTP {status}. Request harus lewat client Axios halaman, yang menambah token '_' sendiri.");
        }

        if (status is < 200 or >= 300)
        {
            throw new ComixContractException($"Comix returned HTTP {status} for {route}.");
        }

        if (response["is_json"]?.GetValue<bool>() != true
            || response["json"] is not JsonObject payload)
        {
            throw new ComixContractException(
                $"Comix answered {route} with a non-JSON payload; the secure contract changed.");
        }

        return payload;
    }

    private static void AppendKey(StringBuilder query, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (query.Length > 0) query.Append('&');
        query.Append(Uri.EscapeDataString(key))
            .Append('=')
            .Append(Uri.EscapeDataString(value));
    }

    private static long ReadParameter(RemotePageTransform transform, string key) =>
        transform.Parameters.TryGetValue(key, out var raw)
            ? long.Parse(raw, CultureInfo.InvariantCulture)
            : throw new ComixContractException($"Comix transform is missing '{key}'.");

    private static string? ReadString(JsonObject entry, string key)
    {
        var node = entry[key];
        if (node is null) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            if (value.TryGetValue<long>(out var number))
            {
                return number.ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static long? ReadLong(JsonObject entry, string key)
    {
        var node = entry[key];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var number)) return number;
            if (value.TryGetValue<string>(out var text)
                && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        // A missing count is genuinely unknown and stays null; it is never
        // reported as zero.
        return null;
    }

    private static ComixContractException Missing(string field) =>
        new($"Comix response is missing '{field}'; the provider contract changed.");

    /// <summary>Format detected from bytes, never from a filename or URL.</summary>
    internal static string DetectFormat(byte[] payload)
    {
        if (payload.Length >= 12
            && payload[0] == 0x89 && payload[1] == 0x50 && payload[2] == 0x4E && payload[3] == 0x47)
        {
            return "png";
        }

        if (payload.Length >= 3 && payload[0] == 0xFF && payload[1] == 0xD8 && payload[2] == 0xFF)
        {
            return "jpg";
        }

        if (payload.Length >= 12
            && payload[8] == 0x57 && payload[9] == 0x45 && payload[10] == 0x42 && payload[11] == 0x50)
        {
            return "webp";
        }

        if (payload.Length >= 3 && payload[0] == 0x47 && payload[1] == 0x49 && payload[2] == 0x46)
        {
            return "gif";
        }

        throw new ComixContractException(
            "Page payload is not a recognized image; refusing to publish unknown bytes.");
    }
}
