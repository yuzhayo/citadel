using System.Windows;

namespace Citadel.Core.Modules;

/// <summary>
/// Optional view contract for one action owned by the current content header.
/// IModule remains unchanged; views that do not implement this interface keep
/// the normal title-only header.
/// </summary>
public interface IContentHeaderActionProvider
{
    FrameworkElement CreateContentHeaderAction();
}
