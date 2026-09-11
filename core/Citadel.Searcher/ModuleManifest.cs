namespace Citadel.Searcher;

/// <summary>
/// One folder's `module.json`, parsed and validated. This type stays inside the
/// searcher on purpose: core only ever sees
/// <c>ModuleDescriptor</c>, so it cannot learn that screens come from disk.
///
/// All six of v0's fields survive translation. `title`,
/// `route`, `entry` and `type` are required; `icon` and `order` are optional.
/// </summary>
internal sealed record ModuleManifest(
    string Title,
    string Route,
    string Entry,
    string Type,
    string? Icon,
    int Order);
