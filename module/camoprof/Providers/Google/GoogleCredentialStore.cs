using System.IO;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CitadelBridge;

namespace Module.Camoprof.Providers.Google;

internal sealed class GoogleCredentialStore
{
    private static readonly Regex ProfileIdPattern = new(
        "^[A-Za-z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Citadel.CamoProf.Google.v1");

    private readonly string _root = CredenzPath.GoogleAccountsRoot();

    public GoogleAccountRecord? TryLoad(string profileId)
    {
        if (!IsValidProfileId(profileId))
        {
            return null;
        }

        var path = Path.Combine(ResolveSafeTarget(profileId), "identity.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<GoogleAccountRecord>(File.ReadAllText(path));
            return record is not null
                   && string.Equals(record.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)
                   && IsValidEmail(record.Email)
                ? record
                : null;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException)
        {
            return null;
        }
    }

    public bool HasPassword(string profileId)
        => IsValidProfileId(profileId)
           && File.Exists(Path.Combine(ResolveSafeTarget(profileId), "password.dat"));

    public Task SaveAsync(
        string profileId,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidProfileId(profileId))
        {
            throw new InvalidOperationException("profile id tidak sah");
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        if (!IsValidEmail(normalizedEmail))
        {
            throw new InvalidOperationException("email Google tidak sah");
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("password belum diisi");
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ResolveSafeTarget(profileId);
            Directory.CreateDirectory(target);

            var existing = TryLoad(profileId);
            var now = DateTimeOffset.Now;
            var record = new GoogleAccountRecord(
                profileId,
                normalizedEmail,
                "google",
                existing?.CreatedAt ?? now,
                now);

            var passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[] protectedBytes;
            try
            {
                protectedBytes = ProtectedData.Protect(
                    passwordBytes,
                    Entropy,
                    DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }

            var passwordTemp = Path.Combine(target, "password.dat.tmp");
            var identityTemp = Path.Combine(target, "identity.json.tmp");
            File.WriteAllBytes(passwordTemp, protectedBytes);
            File.WriteAllText(
                identityTemp,
                JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(passwordTemp, Path.Combine(target, "password.dat"), overwrite: true);
            File.Move(identityTemp, Path.Combine(target, "identity.json"), overwrite: true);
        }, cancellationToken);
    }

    public string ReadPassword(string profileId)
    {
        if (!IsValidProfileId(profileId))
        {
            throw new InvalidOperationException("profile id tidak sah");
        }

        var path = Path.Combine(ResolveSafeTarget(profileId), "password.dat");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("password belum tersimpan");
        }

        var protectedBytes = File.ReadAllBytes(path);
        var plainBytes = ProtectedData.Unprotect(
            protectedBytes,
            Entropy,
            DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (!IsValidProfileId(profileId))
        {
            throw new InvalidOperationException("profile id tidak sah");
        }

        var target = ResolveSafeTarget(profileId);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }, cancellationToken);
    }

    private static bool IsValidEmail(string email)
        => MailAddress.TryCreate(email, out var parsed)
           && string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase);

    private static bool IsValidProfileId(string profileId)
        => ProfileIdPattern.IsMatch(profileId) && profileId is not "." and not "..";

    private string ResolveSafeTarget(string profileId)
    {
        var root = Path.GetFullPath(_root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(root, profileId));
        if (!target.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("account path keluar dari root Credenz");
        }

        return target;
    }
}
