using Module.Camoprof.SharedLogic;

namespace Module.Camoprof.Providers.Google.Enrollment;

/// <summary>
/// The single enrollment contract the Launcher talks to. It knows
/// nothing about selectors, polling, or pyhost responses — and it can
/// never carry a password: <see cref="GoogleEnrollmentResult"/> has no
/// such property. Constructed from the coordinator and credential store
/// the view already holds.
/// </summary>
internal sealed class GoogleEnrollmentFeature
{
    private readonly GoogleEnrollmentService _service;

    public GoogleEnrollmentFeature(
        BrowserSessionCoordinator sessions,
        GoogleCredentialStore credentials)
        => _service = new GoogleEnrollmentService(sessions, credentials);

    public Task<GoogleEnrollmentResult> EnrollAsync(
        string profileId,
        string? expectedEmail = null,
        IProgress<GoogleEnrollmentUpdate>? progress = null,
        CancellationToken cancellationToken = default)
        => _service.EnrollAsync(profileId, expectedEmail, progress, cancellationToken);
}
