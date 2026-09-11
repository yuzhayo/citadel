using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using CitadelBridge;
using Module.Mangareader.Features.Catalog.Runtime;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Catalog.Sources.Comix;
public static class ComixOptions
{
    public const string DefaultSortKey = "latest";

    /// <summary>The thirteen captured sorts and their exact order fields.</summary>
    public static readonly IReadOnlyList<ComixSortOption> SortOptions =
    [
        new("best_match", "Best match", "relevance", ComixContract.OrderDescending),
        new(DefaultSortKey, "Latest update", ComixContract.OrderLatestColumn, ComixContract.OrderDescending),
        new("recently_added", "Recently added", "created_at", ComixContract.OrderDescending),
        new("title_asc", "Title (A-Z)", "title", ComixContract.OrderAscending),
        new("title_desc", "Title (Z-A)", "title", ComixContract.OrderDescending),
        new("year_desc", "Year (newest)", "year", ComixContract.OrderDescending),
        new("year_asc", "Year (oldest)", "year", ComixContract.OrderAscending),
        new("score", "Highest rated", "score", ComixContract.OrderDescending),
        new("views_7d", "Most viewed 7 days", "views_7d", ComixContract.OrderDescending),
        new("views_30d", "Most viewed 30 days", "views_30d", ComixContract.OrderDescending),
        new("views_90d", "Most viewed 90 days", "views_90d", ComixContract.OrderDescending),
        new("views_total", "Most viewed all time", "views_total", ComixContract.OrderDescending),
        new("follows_total", "Most followed", "follows_total", ComixContract.OrderDescending),
    ];

    /// <summary>
    /// The picker-facing projection of the same captured table, so the order
    /// field a sort serializes can never drift from the label a user picks.
    /// </summary>
    public static readonly IReadOnlyList<RemoteOption> Sorts =
        SortOptions
            .Select(option => new RemoteOption(option.Key, option.DisplayName))
            .ToArray();

    public static ComixSortOption? FindSort(string? key) =>
        SortOptions.FirstOrDefault(option =>
            string.Equals(option.Key, key, StringComparison.Ordinal));

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

    /// <summary>
    /// Captured live on 2026-09-06 from the taxonomy the browse page renders into
    /// its own document (<c>https://comix.ws/browse</c>), not inferred from the
    /// display labels. Demographic ids are integers, and they are not in label
    /// order: Josei is 3, Seinen is 4, Shoujo is 1, Shounen is 2. The lowercase
    /// slugs an earlier revision sent were a guess and the provider does not use
    /// them as <c>demographics[]</c> values.
    /// </summary>
    public static readonly IReadOnlyList<RemoteOption> Demographics =
    [
        new("3", "Josei"),
        new("4", "Seinen"),
        new("1", "Shoujo"),
        new("2", "Shounen"),
    ];

    public static readonly IReadOnlyList<RemoteOption> Statuses =
    [
        new("releasing", "Releasing"),
        new("finished", "Finished"),
        new("on_hiatus", "On hiatus"),
        new("discontinued", "Discontinued"),
        new("not_yet_released", "Not yet released"),
    ];

    /// <summary>
    /// The 31 genres captured live on 2026-09-06 from the same rendered taxonomy.
    /// <c>Adult</c>, <c>Ecchi</c>, <c>Hentai</c>, <c>Mature</c> and <c>Smut</c> are
    /// genres here, which is why none of them may be invented as a separate
    /// "uncensored" filter.
    /// </summary>
    public static readonly IReadOnlyList<RemoteOption> Genres =
    [
        new("6", "Action"),
        new("87264", "Adult"),
        new("7", "Adventure"),
        new("8", "Boys Love"),
        new("9", "Comedy"),
        new("10", "Crime"),
        new("11", "Drama"),
        new("87265", "Ecchi"),
        new("12", "Fantasy"),
        new("13", "Girls Love"),
        new("40", "Harem"),
        new("87266", "Hentai"),
        new("14", "Historical"),
        new("15", "Horror"),
        new("16", "Isekai"),
        new("17", "Magical Girls"),
        new("87267", "Mature"),
        new("18", "Mecha"),
        new("19", "Medical"),
        new("20", "Mystery"),
        new("21", "Philosophical"),
        new("22", "Psychological"),
        new("23", "Romance"),
        new("24", "Sci-Fi"),
        new("25", "Slice of Life"),
        new("87268", "Smut"),
        new("26", "Sports"),
        new("27", "Superhero"),
        new("28", "Thriller"),
        new("29", "Tragedy"),
        new("30", "Wuxia"),
    ];

    /// <summary>
    /// The 9 formats captured live on 2026-09-06 from the same rendered taxonomy.
    /// Formats share the <c>genres_in[]</c> key with genres, so one dropdown offers
    /// both and the provider distinguishes them by id alone.
    /// </summary>
    public static readonly IReadOnlyList<RemoteOption> Formats =
    [
        new("93164", "4-Koma"),
        new("93167", "Adaptation"),
        new("93165", "Anthology"),
        new("93166", "Award Winning"),
        new("93168", "Doujinshi"),
        new("93172", "Full Color"),
        new("93170", "Long Strip"),
        new("93169", "Oneshot"),
        new("93171", "Web Comic"),
    ];
}

public static class ComixContract
{
    /// <summary>
    /// Feeds the chapter manifest hash, so it identifies the page and manifest
    /// contract and deliberately does not track the browse query contract above.
    /// Raising it for a new query key would invalidate staged resume data and the
    /// provenance embedded in already published archives while changing no
    /// behavior, so it moves only when the manifest or page contract moves.
    /// </summary>
    public const int Version = 2;

    public const string SourceId = "comix";
    public const string DisplayName = "Comix";

    /// <summary>The host that answers. Captured live: 200 in ~0.4 s.</summary>
    public const string BaseUrl = "https://comix.ws";

    /// <summary>Referer required by image downloads.</summary>
    public const string ImageReferer = "https://comix.ws/";

    /// <summary>
    /// Captured: the shape of the title page path the provider reports in
    /// <c>url</c>, <c>/title/{hid}-{slug}</c>. Only this shape is resolved against
    /// <see cref="BaseUrl"/>, so a tampered payload cannot aim a canonical url at
    /// another origin through a protocol-relative or unrelated relative value.
    /// </summary>
    public const string TitlePagePathPrefix = "/title/";

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
    /// (<c>{items, meta}</c>). The raw network body is encrypted (<c>{"e":â€¦}</c>);
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

    /// <summary>
    /// Browse query keys captured live on 2026-09-06 through the page client.
    /// Every one of these is provider evidence; a filter with no key here is
    /// refused visibly rather than sent under an invented name.
    /// </summary>
    public const string KeyStatuses = "statuses[]";
    public const string KeyDemographics = "demographics[]";
    public const string KeyGenresIn = "genres_in[]";
    public const string KeyGenresMode = "genres_mode";
    public const string KeyMinimumChapter = "min_chap";
    public const string KeyYearFrom = "year_from";
    public const string KeyYearTo = "year_to";
    public const string KeyAuthors = "authors[]";
    public const string KeyArtists = "artists[]";

    /// <summary>The two captured genre-matching modes.</summary>
    public const string GenresModeAnd = "and";
    public const string GenresModeOr = "or";

    /// <summary>
    /// Captured author/artist lookup route and its query keys. Only the author
    /// and artist tag types were captured; a genre or format lookup has no
    /// recorded endpoint and is refused instead of guessed.
    /// </summary>
    public const string RouteTagsSearch = "/api/v1/tags/search";
    public const string KeyTagType = "type";
    public const string KeyQuery = "q";
    public const string TagTypeAuthor = "author";
    public const string TagTypeArtist = "artist";
    public const int LookupLimit = 20;

    /// <summary>The captured order column for "latest update".</summary>
    public const string OrderLatestColumn = "chapter_updated_at";
    public const string OrderDescending = "desc";
    public const string OrderAscending = "asc";

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
    public const string FieldChapterUpdatedAtFormatted = "chapterUpdatedAtFormatted";
    public const string FieldUpdatedAtFormatted = "updatedAtFormatted";
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

    public ComixGenreMode GenreMode { get; init; } = ComixGenreMode.And;

    public IReadOnlyList<string> Demographics { get; init; } = [];

    public IReadOnlyList<string> Statuses { get; init; } = [];

    public int? MinimumChapter { get; init; }

    public int? YearFrom { get; init; }

    public int? YearTo { get; init; }

    public string? AuthorKey { get; init; }

    public string? ArtistKey { get; init; }

    /// <summary>
    /// Local validation only. An invalid range blocks Start; it never reaches the
    /// provider and never produces a silent default. An unrecognized sort is
    /// refused for the same reason: its order column would have to be guessed.
    ///
    /// Every filter value this query can carry was captured live, so there is no
    /// longer an "uncaptured" class to refuse. A new filter must arrive with its
    /// own captured evidence before it is added here.
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

            if (ComixOptions.FindSort(SortKey) is null)
            {
                return $"Sort '{SortKey}' tidak ada di tabel sort yang ditangkap dari Comix.";
            }

            return null;
        }
    }

    /// <summary>
    /// The captured browse wire form: one <c>order[&lt;captured field&gt;]=asc|desc</c>
    /// for the chosen sort, then one repeated key per multi-value filter, the
    /// numeric ranges, and the resolved author/artist ids. Genres and formats
    /// share <c>genres_in[]</c>, and <c>genres_mode</c> is only sent when at least
    /// one of them is chosen. Page, limit and keyword are request-level and added
    /// by the adapter.
    /// </summary>
    public string ToQueryString()
    {
        var sort = ComixOptions.FindSort(SortKey);
        var query = new StringBuilder();
        Append(
            query,
            string.Format(
                CultureInfo.InvariantCulture,
                ComixContract.KeyOrder,
                sort?.OrderField ?? ComixContract.OrderLatestColumn),
            sort?.Direction ?? ComixContract.OrderDescending);

        AppendMany(query, ComixContract.KeyContentRating, Ratings);
        AppendMany(query, ComixContract.KeyTypes, Types);
        AppendMany(query, ComixContract.KeyStatuses, Statuses);
        AppendMany(query, ComixContract.KeyDemographics, Demographics);
        AppendMany(query, ComixContract.KeyGenresIn, Genres);
        if (Genres.Count > 0)
        {
            Append(
                query,
                ComixContract.KeyGenresMode,
                GenreMode == ComixGenreMode.Or
                    ? ComixContract.GenresModeOr
                    : ComixContract.GenresModeAnd);
        }

        Append(query, ComixContract.KeyMinimumChapter, MinimumChapter?.ToString(CultureInfo.InvariantCulture));
        Append(query, ComixContract.KeyYearFrom, YearFrom?.ToString(CultureInfo.InvariantCulture));
        Append(query, ComixContract.KeyYearTo, YearTo?.ToString(CultureInfo.InvariantCulture));
        Append(query, ComixContract.KeyAuthors, AuthorKey);
        Append(query, ComixContract.KeyArtists, ArtistKey);
        return query.ToString();
    }

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
/// The Comix adapter: routes, page-context calls, parsing and normalization.
/// No WPF, no queue, no archive writing.
/// </summary>
public sealed class CatalogComixSource(CatalogBrowserClient client)
    : IMangaSource, ICatalogSnapshotSource
{
    /// <summary>
    /// Bound on one chapter listing: 100 captured pages of 20 is 2000 chapters,
    /// so a provider that keeps reporting <c>hasNext</c> cannot turn one Details
    /// click into unbounded requests.
    /// </summary>
    private const int MaximumChapterPages = 100;

    private readonly CatalogBrowserClient _client = client;

    public string Id => ComixContract.SourceId;

    public string DisplayName => ComixContract.DisplayName;

    string ICatalogSnapshotSource.SourceId => ComixContract.SourceId;

    /// <summary>
    /// Only the lookups this adapter can actually answer. Genre and format are not
    /// here: Comix has no captured endpoint for either, and the filter panel serves
    /// them from the captured static taxonomy instead. Advertising a kind that could
    /// only refuse would offer a control that cannot work.
    /// </summary>
    public MangaSourceCapabilities Capabilities { get; } = new(
        SupportsSearch: true,
        SupportsAdvancedFilters: true,
        LookupKinds:
        [
            RemoteLookupKind.Author,
            RemoteLookupKind.Artist,
        ],
        TransformsPages: true);

    /// <summary>
    /// The captured sort for snapshot traversal: title ascending, so every
    /// record's rating is known from its request partition.
    /// </summary>
    private const string SnapshotSortKey = "title_asc";

    /// <summary>
    /// The four ordered rating partitions, keyed by the captured rating
    /// values. No other filter rides on a sync request.
    /// </summary>
    public IReadOnlyList<CatalogSnapshotPartition> SnapshotPartitions { get; } =
        ComixOptions.Ratings
            .Select(option => new CatalogSnapshotPartition(option.Key, option.Key))
            .ToArray();

    /// <summary>
    /// One snapshot page through the same browser page-context path as
    /// <see cref="BrowseAsync"/>, plus exactly one same-page retry when the
    /// session expired (typed 401/403). Any other failure propagates with the
    /// checkpoint unadvanced, so the sync can resume.
    /// </summary>
    public Task<CatalogSnapshotPage> GetSnapshotPageAsync(
        CatalogSnapshotPartition partition,
        int page,
        CancellationToken cancellationToken,
        bool latestFirst = false) =>
        WithSnapshotSessionRetryAsync(
            token => FetchSnapshotPageAsync(partition, page, token, latestFirst),
            token => _client.ReleaseSessionAsync(token),
            cancellationToken);

    /// <summary>
    /// The snapshot-only session-expiry policy: release the existing browser
    /// session and retry the exact page once, and only for typed 401/403.
    /// Contract errors, malformed payloads, timeouts and anything else never
    /// retry here. Public and delegate-driven so fixtures can prove the exact
    /// policy without a browser.
    /// </summary>
    public static async Task<T> WithSnapshotSessionRetryAsync<T>(
        Func<CancellationToken, Task<T>> fetch,
        Func<CancellationToken, Task> releaseSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(releaseSession);

        try
        {
            return await fetch(cancellationToken).ConfigureAwait(false);
        }
        catch (ComixContractException exception) when (exception.HttpStatus is 401 or 403)
        {
            await releaseSession(cancellationToken).ConfigureAwait(false);
            return await fetch(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<CatalogSnapshotPage> FetchSnapshotPageAsync(
        CatalogSnapshotPartition partition,
        int page,
        CancellationToken cancellationToken,
        bool latestFirst)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "page is one-based");
        }

        if (ComixOptions.Ratings.All(option =>
            !string.Equals(option.Key, partition.Key, StringComparison.Ordinal)))
        {
            throw new ComixContractException($"Snapshot partition '{partition.Key}' is not offered.");
        }

        // Backfill walks title order so every record's rating is known from its
        // partition; refresh walks latest-update order for overlap detection.
        // Both are captured sorts — nothing is invented here.
        var sortKey = latestFirst ? ComixOptions.DefaultSortKey : SnapshotSortKey;
        if (ComixOptions.FindSort(sortKey) is null)
        {
            throw new ComixContractException("Snapshot sort is not a captured sort.");
        }

        var filter = new ComixBrowseQuery { SortKey = sortKey, Ratings = [partition.Key] };
        var query = new StringBuilder(filter.ToQueryString());
        AppendKey(query, ComixContract.KeyPage, page.ToString(CultureInfo.InvariantCulture));
        AppendKey(query, ComixContract.KeyLimit, ComixContract.PageSize.ToString(CultureInfo.InvariantCulture));

        var capturedAt = DateTimeOffset.UtcNow;
        var result = await GetResultAsync(ComixContract.RouteBrowse, query.ToString(), cancellationToken)
            .ConfigureAwait(false);
        return ReadSnapshotPage(result, page, partition.Rating, capturedAt);
    }

    /// <summary>
    /// Decodes one captured browse payload into a snapshot page. A non-object
    /// item fails the page: unlike the online parser (which predates the
    /// mirror and stays untouched), the snapshot contract refuses to silently
    /// skip payload it cannot identify.
    /// </summary>
    public static CatalogSnapshotPage ReadSnapshotPage(
        JsonObject result,
        int page,
        string rating,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(rating);

        var items = result[ComixContract.FieldItems] as JsonArray ?? throw Missing(ComixContract.FieldItems);

        var snapshot = new List<CatalogSnapshotItem>(items.Count);
        foreach (var item in items)
        {
            if (item is not JsonObject entry)
            {
                throw new ComixContractException(
                    "Comix snapshot item is not an object; the provider contract changed.");
            }

            snapshot.Add(ReadSnapshotItem(entry, rating, capturedAtUtc));
        }

        var meta = result[ComixContract.FieldMeta] as JsonObject;
        var total = meta is null ? null : ReadLong(meta, ComixContract.MetaTotal);
        return new CatalogSnapshotPage(snapshot, total, page, HasMore: HasNextPage(result));
    }

    /// <summary>
    /// Decodes one captured browse entry into a snapshot item, reusing the
    /// captured keys and read helpers — no new provider field is invented.
    /// Identity rules match the online parser: a missing identity fails the
    /// page. A missing display title with a valid identity becomes the
    /// deterministic untitled placeholder instead (plan 5.2 revision).
    /// </summary>
    public static CatalogSnapshotItem ReadSnapshotItem(
        JsonObject entry,
        string rating,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(rating);

        var hid = ReadString(entry, ComixContract.FieldHid) ?? throw Missing(ComixContract.FieldHid);
        var id = ReadLong(entry, ComixContract.FieldId)?.ToString(CultureInfo.InvariantCulture) ?? hid;
        var rawTitle = ReadString(entry, ComixContract.FieldTitle);
        var title = rawTitle ?? CatalogSnapshotTitles.PlaceholderFor(hid);

        var url = CanonicalTitleUrl(ReadString(entry, ComixContract.FieldUrl));
        if (url.Length == 0)
        {
            var slug = ReadString(entry, ComixContract.OptionSlug);
            url = slug is null
                ? string.Empty
                : CanonicalTitleUrl(
                    ComixContract.TitlePagePathPrefix + hid + "-" + slug);
        }

        var alternates = new List<string>();
        if (entry[ComixContract.FieldAltTitles] is JsonArray titles)
        {
            foreach (var candidate in titles)
            {
                if (candidate is JsonValue value
                    && value.TryGetValue<string>(out var text)
                    && !string.IsNullOrWhiteSpace(text))
                {
                    alternates.Add(text);
                }
            }
        }

        var poster = entry[ComixContract.FieldPoster] as JsonObject;
        var cover = poster is null
            ? null
            : ReadString(poster, ComixContract.PosterLarge) ?? ReadString(poster, ComixContract.PosterMedium);

        var latest = ReadLong(entry, ComixContract.FieldLatestChapter);
        long? year = ReadLong(entry, ComixContract.FieldYear);
        var updated = ParseRelativeUpdatedAt(
            ReadString(entry, ComixContract.FieldChapterUpdatedAtFormatted)
                ?? ReadString(entry, ComixContract.FieldUpdatedAtFormatted),
            capturedAtUtc);
        return new CatalogSnapshotItem(
            ComixContract.SourceId,
            id,
            hid,
            url,
            title,
            alternates,
            cover,
            latest,
            latest is > 0 ? "Ch. " + latest.Value.ToString(CultureInfo.InvariantCulture) : null,
            rating,
            ReadString(entry, ComixContract.FieldType),
            ReadString(entry, ComixContract.FieldItemStatus),
            ReadString(entry, ComixContract.FieldOriginalLanguage),
            year is >= int.MinValue and <= int.MaxValue ? (int?)year : null,
            ReadString(entry, ComixContract.FieldSynopsis),
            capturedAtUtc,
            rawTitle is null,
            updated);
    }

    /// <summary>
    /// Converts the provider's captured relative update label into a sortable
    /// instant. The source exposes no absolute update timestamp in browse data,
    /// so second/minute/hour/day/week/month/year labels retain their stated
    /// precision instead of inventing finer precision.
    /// </summary>
    public static DateTimeOffset? ParseRelativeUpdatedAt(
        string? value,
        DateTimeOffset capturedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (string.Equals(value.Trim(), "just now", StringComparison.OrdinalIgnoreCase))
        {
            return capturedAtUtc;
        }

        var match = Regex.Match(
            value.Trim(),
            "^(?<amount>[0-9]+)(?<unit>mos|mo|s|m|h|d|w|y) ago$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success
            || !int.TryParse(match.Groups["amount"].Value, CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        return match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "s" => capturedAtUtc.AddSeconds(-amount),
            "m" => capturedAtUtc.AddMinutes(-amount),
            "h" => capturedAtUtc.AddHours(-amount),
            "d" => capturedAtUtc.AddDays(-amount),
            "w" => capturedAtUtc.AddDays(-7 * amount),
            "mo" or "mos" => capturedAtUtc.AddMonths(-amount),
            "y" => capturedAtUtc.AddYears(-amount),
            _ => null,
        };
    }

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

    /// <summary>
    /// One captured tag lookup. Author and artist use the recorded
    /// <c>tags/search</c> route with <c>type=author|artist</c>. Genre and format
    /// have no captured endpoint, so they refuse visibly before any browser
    /// session is started rather than being sent to a guessed route.
    /// </summary>
    public async Task<IReadOnlyList<RemoteLookupOption>> LookupAsync(
        RemoteLookupKind kind,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var tagType = kind switch
        {
            RemoteLookupKind.Author => ComixContract.TagTypeAuthor,
            RemoteLookupKind.Artist => ComixContract.TagTypeArtist,
            _ => throw new ComixContractException(
                $"Lookup '{kind}' belum punya endpoint live yang ditangkap dari web Comix."),
        };

        var queryString = new StringBuilder();
        AppendKey(queryString, ComixContract.KeyTagType, tagType);
        AppendKey(queryString, ComixContract.KeyQuery, query.Trim());
        AppendKey(
            queryString,
            ComixContract.KeyLimit,
            ComixContract.LookupLimit.ToString(CultureInfo.InvariantCulture));

        var result = await GetResultAsync(
            ComixContract.RouteTagsSearch,
            queryString.ToString(),
            cancellationToken).ConfigureAwait(false);
        return ReadLookupOptions(result);
    }

    /// <summary>
    /// Decodes the captured option triple. Every Comix option list entry carries
    /// the same <c>{id, title, slug}</c> shape, and the id is what may enter a
    /// browse query — a translated display label never does.
    /// </summary>
    internal static IReadOnlyList<RemoteLookupOption> ReadLookupOptions(JsonObject result)
    {
        if (result[ComixContract.FieldItems] is not JsonArray items)
        {
            throw Missing(ComixContract.FieldItems);
        }

        var options = new List<RemoteLookupOption>();
        foreach (var item in items)
        {
            if (item is not JsonObject entry) continue;
            var key = ReadString(entry, ComixContract.OptionId)
                ?? ReadString(entry, ComixContract.OptionSlug);
            var name = ReadString(entry, ComixContract.OptionTitle);
            if (key is null || name is null) continue;
            options.Add(new RemoteLookupOption(key, name));
        }

        return options;
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
        if (!CatalogComixPageDecoder.IsSupported(header))
        {
            throw new ComixContractException(
                $"Unsupported Comix scramble variant: algorithm {header.Algorithm}, grid {header.Grid}.");
        }

        var decoded = new CatalogComixPageDecoder().Descramble(payload, header);
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
        // /title/{hid}-{slug}. It leaves this adapter already canonical, because
        // only the adapter knows the origin it belongs to.
        var resolved = identity ?? new RemoteTitleIdentity(
            ComixContract.SourceId,
            id,
            hid,
            CanonicalTitleUrl(ReadString(entry, ComixContract.FieldUrl)));

        var poster = entry[ComixContract.FieldPoster] as JsonObject;
        var cover = poster is null
            ? null
            : ReadString(poster, ComixContract.PosterLarge) ?? ReadString(poster, ComixContract.PosterMedium);

        var latest = ReadLong(entry, ComixContract.FieldLatestChapter);
        return new RemoteTitleSummary(
            resolved,
            name,
            cover,
            latest is > 0 ? "Ch. " + latest.Value.ToString(CultureInfo.InvariantCulture) : null);
    }

    /// <summary>The origin every canonical title url must belong to, parsed once.</summary>
    private static readonly Uri ComixOrigin = new(ComixContract.BaseUrl);

    /// <summary>
    /// The provider's own title URL, made absolute. Comix reports <c>url</c> as the
    /// page path <c>/title/{hid}-{slug}</c>, and the origin it belongs to is this
    /// adapter's <see cref="ComixContract.BaseUrl"/> — resolving it anywhere else
    /// would mean a consumer guessing a provider route.
    ///
    /// A relative value is resolved only when it has the captured title page shape;
    /// the prefix check also rejects the protocol-relative <c>//host/…</c> form, which
    /// is the one relative value <see cref="Uri"/> would otherwise turn into a
    /// different host. An already absolute value is not trusted merely for being well
    /// formed either — see <see cref="ComixTitleUrl"/>. Anything unusable stays empty,
    /// so the consumer's existing fallback shows instead of a broken link. Pure string
    /// normalization: no request, no invented route.
    /// </summary>
    internal static string CanonicalTitleUrl(string? providerUrl)
    {
        if (string.IsNullOrWhiteSpace(providerUrl)) return string.Empty;

        if (!Uri.TryCreate(providerUrl, UriKind.Absolute, out var absolute))
        {
            return providerUrl.StartsWith(ComixContract.TitlePagePathPrefix, StringComparison.Ordinal)
                && Uri.TryCreate(ComixOrigin, providerUrl, out var resolved)
                    ? ComixTitleUrl(resolved)
                    : string.Empty;
        }

        return ComixTitleUrl(absolute);
    }

    /// <summary>
    /// One origin and one path shape. This field is provenance for a Comix title, so a
    /// well-formed url from anywhere else is refused rather than stored: only http(s),
    /// this adapter's own authority — port included, so a look-alike on another port
    /// does not pass — and the captured title page path are accepted.
    /// </summary>
    private static string ComixTitleUrl(Uri url) =>
        url.Scheme is "http" or "https"
        && url.Authority.Equals(ComixOrigin.Authority, StringComparison.OrdinalIgnoreCase)
        && url.AbsolutePath.StartsWith(ComixContract.TitlePagePathPrefix, StringComparison.Ordinal)
            ? url.AbsoluteUri
            : string.Empty;

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
        await EnsureBrowserSessionAsync(cancellationToken).ConfigureAwait(false);

        var url = ComixContract.BaseUrl + route
            + (queryString.Length == 0 ? string.Empty : "?" + queryString);
        var response = await _client.ApiAsync(url, cancellationToken).ConfigureAwait(false);
        return ReadApiResponse(response, route);
    }

    private async Task EnsureBrowserSessionAsync(CancellationToken cancellationToken)
    {
        await _client.EnsureSessionAsync(
            ComixContract.SourceId,
            ComixContract.BaseUrl + "/browse",
            headless: !_client.ShowBrowser,
            cancellationToken).ConfigureAwait(false);
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
                $"Comix menolak request dengan HTTP {status}. Request harus lewat client Axios halaman, yang menambah token '_' sendiri.")
            {
                HttpStatus = status,
            };
        }

        if (status is < 200 or >= 300)
        {
            throw new ComixContractException($"Comix returned HTTP {status} for {route}.")
            {
                HttpStatus = status,
            };
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
