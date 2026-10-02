using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CitadelBridge;
using Module.Agentrouter.SharedLogic;

namespace Module.Agentrouter.Features.Claim;

/// <summary>
/// One profile's gateway credentials.
///
/// <para><see cref="Session"/> is the authenticated browser session's cookie
/// value. That is what the account-state endpoint accepts — a panel PAT sent
/// as <c>Authorization: Bearer</c> is rejected with 401.</para>
///
/// <para><see cref="Pat"/> is the System Access Token. It comes back
/// <em>inside</em> the self response rather than being what authenticates it,
/// so it is stored for callers that need it, not because the probe consumes
/// it.</para>
///
/// <para><see cref="ApiKey"/> is the sk- key, used to spend quota on
/// <c>/v1/*</c>. A distinct credential with a distinct audience.</para>
/// </summary>
internal sealed record AgentRouterCredential(
    string ApiKey,
    string Pat,
    string Session,
    long UserId);

/// <summary>
/// The sole reader and writer of an agentrouter credential blob.
///
/// Mirrors the rule CamoProf's GoogleCredentialStore works under: one owner
/// unprotects, the UI never touches the file. Protected with Windows DPAPI
/// scoped to CurrentUser, written beside the profile's existing
/// <c>identity.json</c> so a profile's credentials and identity stay in one
/// directory.
///
/// The New API PAT is displayed once and regenerating it invalidates the
/// previous value, so a failed write is not recoverable by re-reading — it has
/// to be regenerated. Writes are therefore atomic and acknowledged only after
/// the bytes are on disk.
/// </summary>
internal sealed class AgentRouterCredentialStore
{
    private const string FileName = "agentrouter-credentials.dat";

    private readonly string _root;

    public AgentRouterCredentialStore(string? vaultRoot = null)
    {
        _root = vaultRoot ?? Path.Combine(
            CredenzPath.Resolve(), "google", "accounts");
    }

    public string PathFor(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return Path.Combine(_root, SafeSegment(profileId), FileName);
    }

    public bool Has(string profileId) => File.Exists(PathFor(profileId));

    /// <summary>Returns the stored credential, or null when none was ever captured.</summary>
    public AgentRouterCredential? Read(string profileId)
    {
        var path = PathFor(profileId);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(
                File.ReadAllBytes(path), optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // A blob written by another Windows user, or a partial write from a
            // crash. Treat as absent: the caller regenerates.
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<Payload>(plain, JsonOptions);
            return record is null || string.IsNullOrWhiteSpace(record.Session)
                ? null
                : new AgentRouterCredential(
                    record.ApiKey ?? string.Empty,
                    record.Pat ?? string.Empty,
                    record.Session,
                    record.UserId);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// Persists the credential and returns only after the bytes are durable.
    /// Callers capture the PAT once and must treat a false return as lost.
    /// </summary>
    public bool TryWrite(string profileId, AgentRouterCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(credential.Session) || credential.UserId <= 0)
        {
            return false;
        }

        var path = PathFor(profileId);
        var payload = new Payload(
            credential.ApiKey, credential.Pat, credential.Session, credential.UserId);
        var plain = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        byte[] cipher;

        try
        {
            cipher = ProtectedData.Protect(
                plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        var staging = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(staging, cipher);

            // Flush before the swap: the PAT is single-reveal, so a value that
            // survives a crash is worth more than a fast return.
            using (var stream = new FileStream(
                staging, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Flush(flushToDisk: true);
            }

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
        finally
        {
            CryptographicOperations.ZeroMemory(cipher);
            TryDelete(staging);
        }
    }

    /// <summary>Display form for the KEY column. Never returns the secret.</summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "—";
        }

        return value.Length <= 12
            ? new string('•', value.Length)
            : $"{value[..4]}••••{value[^4..]}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The profile id becomes one path segment, so it is validated as one
    /// before it is ever joined. Same allow-list and traversal rejection the
    /// catalog uses.
    /// </summary>
    private static string SafeSegment(string profileId)
    {
        foreach (var c in profileId)
        {
            var ok = char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';
            if (!ok)
            {
                throw new ArgumentException(
                    "profile id contains characters that are not valid in a path segment",
                    nameof(profileId));
            }
        }

        if (profileId is "." or "..")
        {
            throw new ArgumentException("profile id is a traversal name", nameof(profileId));
        }

        return profileId;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record Payload(string ApiKey, string Pat, string Session, long UserId);
}
