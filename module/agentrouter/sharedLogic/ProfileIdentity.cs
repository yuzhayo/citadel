using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CitadelBridge;

namespace Module.Agentrouter.SharedLogic;

/// <summary>
/// Read-only view of CamoProf's account vault, used only to give a profile a
/// human name.
///
/// CamoProf owns GoogleCredentialStore and keeps it internal, so this screen
/// reads the same <c>identity.json</c> itself rather than importing that type —
/// the arrangement ProxyPoolAdapter already uses on the Proxy side. The profile
/// folder name stays the key everywhere; the address is presentation only, and
/// a profile without a saved identity simply falls back to its folder name.
/// </summary>
internal static class ProfileIdentity
{
    private static readonly Regex SafeProfileId = new(
        "^[A-Za-z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Gmail address when the vault has one, otherwise the profile folder name.</summary>
    public static string DisplayName(string profileId)
        => TryReadEmail(profileId) ?? profileId;

    public static string? TryReadEmail(string profileId)
    {
        // The id becomes one path segment below the vault, so it is validated
        // as one before it is ever joined.
        if (!IsSafeProfileId(profileId))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(CredenzPath.GoogleAccountsRoot(), profileId, "identity.json");
            if (!File.Exists(path))
            {
                return null;
            }

            var record = JsonSerializer.Deserialize<IdentityRecord>(File.ReadAllText(path));
            var email = record?.Email?.Trim();
            return string.IsNullOrEmpty(email) ? null : email;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSafeProfileId(string? value)
        => !string.IsNullOrWhiteSpace(value)
        && SafeProfileId.IsMatch(value)
        && value is not "." and not "..";

    private sealed record IdentityRecord(string? ProfileId, string? Email);
}
