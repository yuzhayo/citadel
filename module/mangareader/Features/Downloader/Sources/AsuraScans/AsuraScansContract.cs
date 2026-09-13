using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.AsuraScans;

public static class AsuraScansContract
{
    public const int Version = 1;
    public const string SourceId = "asura-scans";
    public const string DisplayName = "AsuraScans";
    public const string BaseUrl = "https://asurascans.com";
    public const string ApiBaseUrl = "https://api.asurascans.com";
    public const string Host = "asurascans.com";
    public const string GroupId = "asura-scans";
    public const int PageSize = 20;
    public const int MaximumResponseBytes = 8 * 1024 * 1024;
}

public sealed class AsuraScansContractException(string message) : InvalidOperationException(message);

public sealed record AsuraScansBrowseQuery : IRemoteBrowseFilter
{
    public static AsuraScansBrowseQuery Default { get; } = new();

    public string SourceId => AsuraScansContract.SourceId;
}
