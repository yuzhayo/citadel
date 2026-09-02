namespace Module.Camoprof.Providers.Google.Enrollment;

/// <summary>
/// Pure mapping for the enrollment flow — no I/O, no credential
/// knowledge. Expected-email validation lives in pyhost (before the
/// secret is released) and is re-checked at the service boundary; this
/// policy only maps states and outcomes to non-secret text.
/// </summary>
internal static class GoogleEnrollmentPolicy
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public static string StatusText(string wireState) => wireState switch
    {
        "armed" => "Waiting for Google login in the open browser…",
        "password_observed" => "Password detected — verifying with Google…",
        "waiting_for_google" => "Verifying with Google…",
        "challenge" => "Extra verification required — finish it in the browser.",
        "complete" => "Account active — saving credential…",
        "consumed" => "Credential saved.",
        "cancelled" => "Cancelled.",
        "expired" => "Enrollment timed out after 10 minutes.",
        "browser_gone" => "The browser window was closed.",
        "wrong_account" => "A different account was logged in.",
        _ => "Waiting…",
    };

    /// <summary>Wire states the poller can stop on.</summary>
    public static GoogleEnrollmentOutcome? OutcomeFor(string wireState)
        => wireState switch
        {
            "cancelled" => GoogleEnrollmentOutcome.Cancelled,
            "expired" => GoogleEnrollmentOutcome.Expired,
            "browser_gone" => GoogleEnrollmentOutcome.BrowserGone,
            "wrong_account" => GoogleEnrollmentOutcome.WrongAccount,
            _ => null,
        };

    public static string LauncherStatus(GoogleEnrollmentResult result)
        => LauncherStatus(result.Outcome, result.Email);

    public static string LauncherStatus(GoogleEnrollmentOutcome outcome, string? email)
        => outcome switch
        {
            GoogleEnrollmentOutcome.Completed
                => "Google account linked as " + (email ?? "unknown") + ".",
            GoogleEnrollmentOutcome.ActiveWithoutPassword
                => "Login succeeded, but no password can be saved — automatic relog is unavailable.",
            GoogleEnrollmentOutcome.Cancelled
                => "Enrollment cancelled; profile remains unlinked.",
            GoogleEnrollmentOutcome.Expired
                => "Enrollment timed out; try again.",
            GoogleEnrollmentOutcome.BrowserGone
                => "The browser window was closed; launch it and try again.",
            GoogleEnrollmentOutcome.WrongAccount
                => "A different account was logged in; nothing was saved.",
            _ => "Enrollment failed.",
        };
}
