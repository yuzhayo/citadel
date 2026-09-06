namespace Citadel.Ui.Controls;

/// <summary>
/// The sidebar's complete navigation model. Selection is the route; no view,
/// descriptor, registry, or module type crosses into Citadel.Ui.
/// </summary>
public sealed record NavEntry(
    string Route,
    string Title,
    string Icon,
    bool Pinned = false,
    string? GroupId = null,
    bool IsGroup = false,
    bool IsChild = false)
{
    public static NavEntry Settings { get; } =
        new("settings", "Settings", "\uE713", Pinned: true);

    public static NavEntry Group(string id, string title, bool expanded) => new(
        $"sidebar-group:{id}",
        title,
        expanded ? "\uE70D" : "\uE76C",
        GroupId: id,
        IsGroup: true);
}
