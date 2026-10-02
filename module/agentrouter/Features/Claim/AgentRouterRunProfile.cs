using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Module.Agentrouter.Features.Claim;

/// <summary>One balance reading, in the shape the run JSON stores it.</summary>
public sealed record BalanceSnapshot(
    long Quota,
    long UsedQuota,
    decimal Usd,
    decimal UsedUsd,
    DateTimeOffset FetchedAtUtc);

/// <summary>
/// What one profile's run JSON holds: the account id the flow captured, the
/// api_key that claim captured, and the last balance this screen fetched.
///
/// <para>The browser session is deliberately not here — the Launcher shows and
/// copies the key, and nothing on this screen needs the session.</para>
/// </summary>
internal sealed record AgentRouterRunProfile(
    long? UserId,
    string ApiKey,
    string Pat,
    BalanceSnapshot? Balance);

/// <summary>
/// The claim flow's per-profile JSON, <c>agentrouter-&lt;id&gt;.json</c> beside
/// the status files in <c>%LocalAppData%\Citadel\agentrouter\runs</c>.
///
/// <para>Python owns that file: a claim writes api_key/pat/user_id into it.
/// This class reads it and appends ONE <c>balance</c> object, keeping every key
/// it did not write — a read-modify-write, so a fetched balance can never
/// clobber a captured PAT, and a later claim cannot clobber the balance.</para>
///
/// <para>A missing or malformed file is not an error: the row shows the em
/// dash, exactly as an unclaimed profile does.</para>
/// </summary>
internal sealed class AgentRouterRunProfileStore
{
    private const string BalanceKey = "balance";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,

        // Python owns this file and writes "+" and "/" literally. The default
        // encoder would rewrite those bytes as \u002B on keys this class never
        // touched, so the relaxed encoder is what keeps a PAT byte-identical.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Regex ProfileIdPattern = new(
        "^[A-Za-z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _root;
    private readonly object _sync = new();

    public AgentRouterRunProfileStore(string? runsRoot = null)
        => _root = runsRoot ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "agentrouter",
            "runs");

    /// <summary>The file one profile's credentials belong to.</summary>
    public string PathFor(string profileId)
    {
        if (!IsSafeProfileId(profileId))
        {
            throw new ArgumentException(
                "profile id is not a safe path segment", nameof(profileId));
        }

        return Path.Combine(_root, $"agentrouter-{profileId}.json");
    }

    /// <summary>The row's state, or null when the flow never wrote a file.</summary>
    public AgentRouterRunProfile? Read(string profileId)
    {
        if (!IsSafeProfileId(profileId))
        {
            return null;
        }

        var document = TryLoad(PathFor(profileId));
        if (document is null)
        {
            return null;
        }

        return new AgentRouterRunProfile(
            ReadLong(document, "user_id"),
            ReadString(document, "api_key") ?? string.Empty,
            ReadString(document, "pat") ?? string.Empty,
            ReadBalance(document[BalanceKey] as JsonObject));
    }

    /// <summary>
    /// Adds or replaces the profile's balance block and reports whether the
    /// bytes reached the disk. Nothing else in the file changes.
    /// </summary>
    public bool WriteBalance(string profileId, BalanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var path = PathFor(profileId);

        lock (_sync)
        {
            try
            {
                var document = TryLoad(path) ?? new JsonObject();
                document["profile"] ??= JsonValue.Create(profileId);
                document[BalanceKey] = new JsonObject
                {
                    ["quota"] = snapshot.Quota,
                    ["used_quota"] = snapshot.UsedQuota,
                    ["usd"] = snapshot.Usd,
                    ["used_usd"] = snapshot.UsedUsd,
                    ["fetched_at"] = snapshot.FetchedAtUtc
                        .ToUniversalTime()
                        .ToString(
                            "yyyy-MM-dd'T'HH:mm:ss'Z'",
                            CultureInfo.InvariantCulture),
                };

                Directory.CreateDirectory(_root);

                // Write beside the target, then swap: a reader never observes a
                // half-written file, and an interrupted save leaves the old one.
                var staging = path + ".tmp";
                File.WriteAllText(staging, document.ToJsonString(JsonOptions));
                File.Move(staging, path, overwrite: true);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// A folder name becomes one path segment, so it is validated as one before
    /// it is ever joined: the allow-list the catalog uses, plus the two
    /// traversal names that list would otherwise admit.
    /// </summary>
    private static bool IsSafeProfileId(string? value)
        => !string.IsNullOrWhiteSpace(value)
        && ProfileIdPattern.IsMatch(value)
        && value is not "." and not "..";

    private static JsonObject? TryLoad(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                : null;
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

    /// <summary>A balance block is complete or it is absent — never partial.</summary>
    private static BalanceSnapshot? ReadBalance(JsonObject? balance)
    {
        if (balance is null
            || ReadLong(balance, "quota") is not { } quota
            || ReadLong(balance, "used_quota") is not { } used
            || !TryReadTimestamp(balance, "fetched_at", out var fetched))
        {
            return null;
        }

        return new BalanceSnapshot(
            quota,
            used,
            ReadDecimal(balance, "usd") ?? FromQuota(quota),
            ReadDecimal(balance, "used_usd") ?? FromQuota(used),
            fetched);
    }

    /// <summary>The gateway's own unit: <c>quota_per_unit</c> on the console.</summary>
    private static decimal FromQuota(long quota)
        => (decimal)quota / BalanceProbeResult.QuotaPerUnit;

    private static long? ReadLong(JsonObject document, string key)
        => document[key] is JsonValue value
        && value.TryGetValue<long>(out var number)
            ? number
            : null;

    private static decimal? ReadDecimal(JsonObject document, string key)
        => document[key] is JsonValue value
        && value.TryGetValue<decimal>(out var number)
            ? number
            : null;

    private static string? ReadString(JsonObject document, string key)
        => document[key] is JsonValue value
        && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static bool TryReadTimestamp(
        JsonObject document, string key, out DateTimeOffset moment)
        => DateTimeOffset.TryParse(
            ReadString(document, key),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out moment);
}
