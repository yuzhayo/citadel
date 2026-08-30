namespace Citadel.Core.Modules;

/// <summary>
/// Core listens; it never searches — this is
/// the gate it exposes while waiting.
///
/// Register stays void, and that is a decision rather than an omission. Three
/// things need a registration's outcome (a duplicate rejection, a reserved
/// rejection, and Settings' failure list) but the *caller* needs none of them:
/// the gate implementation holds the registry and the rejection list, and the
/// composition root hands both to Settings. Returning a result here would grow
/// a locked contract, and making it async is a deferred item.
///
/// Register marshals to the main thread internally. The searcher's watcher
/// raises on a threadpool thread and EventStream.Fire is synchronous on the
/// calling thread, so an unmarshalled Register would mutate WPF state
/// off-thread. Callers therefore must not assume the
/// registry has changed by the time Register returns.
/// </summary>
public interface IModuleGate
{
    void Register(ModuleDescriptor descriptor);

    void Unregister(string route);
}
