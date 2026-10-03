using System.Globalization;
using Module.Agentrouter.Features.Claim;
using Module.Agentrouter.SharedLogic;

namespace Module.Agentrouter.Features.Shortcuts;

/// <summary>
/// Display state for one row of the shortcut table: a profile this screen
/// already points at, named by its Gmail address when the vault knows one.
///
/// <para>The account id, the captured api_key and the last fetched balance are
/// read from the profile's run JSON — the file the claim flow writes and Check
/// balance updates. The key is the one secret on this row: it is shown here so
/// the owner can see which key a profile holds, and copied on request so it can
/// be pasted elsewhere.</para>
///
/// Only added profiles reach this row, so there is no "not added yet" branch —
/// picking those is the Select profiles… floating screen's job.
/// </summary>
internal sealed class ShortcutRow
{
    public ShortcutRow(string profileId, bool exists, DateTimeOffset? addedAtUtc,
        IReadOnlyList<string> proxyChoices, string selectedProxy)
    {
        ProfileId = profileId;
        Exists = exists;
        AddedAtUtc = addedAtUtc;
        Account = ProfileIdentity.DisplayName(profileId);
        ProxyChoices = proxyChoices;
        SelectedProxy = selectedProxy;
    }

    public string ProfileId { get; }

    /// <summary>
    /// Gateway account id from the profile's run JSON — null until a claim has
    /// recorded the github_&lt;id&gt; chip.
    /// </summary>
    public long? UserId { get; init; }

    /// <summary>Gateway account id, or the em dash while it is unknown.</summary>
    public string UserIdDisplay => UserId?.ToString() ?? "—";

    /// <summary>Sort key; unknown ids sort first.</summary>
    public long UserIdSort => UserId ?? 0;

    /// <summary>
    /// The <c>sk-</c> key the claim flow captured, straight from the profile's
    /// run JSON. Empty until a claim has recorded one.
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>The key as the KEY column shows it, or the em dash.</summary>
    public string ApiKeyDisplay => HasApiKey ? ApiKey : "—";

    /// <summary>False until a claim captured a key, so Copy stays disabled.</summary>
    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

    /// <summary>The key itself, or why there is none to copy.</summary>
    public string ApiKeyToolTip => HasApiKey
        ? ApiKey
        : "no API key captured yet — Claim it first";

    /// <summary>
    /// The last balance this screen fetched, as stored in the profile's run
    /// JSON. Null until Check balance succeeded once; a failed check never
    /// clears a stored number.
    /// </summary>
    public BalanceSnapshot? Balance { get; init; }

    public LoginSnapshot? Login { get; init; }

    public string LoginDisplay => Login is null ? "—" : Login.Success ? "✓" : "✗";

    public string LoginToolTip => Login is null
        ? "belum pernah dicek"
        : Login.Detail;

    /// <summary>USD balance, or the em dash while none was ever fetched.</summary>
    public string BalanceDisplay => Balance is { } snapshot
        ? Format(snapshot.Usd)
        : "—";

    /// <summary>Sort key; never-fetched balances sort first.</summary>
    public decimal BalanceSort => Balance?.Usd ?? -1m;

    /// <summary>What the number means, and when it was read.</summary>
    public string BalanceToolTip => Balance is { } snapshot
        ? $"used {Format(snapshot.UsedUsd)} USD"
          + $" · fetched {snapshot.FetchedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "no balance fetched yet";

    // Invariant: the gateway quotes USD, and a locale-aware render would show
    // "77,26" to a comma-decimal reader.
    private static string Format(decimal value)
        => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>False when the folder is no longer in CamoProf's vault.</summary>
    public bool Exists { get; }

    public DateTimeOffset? AddedAtUtc { get; }

    /// <summary>Gmail address, or the profile folder name when none is saved.</summary>
    public string Account { get; }

    public IReadOnlyList<string> ProxyChoices { get; }

    public string SelectedProxy { get; set; }

    /// <summary>Ready · Missing</summary>
    public string Status => Exists ? "Ready" : "Missing";

    public string Added => AddedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

    /// <summary>Sort key; <see cref="Added"/> is display text and sorts as a string.</summary>
    public long AddedSort => AddedAtUtc?.UtcTicks ?? 0;
}
