namespace Module.Camoprof.Providers.Google.Enrollment;

internal enum GoogleEnrollmentOutcome
{
    Completed,
    ActiveWithoutPassword,
    Cancelled,
    Expired,
    BrowserGone,
    WrongAccount,
    Failed,
}

/// <summary>
/// Non-secret enrollment result, the only thing the Enrollment folder
/// ever returns to Launcher/UI. It deliberately has no password
/// property: the credential is DPAPI-saved by
/// <see cref="GoogleEnrollmentService"/> before this record is built.
/// </summary>
internal sealed record GoogleEnrollmentResult(
    GoogleEnrollmentOutcome Outcome,
    string? Email,
    string Reason)
{
    public bool SavedCredential
        => Outcome == GoogleEnrollmentOutcome.Completed;
}

/// <summary>
/// Live progress for the enrollment dialog. Carries only the wire state
/// and its mapped, user-facing text — never identity secrets.
/// </summary>
internal sealed record GoogleEnrollmentUpdate(string WireState, string StatusText);
