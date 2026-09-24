namespace Citadel.Shell;

/// <summary>
/// Per-route runtime lifecycle. Boot state is always <see cref="Stopped"/>;
/// runtime state is never persisted as Running.
/// </summary>
public enum ModuleRuntimeState
{
    Stopped,
    Starting,
    Running,
    Faulted,
    Stopping,
    ForceStopping,
}
