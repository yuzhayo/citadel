namespace Module.Camoprof.Providers.Google;

internal enum GoogleAccountState
{
    Unknown,
    Checking,
    Active,
    SignedOut,
    Relogging,
    ActionRequired,
    WrongAccount,
    Offline,
    Degraded,
    ProviderUnavailable,
    CredentialRejected,
    Unlinked,
}

internal sealed record GoogleAccountResult(
    GoogleAccountState State,
    string? Email,
    string Reason,
    DateTimeOffset CheckedAt)
{
    public string Label => State switch
    {
        GoogleAccountState.Checking => "Checking",
        GoogleAccountState.Active => "Active",
        GoogleAccountState.SignedOut => "Signed out",
        GoogleAccountState.Relogging => "Relogging",
        GoogleAccountState.ActionRequired => "Action required",
        GoogleAccountState.WrongAccount => "Wrong account",
        GoogleAccountState.Offline => "Offline",
        GoogleAccountState.Degraded => "Degraded",
        GoogleAccountState.ProviderUnavailable => "Unavailable",
        GoogleAccountState.CredentialRejected => "Rejected",
        GoogleAccountState.Unlinked => "Unlinked",
        _ => "Check",
    };
}
