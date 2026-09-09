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
    public void KeywordAlwaysUsesBestMatchWhileFiltersStayIntact()
    {
        var query = ComixBrowseQuery.Default with
        {
            SortKey = "title_asc",
            Types = ["manga"],
        };

        var snapshot = ComixFilterSearchPolicy.Apply(query, "lover boy");

        Assert.Equal(ComixFilterSearchPolicy.BestMatchSortKey, snapshot.SortKey);
        Assert.Equal(["manga"], snapshot.Types);
    }
}
