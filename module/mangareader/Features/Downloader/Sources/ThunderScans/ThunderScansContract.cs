using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.ThunderScans;

public static class ThunderScansContract
{
    public const int Version = 1;
    public const string SourceId = "thunder-scans";
    public const string DisplayName = "ThunderScans";
    public const string BaseUrl = "https://en-thunderscans.com";
    public const string Host = "en-thunderscans.com";
    public const string GroupId = "thunder-scans";
    public const int PageSize = 24;
    public const int MaximumResponseBytes = 8 * 1024 * 1024;
}

public sealed class ThunderScansContractException(string message) : InvalidOperationException(message);

public sealed record ThunderScansBrowseQuery : IRemoteBrowseFilter
{
    public static ThunderScansBrowseQuery Default { get; } = new();
    public string SourceId => ThunderScansContract.SourceId;
}
