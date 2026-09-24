namespace Citadel.Shell;

/// <summary>
/// Shell-only outcome of an actual-unregistration runtime release. Lives here
/// (not in Contract) because only the Gate↔coordinator bind needs it; Watcher
/// still sees the void IModuleGate.Unregister API.
/// </summary>
public sealed record RuntimeReleaseResult(bool Success, string? Error)
{
    public static readonly RuntimeReleaseResult Released = new(true, null);

    public static RuntimeReleaseResult Failed(string error) => new(false, error);
}
