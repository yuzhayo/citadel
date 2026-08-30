namespace Citadel.Ui.Controls;

/// <summary>
/// The sidebar's complete navigation model. Selection is the route; no view,
/// descriptor, registry, or module type crosses into Citadel.Ui.
/// </summary>
public sealed record NavEntry(string Route, string Title, string Icon, bool Pinned = false)
{
    public static NavEntry Settings { get; } =
        new("settings", "Settings", "\uE713", Pinned: true);
}
