using Module.Mangareader.Features.Downloader.ManualUrl;

namespace Module.Mangareader.Features.Downloader.Sources.ThunderScans;

public sealed class ThunderScansManualUrlProbe(ThunderScansSource source) : IManualUrlProbe
{
    private readonly ThunderScansSource _source = source ?? throw new ArgumentNullException(nameof(source));
    public string SourceId => ThunderScansContract.SourceId;
    public async Task<ManualUrlProbeResult> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        if (!url.Host.Equals(ThunderScansContract.Host, StringComparison.OrdinalIgnoreCase)) return ManualUrlProbeResult.NoMatch();
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !segments[0].Equals("comics", StringComparison.OrdinalIgnoreCase)) return ManualUrlProbeResult.Invalid("URL ThunderScans harus berupa halaman title /comics/{slug}/.");
        try { return ManualUrlProbeResult.Resolved(await _source.ResolveTitleAsync(Uri.UnescapeDataString(segments[1]), cancellationToken).ConfigureAwait(false)); }
        catch (ThunderScansContractException exception) { return ManualUrlProbeResult.Invalid(exception.Message); }
    }
}
