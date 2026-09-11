using Module.Mangareader.Features.Downloader.ManualUrl;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.CucumberManga;

/// <summary>Resolves only Cucumber Manga's canonical /manga/{slug}/ title route.</summary>
public sealed class CucumberMangaManualUrlProbe(CucumberMangaSource source) : IManualUrlProbe
{
    private readonly CucumberMangaSource _source = source ?? throw new ArgumentNullException(nameof(source));

    public string SourceId => CucumberMangaContract.SourceId;

    public async Task<ManualUrlProbeResult> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.Host.Equals(CucumberMangaContract.Host, StringComparison.OrdinalIgnoreCase)
            && !url.Host.EndsWith("." + CucumberMangaContract.Host, StringComparison.OrdinalIgnoreCase))
        {
            return ManualUrlProbeResult.NoMatch();
        }

        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2
            || !segments[0].Equals("manga", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(segments[1]))
        {
            return ManualUrlProbeResult.Invalid("URL Cucumber Manga harus berupa halaman title /manga/{slug}/.");
        }

        var slug = Uri.UnescapeDataString(segments[1]).Trim();
        if (slug.Length == 0)
        {
            return ManualUrlProbeResult.Invalid("URL Cucumber Manga tidak memiliki slug title.");
        }

        try
        {
            var detail = await _source.ResolveCanonicalTitleAsync(slug, cancellationToken).ConfigureAwait(false);
            return ManualUrlProbeResult.Resolved(detail);
        }
        catch (CucumberMangaContractException exception)
        {
            return ManualUrlProbeResult.Invalid(exception.Message);
        }
    }
}
