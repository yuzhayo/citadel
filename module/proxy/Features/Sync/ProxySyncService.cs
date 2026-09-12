using System.Diagnostics;
using System.Net.Http;
using CitadelBridge;
using Module.Proxy.SharedLogic;

namespace Module.Proxy.Features.Sync;

internal sealed record ProxySourceOutcome(string Name, int Candidates, string? Error);

internal sealed record ProxySyncProgress(
    string Phase,
    int SourcesCompleted,
    int SourcesTotal,
    int Candidates,
    int Tested,
    int Reachable,
    int Failed,
    TimeSpan Elapsed);

internal sealed record ProxySyncResult(
    IReadOnlyList<ProxyEndpoint> Reachable,
    IReadOnlyList<ProxyHealthRecord> Health,
    IReadOnlyList<ProxySourceOutcome> Sources,
    int Skipped,
    int Banned,
    int Tested,
    TimeSpan Elapsed);

internal interface IProxySourceFetcher
{
    Task<string> FetchAsync(ProxySource source, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class HttpProxySourceFetcher(HttpClient client) : IProxySourceFetcher
{
    private readonly HttpClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<string> FetchAsync(
        ProxySource source,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 Citadel/Proxy");
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutCancellation.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeoutCancellation.Token).ConfigureAwait(false);
    }
}

internal sealed class ProxySyncService(
    IProxySourceFetcher fetcher,
    IProxyReachabilityProbe probe,
    IReadOnlyList<ProxySource>? sources = null)
{
    private readonly IProxySourceFetcher _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
    private readonly IProxyReachabilityProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    private readonly IReadOnlyList<ProxySource> _sources = sources ?? ProxySourceCatalog.All;

    public async Task<ProxySyncResult> RunAsync(
        ProxySettings settings,
        IReadOnlySet<string> banned,
        IProgress<ProxySyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(banned);
        settings = settings.Validate();
        var stopwatch = Stopwatch.StartNew();
        var candidates = new SortedDictionary<string, ProxyEndpoint>(StringComparer.Ordinal);
        var outcomes = new List<ProxySourceOutcome>(_sources.Count);
        var skipped = 0;
        var bannedCount = 0;

        for (var i = 0; i < _sources.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = _sources[i];
            try
            {
                var body = await _fetcher.FetchAsync(
                    source,
                    TimeSpan.FromSeconds(settings.SourceRequestTimeoutSeconds),
                    cancellationToken).ConfigureAwait(false);
                var snapshot = ProxyPoolContract.ParseLines(
                    body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'),
                    source.DefaultScheme);
                skipped += snapshot.SkippedLines;
                var sourceCandidates = 0;
                foreach (var endpoint in snapshot.Endpoints)
                {
                    if (banned.Contains(endpoint.Canonical))
                    {
                        bannedCount++;
                        continue;
                    }
                    if (candidates.TryAdd(endpoint.Canonical, endpoint))
                    {
                        sourceCandidates++;
                    }
                }
                outcomes.Add(new ProxySourceOutcome(source.Name, sourceCandidates, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcomes.Add(new ProxySourceOutcome(source.Name, 0, SafeError(ex)));
            }

            progress?.Report(new ProxySyncProgress(
                "Fetching sources",
                i + 1,
                _sources.Count,
                candidates.Count,
                0,
                0,
                0,
                stopwatch.Elapsed));
        }

        if (outcomes.All(item => item.Error is not null))
        {
            throw new InvalidOperationException("Every proxy source failed; existing pool was preserved.");
        }

        var orderedCandidates = candidates.Values.ToArray();
        var reachable = new List<ProxyEndpoint>();
        var health = new List<ProxyHealthRecord>();
        var resultGate = new object();
        var tested = 0;
        var failed = 0;
        var next = -1;
        var workerCount = Math.Min(settings.ParallelTcpChecks, orderedCandidates.Length);
        var workers = Enumerable.Range(0, workerCount).Select(async _ =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                lock (resultGate)
                {
                }

                var index = Interlocked.Increment(ref next);
                if (index >= orderedCandidates.Length)
                {
                    return;
                }

                var endpoint = orderedCandidates[index];
                var probe = await _probe.ProbeAsync(
                    endpoint,
                    TimeSpan.FromSeconds(settings.ProxyValidationTimeoutSeconds),
                    cancellationToken).ConfigureAwait(false);

                int testedNow;
                int reachableNow;
                int failedNow;
                lock (resultGate)
                {
                    testedNow = ++tested;
                    if (probe.IsHealthy)
                    {
                        reachable.Add(endpoint);
                        health.Add(probe.ToHealthRecord(endpoint, DateTimeOffset.UtcNow));
                    }
                    else if (!probe.IsHealthy)
                    {
                        failed++;
                    }
                    reachableNow = reachable.Count;
                    failedNow = failed;
                }
                progress?.Report(new ProxySyncProgress(
                    "Checking reachability",
                    _sources.Count,
                    _sources.Count,
                    orderedCandidates.Length,
                    testedNow,
                    reachableNow,
                    failedNow,
                    stopwatch.Elapsed));
            }
        }).ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        stopwatch.Stop();
        return new ProxySyncResult(
            reachable.OrderBy(item => item.Canonical, StringComparer.Ordinal).ToArray(),
            health.OrderBy(item => item.EndpointKey, StringComparer.Ordinal).ToArray(),
            outcomes,
            skipped,
            bannedCount,
            tested,
            stopwatch.Elapsed);
    }

    private static string SafeError(Exception error) =>
        error.GetType().Name + ": " + error.Message;
}
