using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Queue;

internal sealed class QueueSessionUnavailableException(string message) : InvalidOperationException(message);

/// <summary>Serial borrowing only. Never starts, closes or changes Downloader.</summary>
internal sealed class QueueSharedSessionAdapter(DownloaderPyHostClient browser, Action<string> unavailable)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> BorrowAsync<T>(string sourceId, bool proxyMode,
        Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!browser.HasSessionFor(sourceId, proxyMode))
            {
                const string reason = "Independent proxies exhausted; shared Downloader session is inactive or uses a different route. Queue stopped.";
                unavailable(reason);
                throw new QueueSessionUnavailableException(reason);
            }
            using var scope = browser.BorrowExistingSession();
            // Once dispatched, wait for the bounded reply rather than abandon a
            // Python command that can still write staging after Stop/Remove.
            return await action(CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
