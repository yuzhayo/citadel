using System.Text.Json.Nodes;
using CitadelBridge;
using Module.Camoprof.SharedLogic;

namespace Module.Camoprof.Features.AddProfile;

/// <summary>
/// Typed adapter for the camoprof.add_profile.* pyhost commands.
///
/// This is the ONLY C# class that touches the wire protocol for Add
/// Profile. FinishAsync is the single plaintext-carrying call in the
/// feature — its response goes straight to the coordinator, which
/// DPAPI-saves the credential before any non-secret result is built.
/// </summary>
internal sealed class AddProfilePyHostClient
{
    private readonly BrowserSessionCoordinator? _sessions;

    internal Func<string, string?, CancellationToken, Task<JsonObject>> StartAsync { get; set; } = null!;
    internal Func<string, CancellationToken, Task<JsonObject>> StatusAsync { get; set; } = null!;
    internal Func<string, CancellationToken, Task<JsonObject>> FinishAsync { get; set; } = null!;
    internal Func<string, CancellationToken, Task<JsonObject?>> CancelAsync { get; set; } = null!;

    public AddProfilePyHostClient(BrowserSessionCoordinator sessions)
    {
        _sessions = sessions;
        StartAsync = (profile, expectedEmail, token)
            => sessions.StartAddProfileAsync(profile, expectedEmail, token);
        StatusAsync = (profile, token)
            => sessions.AddProfileStatusAsync(profile, token);
        FinishAsync = (profile, token)
            => sessions.AddProfileFinishAsync(profile, token);
        CancelAsync = (profile, token)
            => sessions.AddProfileCancelAsync(profile, token);
    }

    /// <summary>Test-only constructor: every seam replaced by the test.</summary>
    internal AddProfilePyHostClient()
    {
    }
}
