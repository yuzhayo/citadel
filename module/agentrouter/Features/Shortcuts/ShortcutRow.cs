using Module.Agentrouter.SharedLogic;

namespace Module.Agentrouter.Features.Shortcuts;

/// <summary>
/// Display state for one row of the shortcut table: a profile this screen
/// already points at, named by its Gmail address when the vault knows one.
///
/// Only added profiles reach this row, so there is no "not added yet" branch —
/// picking those is the Select profiles… floating screen's job.
/// </summary>
internal sealed class ShortcutRow
{
    public ShortcutRow(string profileId, bool exists, DateTimeOffset? addedAtUtc)
    {
        ProfileId = profileId;
        Exists = exists;
        AddedAtUtc = addedAtUtc;
        Account = ProfileIdentity.DisplayName(profileId);
    }

    public string ProfileId { get; }

    /// <summary>False when the folder is no longer in CamoProf's vault.</summary>
    public bool Exists { get; }

    public DateTimeOffset? AddedAtUtc { get; }

    /// <summary>Gmail address, or the profile folder name when none is saved.</summary>
    public string Account { get; }

    /// <summary>Ready · Missing</summary>
    public string Status => Exists ? "Ready" : "Missing";

    public string Added => AddedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

    /// <summary>Sort key; <see cref="Added"/> is display text and sorts as a string.</summary>
    public long AddedSort => AddedAtUtc?.UtcTicks ?? 0;
}
