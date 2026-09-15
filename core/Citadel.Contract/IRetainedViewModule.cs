using System.Windows;

namespace Citadel.Core.Modules;

/// <summary>
/// Marker interface for modules whose view should survive navigation away.
/// When a module implements this, the Router caches its view and lifetime
/// on navigate-away and re-attaches them on navigate-back — instead of
/// destroying and recreating them each time.
/// </summary>
public interface IRetainedViewModule
{
    /// <summary>
    /// Called when the Router detaches this module's view from the visual tree
    /// (user navigated to a different module). The view and lifetime remain
    /// alive — just hidden.
    /// </summary>
    void OnViewDetached();

    /// <summary>
    /// Called when the Router re-attaches this module's view to the visual tree
    /// (user navigated back). The view is the same instance that was cached.
    /// </summary>
    void OnViewAttached();
}
