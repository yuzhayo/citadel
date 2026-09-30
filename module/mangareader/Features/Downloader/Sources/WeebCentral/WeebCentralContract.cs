using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.WeebCentral;

public static class WeebCentralContract
{
    public const int Version = 1;
    public const string SourceId = "weebcentral";
    public const string DisplayName = "WeebCentral";
    public const string BaseUrl = "https://weebcentral.com";
    public const string Host = "weebcentral.com";
    public const string GroupId = "weebcentral";

    /// <summary>Observed page size of the results grid ("View More" steps by this).</summary>
    public const int PageSize = 32;
    public const int MaximumChapterPages = 1000;
    public const int MaximumResponseBytes = 8 * 1024 * 1024;

    /// <summary>The richer grid variant; its cards carry title, year, status, type, author and tags.</summary>
    public const string FullDisplay = "Full Display";

    /// <summary>
    /// Covers are deterministic per title id: the grid and detail pages both emit
    /// <c>cover/normal/{id}.webp</c> with a <c>cover/fallback/{id}.jpg</c> fallback.
    /// The poster host is a separate CDN, so it is named here rather than derived.
    /// </summary>
    private const string CoverBase = "https://temp.compsci88.com/cover";

    internal static string CoverUrl(string titleId) => CoverBase + "/normal/" + titleId + ".webp";

    internal static IReadOnlyList<string> CoverFallbacks(string titleId) =>
    [
        CoverBase + "/fallback/" + titleId + ".jpg",
    ];
}

public sealed class WeebCentralContractException(string message) : InvalidOperationException(message);

/// <summary>
/// Provider-owned browse filter, carried opaquely by Catalog. Every value is one
/// of the exact tokens the site's Advanced Search form submits; See
/// <see cref="WeebCentralOptions"/> for the captured vocabularies.
/// </summary>
public sealed record WeebCentralBrowseQuery : IRemoteBrowseFilter
{
    public static WeebCentralBrowseQuery Default { get; } = new();

    public string SourceId => WeebCentralContract.SourceId;

    public string SortKey { get; init; } = WeebCentralOptionKeys.DefaultSort;

    public string Order { get; init; } = WeebCentralOptionKeys.Descending;

    public IReadOnlyList<string> Statuses { get; init; } = [];

    public IReadOnlyList<string> Types { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Any/True/False. Defaults keep adult content as a normal category.</summary>
    public string Official { get; init; } = WeebCentralOptionKeys.Any;

    public string Anime { get; init; } = WeebCentralOptionKeys.Any;

    public string Adult { get; init; } = WeebCentralOptionKeys.Any;
}

public static class WeebCentralOptionKeys
{
    /// <summary>Browse default. Relevance ("Best Match") is only meaningful with a keyword.</summary>
    public const string DefaultSort = "Popularity";
    public const string Relevance = "Best Match";
    public const string Descending = "Descending";
    public const string Any = "Any";
}

/// <summary>Values captured from Weeb Central's live Advanced Search form.</summary>
public static class WeebCentralOptions
{
    public static IReadOnlyList<RemoteOption> Sorts { get; } =
    [
        new("Best Match", "Best match"),
        new("Alphabet", "Title (A-Z)"),
        new("Popularity", "Popularity"),
        new("Subscribers", "Subscribers"),
        new("Recently Added", "Recently added"),
        new("Latest Updates", "Latest updates"),
    ];

    public static IReadOnlyList<RemoteOption> Orders { get; } =
    [
        new("Descending", "Descending"),
        new("Ascending", "Ascending"),
    ];

    public static IReadOnlyList<RemoteOption> Statuses { get; } =
    [
        new("Ongoing", "Ongoing"),
        new("Complete", "Complete"),
        new("Hiatus", "Hiatus"),
        new("Canceled", "Canceled"),
    ];

    public static IReadOnlyList<RemoteOption> Types { get; } =
    [
        new("Manga", "Manga"),
        new("Manhwa", "Manhwa"),
        new("Manhua", "Manhua"),
        new("OEL", "OEL"),
    ];

    /// <summary>Any/True/False selectors shared by official, anime and adult.</summary>
    public static IReadOnlyList<RemoteOption> TriState { get; } =
    [
        new("Any", "Any"),
        new("True", "Yes"),
        new("False", "No"),
    ];

    public static IReadOnlyList<RemoteOption> Tags { get; } =
    [
        new("Action", "Action"), new("Adult", "Adult"), new("Adventure", "Adventure"),
        new("Comedy", "Comedy"), new("Doujinshi", "Doujinshi"), new("Drama", "Drama"),
        new("Ecchi", "Ecchi"), new("Fantasy", "Fantasy"), new("Gender Bender", "Gender Bender"),
        new("Harem", "Harem"), new("Hentai", "Hentai"), new("Historical", "Historical"),
        new("Horror", "Horror"), new("Isekai", "Isekai"), new("Josei", "Josei"),
        new("Lolicon", "Lolicon"), new("Martial Arts", "Martial Arts"), new("Mature", "Mature"),
        new("Mecha", "Mecha"), new("Mystery", "Mystery"), new("Psychological", "Psychological"),
        new("Romance", "Romance"), new("School Life", "School Life"), new("Sci-fi", "Sci-fi"),
        new("Seinen", "Seinen"), new("Shotacon", "Shotacon"), new("Shoujo", "Shoujo"),
        new("Shoujo Ai", "Shoujo Ai"), new("Shounen", "Shounen"), new("Shounen Ai", "Shounen Ai"),
        new("Slice of Life", "Slice of Life"), new("Smut", "Smut"), new("Sports", "Sports"),
        new("Supernatural", "Supernatural"), new("Tragedy", "Tragedy"), new("Yaoi", "Yaoi"),
        new("Yuri", "Yuri"), new("Other", "Other"),
    ];
}
