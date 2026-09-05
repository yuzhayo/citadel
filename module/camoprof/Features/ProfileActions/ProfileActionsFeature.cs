using Module.Camoprof.Features.AddProfile;
using Module.Camoprof.Providers.Google;
using Module.Camoprof.SharedLogic;

namespace Module.Camoprof.Features.ProfileActions;

/// <summary>
/// Launcher-facing result of a Google account check.
///
/// <see cref="Account"/> is null when no live check ran, so the caller leaves
/// the row's existing state untouched instead of rendering a check that never
/// happened. A non-null <see cref="Enrollment"/> is this feature's decision
/// that pairing or credential repair is required next; the caller routes it to
/// <see cref="AddProfileFeature"/>, the only owner of that flow.
/// </summary>
internal sealed record GoogleCheckOutcome(
    GoogleAccountResult? Account,
    AddProfileRequest? Enrollment);

/// <summary>
/// Owns the profile actions the Launcher offers but must not sequence or
/// decide by itself: complete deletion, and the Google check policy that
/// determines when pairing or credential repair is required.
/// </summary>
internal sealed class ProfileActionsFeature
{
    private readonly ProfileCatalog _catalog;
    private readonly BrowserSessionCoordinator _sessions;
    private readonly GoogleCredentialStore _credentials;
    private readonly GoogleAccountService _google;

    public ProfileActionsFeature(
        ProfileCatalog catalog,
        BrowserSessionCoordinator sessions,
        GoogleCredentialStore credentials,
        GoogleAccountService google)
    {
        _catalog = catalog;
        _sessions = sessions;
        _credentials = credentials;
        _google = google;
    }

    /// <summary>
    /// Removes a profile completely. The browser must release the resident
    /// folder before the catalog deletes it, or the delete fails on a
    /// directory still in use.
    /// </summary>
    public async Task DeleteAsync(string profileId)
    {
        await _sessions.CloseAsync(profileId);
        await _catalog.DeleteAsync(profileId);
        await _credentials.DeleteAsync(profileId);
    }

    /// <summary>
    /// Checks the profile's Google account and decides whether enrollment is
    /// required. <paramref name="checkStarting"/> runs only when a live check
    /// will actually happen, so the caller can render its in-progress state
    /// without owning that decision.
    /// </summary>
    public async Task<GoogleCheckOutcome> CheckGoogleAsync(
        ProfileEntry profile,
        bool showBrowser,
        Action? checkStarting = null)
    {
        if (!profile.IsLinked)
        {
            return new GoogleCheckOutcome(
                null,
                new AddProfileRequest(profile.ProfileId));
        }

        checkStarting?.Invoke();
        var account = await _google.CheckAsync(profile, showBrowser);
        var enrollment = account.State == GoogleAccountState.CredentialRejected
            ? new AddProfileRequest(profile.ProfileId, profile.Email)
            : null;
        return new GoogleCheckOutcome(account, enrollment);
    }
}
