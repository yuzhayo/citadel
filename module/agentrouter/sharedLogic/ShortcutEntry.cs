namespace Module.Agentrouter.SharedLogic;

/// <summary>
/// One Agentrouter pointer at a CamoProf resident profile.
///
/// The shortcut is a reference, never a copy: the browser folder itself stays
/// owned by CamoProf, which remains the only screen allowed to create or delete
/// it. A shortcut whose folder has since been removed is kept so the operator
/// can see it went missing, and is reported as such rather than dropped.
/// </summary>
internal sealed record ShortcutEntry(
    string ProfileId,
    DateTimeOffset AddedAtUtc,
    string? SelectedProxy = null);
