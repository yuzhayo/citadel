using Module.Mangareader.Features.Downloader.Sources.Comix;

namespace Module.Mangareader.Features.Downloader.FilterSearch.Comix;

/// <summary>
/// Provider-specific search behavior observed on the Comix browse screen.
/// Every keyword search uses the provider's observed relevance order while
/// preserving the selected filter values. Without a keyword the selected sort
/// remains user-owned.
/// </summary>
public static class ComixFilterSearchPolicy
{
    public const string BestMatchSortKey = "best_match";

    public static ComixBrowseQuery Apply(ComixBrowseQuery query, string? keyword)
    {
        ArgumentNullException.ThrowIfNull(query);
        return string.IsNullOrWhiteSpace(keyword)
            ? query
            : query with { SortKey = BestMatchSortKey };
    }
}
