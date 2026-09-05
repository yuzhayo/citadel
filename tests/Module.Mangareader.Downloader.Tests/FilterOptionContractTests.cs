using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Features.Downloader.Sources.Comix;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Every Advanced Filters list is object-backed, so the shared item template
/// renders whatever the option exposes and the accessible name comes from
/// <c>DisplayName</c>. These gates cover the half of the shared-controls row
/// that does not need a rendered screen; UIA exposure itself stays a live gate.
/// </summary>
public sealed class FilterOptionContractTests
{
    public static TheoryData<string, RemoteOption> EveryOption()
    {
        var data = new TheoryData<string, RemoteOption>();
        Add(data, "sort", ComixOptions.Sorts);
        Add(data, "rating", ComixOptions.Ratings);
        Add(data, "type", ComixOptions.Types);
        Add(data, "demographic", ComixOptions.Demographics);
        Add(data, "status", ComixOptions.Statuses);
        return data;
    }

    private static void Add(TheoryData<string, RemoteOption> data, string list, IEnumerable<RemoteOption> options)
    {
        foreach (var option in options) data.Add(list, option);
    }

    [Theory]
    [MemberData(nameof(EveryOption))]
    public void AnOptionRendersItsDisplayNameAndNeverARecordDump(string list, RemoteOption option)
    {
        Assert.False(string.IsNullOrWhiteSpace(option.DisplayName), $"{list}: empty label");
        Assert.False(string.IsNullOrWhiteSpace(option.Key), $"{list}: empty key");

        // A default ContentPresenter falls back to ToString, so the label the
        // user reads is exactly this. The record's own dump would leak the type
        // name and its members into the filter list.
        Assert.Equal(option.DisplayName, option.ToString());
        Assert.DoesNotContain(nameof(RemoteOption), option.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("{", option.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AResolvedLookupOptionRendersItsDisplayNameToo()
    {
        var option = new RemoteLookupOption("genre:action", "Action");

        Assert.Equal("Action", option.ToString());
        Assert.DoesNotContain(nameof(RemoteLookupOption), option.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sort")]
    [InlineData("rating")]
    [InlineData("type")]
    [InlineData("demographic")]
    [InlineData("status")]
    public void OptionKeysAreUniqueWithinTheirList(string list)
    {
        var options = list switch
        {
            "sort" => ComixOptions.Sorts,
            "rating" => ComixOptions.Ratings,
            "type" => ComixOptions.Types,
            "demographic" => ComixOptions.Demographics,
            _ => ComixOptions.Statuses,
        };

        Assert.NotEmpty(options);
        Assert.Equal(options.Count, options.Select(option => option.Key).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Reset selects the default sort and the default ratings by key. A key that
    /// is absent from its table would silently leave the picker empty and the
    /// rating list unselected while the query still claimed the defaults.
    /// </summary>
    [Fact]
    public void ThePanelDefaultsResolveToRealOptions()
    {
        Assert.Contains(ComixOptions.Sorts, option => option.Key == ComixOptions.DefaultSortKey);
        Assert.NotEmpty(ComixOptions.DefaultRatingKeys);

        foreach (var key in ComixOptions.DefaultRatingKeys)
        {
            Assert.Contains(ComixOptions.Ratings, option => option.Key == key);
        }
    }

    /// <summary>
    /// The defaults the panel selects on Reset and the defaults the query carries
    /// before any input must be the same provider-observed state, or the visible
    /// filter list would disagree with the first request.
    /// </summary>
    [Fact]
    public void ThePanelDefaultsMatchTheDefaultQuery()
    {
        var query = ComixBrowseQuery.Default;

        Assert.Equal(ComixOptions.DefaultSortKey, query.SortKey);
        Assert.Equal(ComixOptions.DefaultRatingKeys, query.Ratings);
    }
}
