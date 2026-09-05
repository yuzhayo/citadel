using System.Text.Json.Nodes;
using CitadelBridge;
using Module.Camoprof.SharedLogic;

namespace Module.Camoprof.Features.AddProfile;

/// <summary>
/// Typed adapter for the camoprof.add_profile.* pyhost commands.
///
/// This is the ONLY C# class that touches the wire protocol for Add
/// Profile. It builds command names and payloads, then routes through
/// BrowserSessionCoordinator's generic session-scoped dispatcher.
/// FinishAsync is the single plaintext-carrying call in the feature —
/// its response goes straight to the coordinator, which DPAPI-saves
/// the credential before any non-secret result is built.
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
        StartAsync = StartCoreAsync;
        StatusAsync = StatusCoreAsync;
        FinishAsync = FinishCoreAsync;
        CancelAsync = CancelCoreAsync;
    }

    /// <summary>Test-only constructor: every seam replaced by the test.</summary>
    internal AddProfilePyHostClient()
    {
    }

    private async Task<JsonObject> StartCoreAsync(
        string profile,
        string? expectedEmail,
        CancellationToken cancellationToken)
    {
        var parameters = new JsonObject();
        if (!string.IsNullOrWhiteSpace(expectedEmail))
        {
            parameters["expected_email"] = expectedEmail;
        }

        return await _sessions!.SendSessionCommandAsync(
            profile,
            "camoprof.add_profile.start",
            parameters,
            cancellationToken);
    }

    private async Task<JsonObject> StatusCoreAsync(
        string profile,
        CancellationToken cancellationToken)
    {
        return await _sessions!.SendSessionCommandAsync(
            profile,
            "camoprof.add_profile.status",
            null,
            cancellationToken);
    }

    private async Task<JsonObject> FinishCoreAsync(
        string profile,
        CancellationToken cancellationToken)
    {
        return await _sessions!.SendSessionCommandAsync(
            profile,
            "camoprof.add_profile.finish",
            null,
            cancellationToken);
    }

    private async Task<JsonObject?> CancelCoreAsync(
        string profile,
        CancellationToken cancellationToken)
    {
        return await _sessions!.SendSessionCommandOrNullAsync(
            profile,
            "camoprof.add_profile.cancel",
            null,
            cancellationToken);
    }
}
