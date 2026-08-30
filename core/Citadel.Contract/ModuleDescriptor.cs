namespace Citadel.Core.Modules;

/// <summary>
/// Everything the shell needs about a screen,
/// and nothing about where it came from — no folder path, no manifest, no load
/// context. ModuleManifest deliberately lives in the searcher instead, so core
/// cannot learn that screens come from disk.
/// </summary>
public sealed record ModuleDescriptor(
    string Route,
    string Title,
    string? Icon,
    int Order,
    IModule Instance,
    LayoutDeclaration? Layout);
