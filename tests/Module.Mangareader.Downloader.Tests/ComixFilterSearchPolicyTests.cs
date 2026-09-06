using Module.Mangareader.Features.Downloader.FilterSearch.Comix;
using Module.Mangareader.Features.Downloader.Sources.Comix;

namespace Module.Mangareader.Downloader.Tests;

public sealed class ComixFilterSearchPolicyTests
{
    [Fact]
    public void KeywordUsesBestMatchWhenDefaultSortWasUntouched()
    {
        var snapshot = ComixFilterSearchPolicy.Apply(ComixBrowseQuery.Default, "lover boy");

        Assert.Equal(ComixFilterSearchPolicy.BestMatchSortKey, snapshot.SortKey);
        Assert.Contains("order%5Brelevance%5D=desc", snapshot.ToQueryString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitNonDefaultSortIsPreservedDuringSearch()
    {
        var query = ComixBrowseQuery.Default with { SortKey = "title_asc" };

        var snapshot = ComixFilterSearchPolicy.Apply(query, "lover boy");

        Assert.Equal("title_asc", snapshot.SortKey);
    }
}
