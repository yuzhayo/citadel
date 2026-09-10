using System.Net;
using System.Net.Http;
using System.Text;
using Module.Mangareader.Features.Downloader.ManualUrl;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Downloader.Tests;

public sealed class DynamicManualSourceTests
{
    private const string TitleHtml = """
        <html><body>
          <main>
            <h1 class="entry-title">Catastrophic Necromancer</h1>
            <div class="entry-content">
              <img data-src="https://assets.example.test/cover.webp" alt="Catastrophic Necromancer cover" />
              <p>A necromancer returns to a world changed by monsters and magic.</p>
              <div class="comics-table-wrap"><table><tbody>
                <tr><td><a href="/manga/catastrophic-necromancer-chapter-2/">Catastrophic Necromancer - Chapter 2</a></td></tr>
                <tr><td><a href="/manga/catastrophic-necromancer-chapter-1/">Catastrophic Necromancer - Chapter 1</a></td></tr>
              </tbody></table></div>
            </div>
          </main>
        </body></html>
        """;

    private const string ChapterHtml = """
        <html><body><div class="chapter-content">
          <img data-src="https://cdn.example.test/chapter-2-001.webp" />
          <img data-src="https://cdn.example.test/chapter-2-002.webp" />
        </div></body></html>
        """;

    [Fact]
    public async Task ArbitraryHostWithSupportedHtmlSignatureResolvesEndToEnd()
    {
        var handler = new RoutingHandler(request =>
            request.RequestUri?.AbsolutePath.Contains("chapter-2", StringComparison.Ordinal) == true
                ? Html(ChapterHtml)
                : Html(TitleHtml));
        var source = new DynamicManualSource(new HttpClient(handler));
        var titleUrl = new Uri("https://catastrophicnecromancer.us/");

        var detail = await source.TryResolveAsync(titleUrl, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(DynamicManualSource.SourceId, detail.Summary.Identity.SourceId);
        Assert.Equal(titleUrl.AbsoluteUri, detail.Summary.Identity.TitleId);
        Assert.Equal("Catastrophic Necromancer", detail.Summary.DisplayName);
        Assert.Equal("https://assets.example.test/cover.webp", detail.Summary.CoverUrl);

        var groups = await source.GetGroupsAsync(detail.Summary.Identity, CancellationToken.None);
        var chapters = await source.GetChaptersAsync(
            detail.Summary.Identity,
            Assert.Single(groups).Identity,
            CancellationToken.None);

        Assert.Equal(["1", "2"], chapters.Select(chapter => chapter.Identity.ChapterNumber));
        var chapter = chapters.Single(item => item.Identity.ChapterNumber == "2");
        var manifest = await source.GetManifestAsync(chapter.Identity, CancellationToken.None);
        Assert.Equal(
            ["https://cdn.example.test/chapter-2-001.webp", "https://cdn.example.test/chapter-2-002.webp"],
            manifest.Pages.Select(page => page.Url));
        Assert.Equal(chapter.Identity.ChapterId, manifest.RequestHeaders["Referer"]);
    }

    [Fact]
    public async Task HtmlWithoutTitleAndChapterSignatureIsDeclined()
    {
        var source = new DynamicManualSource(new HttpClient(
            new RoutingHandler(_ => Html("<html><body><h1>News</h1></body></html>"))));

        var detail = await source.TryResolveAsync(
            new Uri("https://example.test/article"),
            CancellationToken.None);

        Assert.Null(detail);
    }

    [Fact]
    public void DynamicSourceIsQueueResolvableButHiddenFromBrowseProviders()
    {
        var source = new DynamicManualSource(new HttpClient(
            new RoutingHandler(_ => Html(TitleHtml))));
        var registry = new MangaSourceRegistry([], [source]);

        Assert.Empty(registry.Sources);
        Assert.Same(source, registry.FindSource(DynamicManualSource.SourceId));
        Assert.DoesNotContain(source, ((IMangaSourceDirectory)registry).AvailableSources);
        Assert.IsType<DynamicManualUrlProbe>(Assert.Single(ManualUrlProbeRegistry.Create(registry)));
    }

    private static HttpResponseMessage Html(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "text/html"),
    };

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
