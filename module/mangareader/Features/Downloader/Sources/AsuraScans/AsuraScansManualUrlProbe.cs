using Module.Mangareader.Features.Downloader.ManualUrl;

namespace Module.Mangareader.Features.Downloader.Sources.AsuraScans;

public sealed class AsuraScansManualUrlProbe(AsuraScansSource source) : IManualUrlProbe
{
    private readonly AsuraScansSource _source = source ?? throw new ArgumentNullException(nameof(source));
    public string SourceId => AsuraScansContract.SourceId;
    public async Task<ManualUrlProbeResult> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        if (!url.Host.Equals(AsuraScansContract.Host, StringComparison.OrdinalIgnoreCase)) return ManualUrlProbeResult.NoMatch();
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !segments[0].Equals("comics", StringComparison.OrdinalIgnoreCase) || segments[1].Equals("chapter", StringComparison.OrdinalIgnoreCase)) return ManualUrlProbeResult.Invalid("URL AsuraScans harus berupa halaman title /comics/{slug}.");
        var slug = System.Text.RegularExpressions.Regex.Replace(Uri.UnescapeDataString(segments[1]), "-[0-9a-f]{8}$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        try { return ManualUrlProbeResult.Resolved(await _source.ResolveTitleAsync(slug, cancellationToken).ConfigureAwait(false)); }
        catch (AsuraScansContractException exception) { return ManualUrlProbeResult.Invalid(exception.Message); }
    }
}
