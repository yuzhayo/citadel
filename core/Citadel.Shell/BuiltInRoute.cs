using System.Windows;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;

namespace Citadel.Shell;

/// <summary>
/// Shell-owned metadata for routes that never pass through module discovery.
/// It keeps the route title beside its factory without growing Citadel.Contract.
/// </summary>
internal sealed record BuiltInRoute
{
    public BuiltInRoute(
        string title,
        Func<Lifetime, FrameworkElement> createView,
        LayoutDeclaration? layout = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("built-in route title cannot be empty", nameof(title));
        }

        Title = title;
        CreateView = createView ?? throw new ArgumentNullException(nameof(createView));
        Layout = layout;
    }

    public string Title { get; }

    public Func<Lifetime, FrameworkElement> CreateView { get; }

    /// <summary>Built-in layout metadata supplied directly by its owning screen.</summary>
    public LayoutDeclaration? Layout { get; }
}
