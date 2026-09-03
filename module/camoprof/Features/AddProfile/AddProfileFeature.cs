using Module.Camoprof.Providers.Google;
using Module.Camoprof.SharedLogic;

namespace Module.Camoprof.Features.AddProfile;

/// <summary>
/// THE single Launcher-facing entry point for Add Profile. Launcher
/// calls ExecuteAsync and displays the non-secret result; it never
/// opens a browser session, navigates, polls, or stores credentials
/// for this flow — all of that lives behind this contract.
///
/// Initial Add and existing-profile credential repair share this one
/// contract; neither caller gains access to enrollment internals.
/// </summary>
internal sealed class AddProfileFeature
{
    private readonly AddProfileCoordinator _coordinator;

    public AddProfileFeature(
        BrowserSessionCoordinator sessions,
        GoogleCredentialStore credentials)
        => _coordinator = new AddProfileCoordinator(sessions, credentials);

    public Task<AddProfileResult> ExecuteAsync(
        AddProfileRequest request,
        IProgress<AddProfileUpdate>? progress = null,
        CancellationToken cancellationToken = default)
        => _coordinator.ExecuteAsync(request, progress, cancellationToken);
}
