namespace Module.Mangareader.Features.Downloader.Sources;

/// <summary>
/// Optional provider-owned prerequisite for Queue execution. Providers that do
/// not implement this contract remain immediately runnable; the Queue never
/// needs to know which provider requires a prepared browser session.
/// </summary>
public interface IQueueSourceReadiness
{
    QueueSourceReadiness GetQueueReadiness();
}

/// <summary>Immutable answer read before the Queue starts provider work.</summary>
public sealed record QueueSourceReadiness(bool IsReady, string? BlockedReason)
{
    public static QueueSourceReadiness Ready { get; } = new(true, null);

    public static QueueSourceReadiness Blocked(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new QueueSourceReadiness(false, reason);
    }
}
