using Module.Mangareader.Features.Downloader.ManualUrl;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Downloader.Tests;

public sealed class ManualUrlFeatureTests
{
    [Fact]
    public async Task ResolvesWithTheFirstMatchingProbeAndPreservesProviderIdentity()
    {
        var drake = new StubProbe("drake-scans", _ => ManualUrlProbeResult.NoMatch());
        var cucumber = new StubProbe("cucumber-manga", _ => ManualUrlProbeResult.Resolved(Title("cucumber-manga")));
        var comix = new StubProbe("comix", _ => throw new Xunit.Sdk.XunitException("later probe must not run"));
        var feature = new ManualUrlFeature([drake, cucumber, comix]);

        await feature.LoadAsync("https://cucumbermanga.com/manga/example/", CancellationToken.None);

        var resolution = Assert.IsType<ManualUrlResolution>(feature.State.Resolution);
        Assert.Equal("cucumber-manga", resolution.SourceId);
        Assert.Equal("cucumber-manga", resolution.Title.Summary.Identity.SourceId);
        Assert.False(feature.State.IsBusy);
        Assert.Null(feature.State.ErrorMessage);
    }

    [Fact]
    public async Task RejectsNonHttpUrlWithoutCallingAnyProbe()
    {
        var probe = new StubProbe("drake-scans", _ => throw new Xunit.Sdk.XunitException("probe must not run"));
        var feature = new ManualUrlFeature([probe]);

        await feature.LoadAsync("file:///C:/private.cbz", CancellationToken.None);

        Assert.NotNull(feature.State.ErrorMessage);
        Assert.Null(feature.State.Resolution);
        Assert.False(feature.State.IsBusy);
    }

    [Fact]
    public async Task StopsAtMatchedButInvalidInsteadOfPretendingAnotherProviderFits()
    {
        var drake = new StubProbe("drake-scans", _ => ManualUrlProbeResult.Invalid("Drake title response is invalid."));
        var cucumber = new StubProbe("cucumber-manga", _ => throw new Xunit.Sdk.XunitException("later probe must not run"));
        var feature = new ManualUrlFeature([drake, cucumber]);

        await feature.LoadAsync("https://drakecomic.net/series/comic/example", CancellationToken.None);

        Assert.Equal("Drake title response is invalid.", feature.State.ErrorMessage);
        Assert.Null(feature.State.Resolution);
    }

    [Fact]
    public async Task ReportsUnsupportedWhenNoProbeRecognizesTheUrl()
    {
        var feature = new ManualUrlFeature([new StubProbe("drake-scans", _ => ManualUrlProbeResult.NoMatch())]);

        await feature.LoadAsync("https://example.test/title/a", CancellationToken.None);

        Assert.Equal("URL belum didukung oleh provider yang tersedia.", feature.State.ErrorMessage);
        Assert.Null(feature.State.Resolution);
    }

    private static RemoteTitleDetail Title(string sourceId) => new(
        new RemoteTitleSummary(
            new RemoteTitleIdentity(sourceId, "title-id", "title-hid", "example"),
            "Example",
            null,
            null),
        null,
        [],
        []);

    private sealed class StubProbe(string sourceId, Func<Uri, ManualUrlProbeResult> resolve) : IManualUrlProbe
    {
        public string SourceId => sourceId;

        public Task<ManualUrlProbeResult> ProbeAsync(Uri url, CancellationToken cancellationToken) =>
            Task.FromResult(resolve(url));
    }
}
