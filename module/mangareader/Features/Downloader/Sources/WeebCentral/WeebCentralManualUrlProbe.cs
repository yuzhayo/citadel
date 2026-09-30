using Module.Mangareader.Features.Downloader.ManualUrl;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.WeebCentral;

/// <summary>Resolves only Weeb Central's canonical /series/{id}/{slug}/ title route.</summary>
public sealed class WeebCentralManualUrlProbe(WeebCentralSource source) : IManualUrlProbe
{
    private readonly WeebCentralSource _source = source ?? throw new ArgumentNullException(nameof(source));

    public string SourceId => WeebCentralContract.SourceId;

    public async Task<ManualUrlProbeResult> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.Host.Equals(WeebCentralContract.Host, StringComparison.OrdinalIgnoreCase)
            && !url.Host.EndsWith("." + WeebCentralContract.Host, StringComparison.OrdinalIgnoreCase))
        {
            return ManualUrlProbeResult.NoMatch();
        }

        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3
            || !segments[0].Equals("series", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(segments[1])
            || string.IsNullOrWhiteSpace(segments[2]))
        {
            return ManualUrlProbeResult.Invalid("URL WeebCentral harus berupa halaman title /series/{id}/{slug}/.");
        }

        var titleId = Uri.UnescapeDataString(segments[1]).Trim();
        var slug = Uri.UnescapeDataString(segments[2]).Trim();
        if (titleId.Length == 0 || slug.Length == 0)
        {
            return ManualUrlProbeResult.Invalid("URL WeebCentral tidak memiliki id atau slug title.");
        }

        try
        {
            var detail = await _source.ResolveCanonicalSeriesAsync(titleId, slug, cancellationToken).ConfigureAwait(false);
            return ManualUrlProbeResult.Resolved(detail);
        }
        catch (WeebCentralContractException exception)
        {
            return ManualUrlProbeResult.Invalid(exception.Message);
        }
    }
}
