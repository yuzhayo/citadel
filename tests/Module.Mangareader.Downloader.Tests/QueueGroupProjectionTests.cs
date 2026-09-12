using Module.Mangareader.Features.Downloader.Queue;

namespace Module.Mangareader.Downloader.Tests;

public sealed class QueueGroupProjectionTests
{
    [Fact]
    public void CollapsedGroupSelectsHiddenChildrenAndProgressKeepsRowIdentity()
    {
        var first = Job("a");
        var second = Job("b");
        var view = new QueueGroupProjection();
        view.Update([first, second]);
        var header = view.Visible[0];
        var child = view.Visible[1];
        view.Select(child, true);
        Assert.Null(header.Selected);
        view.Toggle(header);
        Assert.Single(view.Visible);
        var selectionSignals = 0;
        view.SelectionChanged += () => selectionSignals++;
        header.Selected = true; // same two-way contract as keyboard/UI Automation
        Assert.Equal(1, selectionSignals);
        Assert.Equal(2, view.SelectedIds.Count);
        view.Update([first with { CompletedPages = 3, PageCount = 8 }, second]);
        Assert.Same(header, Assert.Single(view.Visible));
        Assert.Equal("3/8", header.ProgressText);
        view.Toggle(header);
        Assert.Same(child, view.Visible[1]);
        Assert.All(view.Visible, row => Assert.True(row.Selected));
    }

    [Fact]
    public void EqualDisplayNamesFromDifferentSourcesDoNotMerge()
    {
        var view = new QueueGroupProjection();
        view.Update([Job("a"), Job("b") with { Identity = Job("b").Identity with { SourceId = "other" } }]);
        Assert.Equal(2, view.Visible.Count(row => row.IsGroup));
    }

    private static DownloadJobRecord Job(string id) => new()
    {
        JobId = id, TitleDisplayName = "Same title", Identity = new("comix", "title", "hid", id, "group"),
    };
}
