using CitadelBridge;
using Module.Mangareader.ShareLogic;
using Module.Mangareader.Sources;
using Module.Mangareader.Features.Downloader.Sources.Comix;

namespace Module.Mangareader.Features.Downloader.Queue;

/// <summary>Lazy per-execution Comix host. No host or requests at construction.</summary>
internal sealed class QueueManifestSession(
    string stagingRoot, string executionId, ProxyPoolAdapter pool,
    IMangaSource sharedSource, QueueSharedSessionAdapter shared,
    Action<string> status,
    Func<ProxyLease, DownloaderPyHostClient>? createClient = null)
{
    private readonly IReadOnlyList<ProxyEndpoint> _candidates = pool.Candidates(ProxyTarget.Browser);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);

    public Task<RemoteChapterManifest> GetAsync(RemoteChapterIdentity chapter, CancellationToken token) =>
        ExecuteAsync(source => source.GetManifestAsync(chapter, token),
            ct => sharedSource.GetManifestAsync(chapter, ct), token);

    public Task<IReadOnlyList<RemoteAlternateChapter>> FindAlternatesAsync(
        RemoteChapterIdentity chapter, CancellationToken token) =>
        ExecuteAsync(source => source.FindAlternateGroupsAsync(chapter, token),
            ct => sharedSource.FindAlternateGroupsAsync(chapter, ct), token);

    private async Task<T> ExecuteAsync<T>(Func<IMangaSource, Task<T>> independent,
        Func<CancellationToken, Task<T>> fallback, CancellationToken token)
    {
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var eligible = _candidates.Where(item => !attempted.Contains(ProxyLeaseRegistry.Key(item))
                && !_failed.Contains(ProxyLeaseRegistry.Key(item)))
                .Where(item => !pool.Reservations.Snapshot().Any(usage => usage.Owner == "downloader-browser"
                    && usage.EndpointKey == ProxyLeaseRegistry.Key(item))).ToArray();
            if (eligible.Length == 0) break;
            status("Independent manifest: waiting for proxy");
            using var reservation = await pool.Reservations.ReserveAsync(
                executionId + "/manifest", eligible, token).ConfigureAwait(false);
            var key = ProxyLeaseRegistry.Key(reservation.Lease.Endpoint);
            attempted.Add(key);
            using var client = createClient?.Invoke(reservation.Lease)
                ?? new DownloaderPyHostClient(stagingRoot, pool, "queue-" + executionId, reservation.Lease);
            // Abort only this owned process, then let its awaited operation settle.
            using var cancelHost = token.Register(client.AbortSession);
            try
            {
                status($"Independent manifest · {key} · attempt {attempted.Count}");
                return await independent(new ComixSource(client)).ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }
            catch (Exception ex) when (ex is TimeoutException
                || ex is PyHostException { Code: "BROWSER_LAUNCH" or "TIMEOUT" or "HOST_EXITED" or "BROWSER_GONE" })
            {
                _failed.Add(key);
            }
            finally
            {
                // Abort waits for this host tree to stop; only then release lease.
                client.AbortSession();
            }
        }
        status("Shared manifest fallback");
        return await shared.BorrowAsync(sharedSource.Id, true, fallback, token).ConfigureAwait(false);
    }
}
