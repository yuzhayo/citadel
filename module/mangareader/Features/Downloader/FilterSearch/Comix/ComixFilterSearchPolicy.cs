using Module.Mangareader.Features.Downloader.Sources.Comix;

namespace Module.Mangareader.Features.Downloader.FilterSearch.Comix;

/// <summary>
/// Provider-specific search behavior observed on the Comix browse screen.
/// A keyword submitted with the untouched default sort uses relevance (the
/// site's "Best match"); an explicitly different sort remains user-owned.
/// </summary>
public static class ComixFilterSearchPolicy
{
    public const string BestMatchSortKey = "best_match";

    public static ComixBrowseQuery Apply(ComixBrowseQuery query, string? keyword)
    {
        ArgumentNullException.ThrowIfNull(query);
        return string.IsNullOrWhiteSpace(keyword) || query.SortKey != ComixOptions.DefaultSortKey
            ? query
            : query with { SortKey = BestMatchSortKey };
    }
}
