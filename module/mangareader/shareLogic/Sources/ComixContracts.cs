using System.IO;
using System.Text;

namespace Module.Mangareader.Sources;

// The Comix wire-contract vocabulary, declared ONCE at module level. Both Comix
// providers (Downloader and Catalog) and both page decoders previously carried
// byte-identical copies of these types in their own namespaces; same-name-in-two-
// namespaces is the hazard class that made the CatalogMirror throttle catch bind
// the wrong type (BUG-1). Captured provider data lives here; the POLICY choice of
// which captured default a provider uses stays with each provider.

public enum ComixGenreMode
{
    And,
    Or,
}


public sealed record ComixSortOption(string Key, string DisplayName, string OrderField, string Direction)
{
    public override string ToString() => DisplayName;
}


public sealed record ComixScrambleHeader(long Seed, int Grid, int Algorithm, int? Hash);

public static class ComixScrambleHeaders
{
    public const string SeedHeader = "x-scramble-seed";
    public const string GridHeader = "x-scramble-grid";
    public const string AlgorithmHeader = "x-scramble-algo";
    public const string HashHeader = "x-scramble-hash";

    /// <summary>
    /// Reads the declared scramble header set. Returns null when the payload
    /// was not declared scrambled, and throws when it was declared but cannot
    /// be parsed — an unparseable variant must fail visibly, not silently pass
    /// scrambled bytes through as a page.
    /// </summary>
    public static ComixScrambleHeader? Parse(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return null;

        var seed = Find(headers, SeedHeader);
        var grid = Find(headers, GridHeader);
        var algorithm = Find(headers, AlgorithmHeader);
        if (seed is null && grid is null && algorithm is null) return null;

        var hash = Find(headers, HashHeader);
        return new ComixScrambleHeader(
            ParseRequired(seed, SeedHeader),
            (int)ParseRequired(grid, GridHeader),
            (int)ParseRequired(algorithm, AlgorithmHeader),
            hash is null ? null : (int)ParseRequired(hash, HashHeader));
    }

    private static string? Find(IReadOnlyDictionary<string, string> headers, string key)
    {
        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(pair.Value) ? null : pair.Value.Trim();
            }
        }

        return null;
    }

    private static long ParseRequired(string? raw, string header) =>
        long.TryParse(raw, out var value)
            ? value
            : throw new InvalidDataException(
                $"Comix scramble header '{header}' is absent or not numeric.");
}
