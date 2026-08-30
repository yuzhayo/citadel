namespace Citadel.Searcher;

/// <summary>
/// Which part of reading a folder failed. Settings shows the stage so a broken
/// citizen says *what* is broken, not just that something is.
/// </summary>
public enum SearchStage
{
    /// <summary>The runtime module root itself could not be created or watched.</summary>
    Root,

    /// <summary>`module.json` is missing, unreadable, malformed, or invalid.</summary>
    Manifest,

    /// <summary>`layout.json` is present but unusable. The screen still registers.</summary>
    Layout,

    /// <summary>The declared entry assembly is missing or not loadable.</summary>
    Entry,

    /// <summary>The declared type is absent, not an IModule, or threw on construction.</summary>
    Type,
}

/// <summary>
/// One folder's current problem, keyed by folder so it can be reconciled rather
/// than appended. A folder that is fixed or
/// deleted stops being listed.
/// </summary>
/// <param name="Folder">Folder name under the runtime module root.</param>
/// <param name="Stage">Where it failed.</param>
/// <param name="Message">What to show the user.</param>
public sealed record ModuleFailure(string Folder, SearchStage Stage, string Message);
