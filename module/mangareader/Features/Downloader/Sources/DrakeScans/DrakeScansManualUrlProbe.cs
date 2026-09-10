using Module.Mangareader.Features.Downloader.ManualUrl;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.DrakeScans;

/// <summary>Resolves only Drake's own canonical title route.</summary>
public sealed class DrakeScansManualUrlProbe(DrakeScansSource source) : IManualUrlProbe
{
    private readonly DrakeScansSource _source = source ?? throw new ArgumentNullException(nameof(source));

    public string SourceId => DrakeScansContract.SourceId;

    public async Task<ManualUrlProbeResult> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.Host.Equals(DrakeScansContract.Host, StringComparison.OrdinalIgnoreCase))
        {
            return ManualUrlProbeResult.NoMatch();
        }

        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3
            || !segments[0].Equals("series", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals("comic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(segments[2]))
        {
            return ManualUrlProbeResult.Invalid("URL Drake harus berupa halaman title /series/comic/{slug}.");
        }

        var slug = Uri.UnescapeDataString(segments[2]);
        try
        {
            var identity = new RemoteTitleIdentity(SourceId, slug, slug, slug);
            return ManualUrlProbeResult.Resolved(
                await _source.GetTitleAsync(identity, cancellationToken).ConfigureAwait(false));
        }
        catch (DrakeScansContractException exception)
        {
            return ManualUrlProbeResult.Invalid(exception.Message);
        }
    }
}
