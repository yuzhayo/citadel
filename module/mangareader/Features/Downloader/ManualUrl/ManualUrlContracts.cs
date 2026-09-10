using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.ManualUrl;

/// <summary>
/// One source-owned URL recognizer. A probe either declines the URL, resolves
/// it to the source's existing identity, or reports that its own matching
/// contract failed. It never owns UI, Queue, or a browser lifecycle.
/// </summary>
public interface IManualUrlProbe
{
    string SourceId { get; }

    Task<ManualUrlProbeResult> ProbeAsync(Uri url, CancellationToken cancellationToken);
}

public abstract record ManualUrlProbeResult
{
    private ManualUrlProbeResult()
    {
    }

    public sealed record NoMatchResult : ManualUrlProbeResult;

    public sealed record ResolvedResult(RemoteTitleDetail Title) : ManualUrlProbeResult;

    public sealed record InvalidResult(string Message) : ManualUrlProbeResult;

    public static ManualUrlProbeResult NoMatch() => new NoMatchResult();

    public static ManualUrlProbeResult Resolved(RemoteTitleDetail title) =>
        new ResolvedResult(title ?? throw new ArgumentNullException(nameof(title)));

    public static ManualUrlProbeResult Invalid(string message) =>
        new InvalidResult(string.IsNullOrWhiteSpace(message)
            ? "Provider tidak dapat membaca URL ini."
            : message);
}

/// <summary>
/// A title resolved from a direct URL. SourceId remains the real source id so
/// Queue, manifest retrieval, and published provenance do not gain a fake
/// manual provider identity.
/// </summary>
public sealed record ManualUrlResolution(string SourceId, RemoteTitleDetail Title)
{
    public static ManualUrlResolution From(string sourceId, RemoteTitleDetail title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(title);
        if (!string.Equals(sourceId, title.Summary.Identity.SourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Resolved title identity belongs to another source.", nameof(title));
        }

        return new ManualUrlResolution(sourceId, title);
    }
}

public sealed record ManualUrlState
{
    public string UrlText { get; init; } = string.Empty;

    public bool IsBusy { get; init; }

    public string? StatusMessage { get; init; }

    public string? ErrorMessage { get; init; }

    public ManualUrlResolution? Resolution { get; init; }
}
