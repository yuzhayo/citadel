using Module.Mangareader.Features.Downloader.ManualUrl;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.Comix;

/// <summary>Resolves only Comix's own canonical title route through its existing transport.</summary>
public sealed class ComixManualUrlProbe(ComixSource source) : IManualUrlProbe
{
    private readonly ComixSource _source = source ?? throw new ArgumentNullException(nameof(source));

    public string SourceId => ComixContract.SourceId;

    public async Task<ManualUrlProbeResult> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.Host.Equals(new Uri(ComixContract.BaseUrl).Host, StringComparison.OrdinalIgnoreCase))
        {
            return ManualUrlProbeResult.NoMatch();
        }

        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2
            || !segments[0].Equals("title", StringComparison.OrdinalIgnoreCase)
            || !TryReadTitle(segments[1], out var hid, out var slug))
        {
            return ManualUrlProbeResult.Invalid("URL Comix harus berupa halaman title /title/{hid}-{slug}.");
        }

        try
        {
            var identity = new RemoteTitleIdentity(SourceId, hid, hid, slug);
            return ManualUrlProbeResult.Resolved(
                await _source.GetTitleAsync(identity, cancellationToken).ConfigureAwait(false));
        }
        catch (ComixContractException exception)
        {
            return ManualUrlProbeResult.Invalid(exception.Message);
        }
    }

    private static bool TryReadTitle(string segment, out string hid, out string slug)
    {
        var separator = segment.IndexOf('-');
        if (separator <= 0 || separator == segment.Length - 1)
        {
            hid = string.Empty;
            slug = string.Empty;
            return false;
        }

        hid = Uri.UnescapeDataString(segment[..separator]);
        slug = Uri.UnescapeDataString(segment[(separator + 1)..]);
        return !string.IsNullOrWhiteSpace(hid) && !string.IsNullOrWhiteSpace(slug);
    }
}
