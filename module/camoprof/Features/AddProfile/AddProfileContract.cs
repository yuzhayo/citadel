namespace Module.Camoprof.Features.AddProfile;

internal sealed record AddProfileRequest(
    string ProfileId,
    string? ExpectedEmail = null);

internal enum AddProfileOutcome
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
/// Non-secret Add Profile result — the only thing this feature ever
/// returns to Launcher/UI. It deliberately has no password property:
/// the credential is DPAPI-saved inside the feature before this record
/// is built.
/// </summary>
internal sealed record AddProfileResult(
    AddProfileOutcome Outcome,
    string? Email,
    string Reason)
{
    public bool SavedCredential
        => Outcome == AddProfileOutcome.Completed;
}

/// <summary>
/// Live progress for the enrollment dialog: the wire state and its
/// mapped user-facing text — never identity secrets.
/// </summary>
internal sealed record AddProfileUpdate(string WireState, string StatusText);

/// <summary>
/// Pure mapping for the Add Profile flow — no I/O, no credential
/// knowledge. Wire states and outcomes map to non-secret text only.
/// </summary>
internal static class AddProfilePolicy
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
        "failed" => "Could not open the Google login page.",
        _ => "Waiting…",
    };

    /// <summary>Wire states the poller can stop on.</summary>
    public static AddProfileOutcome? OutcomeFor(string wireState)
        => wireState switch
        {
            "cancelled" => AddProfileOutcome.Cancelled,
            "expired" => AddProfileOutcome.Expired,
            "browser_gone" => AddProfileOutcome.BrowserGone,
            "wrong_account" => AddProfileOutcome.WrongAccount,
            "failed" => AddProfileOutcome.Failed,
            _ => null,
        };

    public static string LauncherStatus(AddProfileResult result)
        => LauncherStatus(result.Outcome, result.Email);

    public static string LauncherStatus(AddProfileOutcome outcome, string? email)
        => outcome switch
        {
            AddProfileOutcome.Completed
                => "Google account linked as " + (email ?? "unknown") + ".",
            AddProfileOutcome.ActiveWithoutPassword
                => "Login succeeded, but no password can be saved — automatic relog is unavailable.",
            AddProfileOutcome.Cancelled
                => "Add Profile cancelled; the profile remains unlinked.",
            AddProfileOutcome.Expired
                => "Add Profile timed out; try again.",
            AddProfileOutcome.BrowserGone
                => "The browser window was closed; launch it and try again.",
            AddProfileOutcome.WrongAccount
                => "A different account was logged in; nothing was saved.",
            _ => "Add Profile failed.",
        };
}
