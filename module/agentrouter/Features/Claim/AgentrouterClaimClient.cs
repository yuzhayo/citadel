using System.IO;
using System.Text.Json.Nodes;
using CitadelBridge;

namespace Module.Agentrouter.SharedLogic;

/// <summary>Hasil satu claim. Tidak pernah memuat secret.</summary>
internal sealed record ClaimOutcome(
    string Outcome,
    int ReturnCode,
    string Detail,
    IReadOnlyList<string> Keys)
{
    /// <summary>One line for the status area.</summary>
    public string Describe() => Outcome switch
    {
        "saved" => Keys.Count > 0
            ? "Claimed · keys: " + string.Join(", ", Keys)
            : "Claimed.",
        "busy" => Detail,
        "refused" => Detail,
        _ => Detail.Length > 0 ? "Claim failed: " + Detail : "Claim failed.",
    };
}

/// <summary>
/// Owns Agentrouter's one pyhost process and runs the claim flow through it.
///
/// <para>The claim mutates a remote account and the panel PAT is single-reveal
/// — regenerating it invalidates the previous value, and a value never read
/// back is gone. So at most one claim per profile is allowed in flight, and a
/// second request is refused instead of stacked onto the same session.</para>
///
/// <para>A timed-out or cancelled run is reported as an unknown outcome, never
/// as a failure the caller may safely retry: the account may already have been
/// mutated.</para>
/// </summary>
internal sealed class AgentrouterClaimClient : IDisposable
{
    private const string PluginName = "agentrouter_claim";
    private const string ClaimCommand = "agentrouter.claim";

    // The flow enforces its own wall-clock budget. A request timeout below that
    // would cancel this await while the worker thread kept driving the browser:
    // the caller would be told "TIMEOUT" about an account still being mutated.
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(11);

    private readonly object _sync = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private PyHost? _host;
    private int _disposed;

    /// <summary>Runs the claim flow for one profile. Never runs two at once.</summary>
    public async Task<ClaimOutcome> ClaimAsync(
        string profileId,
        bool headless,
        string? proxy = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        lock (_sync)
        {
            if (!_inFlight.Add(profileId))
            {
                return new ClaimOutcome("busy", 0,
                    "already claiming this profile", []);
            }
        }

        try
        {
            var host = EnsureHost();
            var response = await host.SendAsync(
                    ClaimCommand,
                    new JsonObject
                    {
                        ["profile"] = profileId,
                        ["headless"] = headless,
                        ["proxy"] = proxy,
                    },
                    ClaimTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            var keys = (response["keys"] as JsonArray)?
                .Select(node => node?.GetValue<string>() ?? string.Empty)
                .Where(key => key.Length > 0)
                .ToArray() ?? [];

            return new ClaimOutcome(
                response["outcome"]?.GetValue<string>() ?? "failed",
                response["returncode"]?.GetValue<int>() ?? 1,
                response["detail"]?.GetValue<string>() ?? string.Empty,
                keys);
        }
        finally
        {
            lock (_sync)
            {
                _inFlight.Remove(profileId);
            }
        }
    }

    private PyHost EnsureHost()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_host is not null)
            {
                return _host;
            }

            var python = RuntimeSetup.VenvPython;
            if (!File.Exists(python))
            {
                throw new InvalidOperationException(
                    "runtime belum siap — buka tab Runtime lalu jalankan Setup runtime");
            }

            var script = RuntimeSetup.DeployedPyhostScript;
            if (!File.Exists(script))
            {
                throw new InvalidOperationException(
                    "payload pyhost tidak ter-deploy: " + script);
            }

            // The claim plugin is activated by name; the shared pyhost core
            // stays feature-free.
            _host = PyHost.Start(python, script, CredenzPath.Resolve(), PluginName);
            return _host;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_sync)
        {
            _host?.Dispose();
            _host = null;
        }
    }
}
