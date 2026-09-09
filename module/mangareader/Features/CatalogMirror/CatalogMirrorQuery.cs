using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.CatalogMirror;

// Parameterized local search/filter/sort/count/page over the snapshot
// database. Column names always come from the fixed sort enum below — never
// from UI text; only values are parameterized. LIKE needles escape wildcards
// so search keeps exact-substring semantics on the pre-normalized column.
// Background callers coordinate through BeginQuery/IsLatest so an older
// finished query can never replace a newer result. No WPF, no network.
public sealed class CatalogMirrorQuery
{
    private readonly CatalogMirrorDatabase _database;
    private readonly string _generationId;
    private long _latestGeneration;

    public CatalogMirrorQuery(CatalogMirrorDatabase database, string generationId)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        _database = database;
        _generationId = generationId;
        DistinctTypes = Distinct("type");
        DistinctStatuses = Distinct("status");
        DistinctLanguages = Distinct("language");
        DistinctGenres = Genres();
        IsGenreEnrichmentComplete = GenreEnrichmentComplete();
    }

    /// <summary>Snapshot-driven option lists for the local filter panel.</summary>
    public IReadOnlyList<string> DistinctTypes { get; }

    /// <inheritdoc cref="DistinctTypes" />
    public IReadOnlyList<string> DistinctStatuses { get; }

    /// <inheritdoc cref="DistinctTypes" />
    public IReadOnlyList<string> DistinctLanguages { get; }

    public IReadOnlyList<CatalogGenreOption> DistinctGenres { get; }

    public bool IsGenreEnrichmentComplete { get; }

    /// <summary>Claims the generation of one outgoing background query.</summary>
    public long BeginQuery() => Interlocked.Increment(ref _latestGeneration);

    /// <summary>True only when no newer query started since this generation.</summary>
    public bool IsLatest(long generation) =>
        Volatile.Read(ref _latestGeneration) == generation;

    public CatalogMirrorResultPage Execute(CatalogMirrorQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Filter);

        var filter = request.Filter;
        var conditions = new List<string> { "generation_id = $gen" };
        var parameters = new List<(string Name, object? Value)> { ("$gen", _generationId) };

        // Case-insensitive match like the retired in-memory matcher. Both sides
        // are ASCII-folded: provider option values are ASCII, and titles match
        // byte-exact on the pre-normalized column (see LIKE below).
        if (filter.Ratings.Count != 0)
        {
            conditions.Add(InList("lower(rating)",
                filter.Ratings.Select(rating => rating.ToString().ToLowerInvariant()),
                parameters));
        }

        if (filter.Types.Count != 0)
        {
            conditions.Add(InList("lower(type)",
                filter.Types.Select(Normalize), parameters));
        }

        if (filter.Statuses.Count != 0)
        {
            conditions.Add(InList("lower(status)",
                filter.Statuses.Select(Normalize), parameters));
        }

        if (filter.Languages.Count != 0)
        {
            conditions.Add(InList("lower(language)",
                filter.Languages.Select(Normalize), parameters));
        }

        if (filter.Genres.Count != 0)
        {
            conditions.Add(
                "EXISTS (SELECT 1 FROM catalog_title_genre tg " +
                "WHERE tg.generation_id = catalog_title.generation_id " +
                "AND tg.source_id = catalog_title.source_id " +
                "AND tg.title_id = catalog_title.title_id AND " +
                InList("tg.genre_key", filter.Genres, parameters) + ")");
        }

        if (filter.YearFrom is not null)
        {
            conditions.Add("year >= $yearFrom");
            parameters.Add(("$yearFrom", filter.YearFrom));
        }

        if (filter.YearTo is not null)
        {
            conditions.Add("year <= $yearTo");
            parameters.Add(("$yearTo", filter.YearTo));
        }

        if (filter.MinLatestChapter is not null)
        {
            conditions.Add("latest_chapter >= $minChapter");
            parameters.Add(("$minChapter", filter.MinLatestChapter));
        }

        var needle = request.Search is null ? string.Empty : Normalize(request.Search);
        if (needle.Length != 0)
        {
            conditions.Add("search_text LIKE '%' || $needle || '%' ESCAPE '\\'");
            parameters.Add(("$needle", needle));
            parameters.Add(("$needleLike", EscapeLike(needle)));
            conditions[^1] = "search_text LIKE '%' || $needleLike || '%' ESCAPE '\\'";
        }

        // A null field never satisfies a set filter or range: same rule as the
        // retired in-memory matcher (SQL three-valued logic does this natively
        // for =, IN and comparisons; LIKE on null is likewise not true).
        var where = string.Join(" AND ", conditions);
        var total = Count(where, parameters);
        var pageSize = CatalogMirrorPaging.DefaultPageSize;
        var totalPages = Math.Max(1, (int)((total + pageSize - 1) / pageSize));
        var page = Math.Clamp(request.Page < 1 ? 1 : request.Page, 1, totalPages);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT source_id, title_id, title_hid, title, " +
            "canonical_url, cover_url, rating, type, status, language, year, " +
            "latest_chapter, latest_chapter_label, synopsis, captured_utc, " +
            "is_title_placeholder, alternate_titles, updated_utc " +
            $"FROM catalog_title WHERE {where} {OrderBy(request.Sort, needle.Length != 0)} " +
            $"LIMIT {pageSize} OFFSET {(page - 1) * pageSize};";
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var items = new List<CatalogSnapshotItem>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                items.Add(new CatalogSnapshotItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(4),
                    reader.GetString(3),
                    ReadAlternates(reader.GetString(16)),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(11) ? null : reader.GetInt64(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : (int)reader.GetInt64(10),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    DateTimeOffset.Parse(reader.GetString(14)),
                    reader.GetInt64(15) != 0,
                    reader.IsDBNull(17) ? null : DateTimeOffset.Parse(reader.GetString(17))));
            }
        }

        return new CatalogMirrorResultPage(items, (int)Math.Min(total, int.MaxValue), page, totalPages);
    }

    private long Count(string where, List<(string Name, object? Value)> parameters)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM catalog_title WHERE {where};";
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return Convert.ToInt64(command.ExecuteScalar());
    }

    private IReadOnlyList<string> Distinct(string column)
    {
        var values = new List<string>();
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT DISTINCT {column} FROM catalog_title " +
            "WHERE generation_id = $gen AND " + column + " IS NOT NULL " +
            $"ORDER BY {column};";
        command.Parameters.AddWithValue("$gen", _generationId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private IReadOnlyList<CatalogGenreOption> Genres()
    {
        var values = new List<CatalogGenreOption>();
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.genre_key, g.display_name
            FROM catalog_genre g
            JOIN catalog_title_genre tg ON tg.genre_key = g.genre_key
            WHERE tg.generation_id = $gen
            GROUP BY g.genre_key, g.display_name, g.ordinal
            ORDER BY g.ordinal, g.display_name;
            """;
        command.Parameters.AddWithValue("$gen", _generationId);
        using var reader = command.ExecuteReader();
        while (reader.Read()) values.Add(new CatalogGenreOption(reader.GetString(0), reader.GetString(1)));
        return values;
    }

    private bool GenreEnrichmentComplete()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT state FROM catalog_genre_state WHERE generation_id=$gen;";
        command.Parameters.AddWithValue("$gen", _generationId);
        return string.Equals(command.ExecuteScalar() as string, "ready", StringComparison.Ordinal);
    }

    private static string InList(
        string column,
        IEnumerable<string> values,
        List<(string Name, object? Value)> parameters)
    {
        var names = new List<string>();
        foreach (var value in values)
        {
            var name = "$in" + parameters.Count;
            names.Add(name);
            parameters.Add((name, value));
        }

        // An empty set can never match; the caller only adds non-empty sets.
        return column + " IN (" + string.Join(", ", names) + ")";
    }

    private static string OrderBy(CatalogTitleSort sort, bool hasSearch)
    {
        if (hasSearch)
        {
            return "ORDER BY CASE " +
                "WHEN lower(title) = $needle THEN 0 " +
                "WHEN lower(title) LIKE $needle || '%' THEN 1 " +
                "WHEN lower(title) LIKE '%' || $needle || '%' THEN 2 " +
                "ELSE 3 END, title COLLATE NOCASE, title_id";
        }

        return sort switch
    {
        CatalogTitleSort.LatestUpdate =>
            "ORDER BY updated_utc DESC NULLS LAST, title COLLATE NOCASE, title_id",
        CatalogTitleSort.TitleDesc =>
            "ORDER BY title COLLATE NOCASE DESC, title_id",
        CatalogTitleSort.YearNewest =>
            "ORDER BY year DESC, title COLLATE NOCASE, title_id",
        CatalogTitleSort.YearOldest =>
            "ORDER BY year ASC NULLS LAST, title COLLATE NOCASE, title_id",
        CatalogTitleSort.LatestChapterHighest =>
            "ORDER BY latest_chapter DESC, title COLLATE NOCASE, title_id",
        CatalogTitleSort.LatestChapterLowest =>
            "ORDER BY latest_chapter ASC NULLS LAST, title COLLATE NOCASE, title_id",
        _ =>
            "ORDER BY title COLLATE NOCASE, title_id",
    };
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static string EscapeLike(string needle)
    {
        var builder = new StringBuilder(needle.Length);
        foreach (var character in needle)
        {
            if (character is '%' or '_' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> ReadAlternates(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
