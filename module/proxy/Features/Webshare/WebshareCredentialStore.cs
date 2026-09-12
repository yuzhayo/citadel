using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using CitadelBridge;
using Module.Proxy.SharedLogic;

namespace Module.Proxy.Features.Webshare;

/// <summary>Owns Webshare API keys. The file is local-user encrypted and never enters the pool or UI status.</summary>
internal sealed class WebshareCredentialStore(string? root = null)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Citadel.Proxy.Webshare.v1");
    private readonly string _root = Path.GetFullPath(root ?? Path.Combine(CredenzPath.Resolve(), "webshare"));

    private string PathToKeys => Path.Combine(_root, "api-keys.dat");

    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(PathToKeys)) return [];
        var protectedBytes = File.ReadAllBytes(PathToKeys);
        var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<string[]>(plain) is { } values
                ? Normalize(values)
                : [];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public int Save(IEnumerable<string> rawKeys)
    {
        var keys = Normalize(rawKeys).ToArray();
        Directory.CreateDirectory(_root);
        var plain = JsonSerializer.SerializeToUtf8Bytes(keys);
        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        try
        {
            var temporary = PathToKeys + ".tmp";
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, PathToKeys, overwrite: true);
            return keys.Length;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public int AddFromPaste(string? text)
    {
        var added = Split(text);
        if (added.Length == 0) throw new InvalidOperationException("Enter at least one API key.");
        return Save(Load().Concat(added));
    }

    public void Clear()
    {
        if (File.Exists(PathToKeys)) File.Delete(PathToKeys);
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> values) => values
        .Select(value => value?.Trim())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static string[] Split(string? text) => string.IsNullOrWhiteSpace(text)
        ? []
        : text.Split([',', ';', '\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
