namespace Module.Mangareader.Features.Downloader.ManualUrl;

/// <summary>Last-resort signature probe for a public server-rendered manga page.</summary>
public sealed class DynamicManualUrlProbe(DynamicManualSource source) : IManualUrlProbe
{
    private readonly DynamicManualSource _source =
        source ?? throw new ArgumentNullException(nameof(source));

    public string SourceId => DynamicManualSource.SourceId;

    public async Task<ManualUrlProbeResult> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            var detail = await _source.TryResolveAsync(url, cancellationToken).ConfigureAwait(false);
            return detail is null
                ? ManualUrlProbeResult.NoMatch()
                : ManualUrlProbeResult.Resolved(detail);
        }
        catch (DynamicManualContractException exception)
        {
            return ManualUrlProbeResult.Invalid(exception.Message);
        }
    }
}
