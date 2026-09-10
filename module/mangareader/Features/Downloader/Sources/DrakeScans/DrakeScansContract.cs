using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.DrakeScans;

public static class DrakeScansContract
{
    public const int Version = 1;
    public const string SourceId = "drake-scans";
    public const string DisplayName = "Drake Scans";
    public const string BaseUrl = "https://drakecomic.net";
    public const string Host = "drakecomic.net";
    public const string GroupId = "drake-scans";
    public const int PageSize = 24;
    public const int MaximumChapterPages = 1000;
    public const int MaximumResponseBytes = 8 * 1024 * 1024;
}

public sealed class DrakeScansContractException(string message) : InvalidOperationException(message);

public sealed record DrakeScansBrowseQuery : IRemoteBrowseFilter
{
    public static DrakeScansBrowseQuery Default { get; } = new();

    public string SourceId => DrakeScansContract.SourceId;

    public IReadOnlyList<string> Genres { get; init; } = [];

    public IReadOnlyList<string> Statuses { get; init; } = [];
}

public sealed record DrakeScansPage(string RemoteKey, int PageNumber, string Url);

public sealed record DrakeScansChapterPage(
    IReadOnlyList<RemoteChapterSummary> Chapters,
    int CurrentPage,
    int TotalPages);

public static class DrakeScansOptions
{
    public static IReadOnlyList<RemoteOption> Statuses { get; } =
    [
        new("ONGOING", "Ongoing"),
        new("COMPLETED", "Completed"),
        new("HIATUS", "Hiatus"),
        new("CANCELLED", "Cancelled"),
    ];

    public static IReadOnlyList<RemoteOption> Genres { get; } =
    [
        new("action", "Action"), new("adventure", "Adventure"),
        new("apocalypse", "Apocalypse"), new("comedy", "Comedy"),
        new("cultivation", "Cultivation"), new("drama", "Drama"),
        new("ecchi", "Ecchi"), new("fantasy", "Fantasy"),
        new("harem", "Harem"), new("historical", "Historical"),
        new("horror", "Horror"), new("isekai", "Isekai"),
        new("magic", "Magic"), new("martial-arts", "Martial Arts"),
        new("mature", "Mature"), new("monster-girls", "Monster Girls"),
        new("monsters", "Monsters"), new("mystery", "Mystery"),
        new("reincarnation", "Reincarnation"), new("romance", "Romance"),
        new("school-life", "School Life"), new("seinen", "Seinen"),
        new("shounen", "Shounen"), new("supernatural", "Supernatural"),
        new("system", "System"),
    ];
}
