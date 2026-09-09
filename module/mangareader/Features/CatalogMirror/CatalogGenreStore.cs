using Microsoft.Data.Sqlite;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.CatalogMirror;

/// <summary>
/// SQLite owner for deterministic, per-title genre enrichment. It only reads
/// titles already committed by Catalog sync and writes taxonomy, membership,
/// and one durable completion row for that exact title.
/// </summary>
public sealed class CatalogGenreStore(CatalogMirrorDatabase database)
{
    private readonly CatalogMirrorDatabase _database = database;

    public string? ResolveGeneration(bool requireBuilding)
    {
        _database.EnsureCreated();
        using var connection = _database.Open();
        var building = Scalar(connection,
            "SELECT value FROM catalog_state WHERE key='building_generation';");
        if (requireBuilding) return building;
        return building ?? Scalar(connection,
            "SELECT value FROM catalog_state WHERE key='active_generation';");
    }

    public void Prepare(string generationId, bool fresh)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        _database.EnsureCreated();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        CleanupOrphans(connection, transaction);
        if (fresh)
        {
            Execute(connection, transaction,
                "DELETE FROM catalog_title_genre WHERE generation_id=$gen;" +
                "DELETE FROM catalog_title_genre_enrichment WHERE generation_id=$gen;",
                ("$gen", generationId));
        }

        SetRunState(connection, transaction, generationId, "syncing");
        transaction.Commit();
    }

    public CatalogGenreWorkItem? ReadNext(string generationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        _database.EnsureCreated();
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.generation_id, t.source_id, t.title_id, t.title_hid,
                   t.title, t.canonical_url
            FROM catalog_title t
            LEFT JOIN catalog_title_genre_enrichment e
              ON e.generation_id=t.generation_id
             AND e.source_id=t.source_id
             AND e.title_id=t.title_id
            WHERE t.generation_id=$gen
              AND COALESCE(e.state, 'pending') <> 'ready'
            ORDER BY t.partition_index, t.title COLLATE NOCASE, t.title_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$gen", generationId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new CatalogGenreWorkItem(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5))
            : null;
    }

    public CatalogGenreWorkCounts ReadCounts(string generationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        _database.EnsureCreated();
        using var connection = _database.Open();
        var available = ScalarLong(connection, null,
            "SELECT COUNT(*) FROM catalog_title WHERE generation_id=$gen;",
            ("$gen", generationId));
        var processed = ScalarLong(connection, null,
            "SELECT COUNT(*) FROM catalog_title_genre_enrichment " +
            "WHERE generation_id=$gen AND state='ready';",
            ("$gen", generationId));
        var tagged = ScalarLong(connection, null,
            "SELECT COUNT(*) FROM catalog_title_genre WHERE generation_id=$gen;",
            ("$gen", generationId));
        var state = Scalar(connection,
            "SELECT state FROM catalog_generation WHERE generation_id=$gen;",
            ("$gen", generationId)) ?? "missing";
        return new CatalogGenreWorkCounts(processed, available, tagged, state);
    }

    public bool SaveTitleGenres(
        CatalogGenreWorkItem title,
        IReadOnlyList<RemoteOption> genres)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(genres);
        _database.EnsureCreated();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var exists = ScalarLong(connection, transaction,
            "SELECT COUNT(*) FROM catalog_title " +
            "WHERE generation_id=$gen AND source_id=$source AND title_id=$title;",
            ("$gen", title.GenerationId), ("$source", title.SourceId), ("$title", title.TitleId)) != 0;
        if (!exists)
        {
            transaction.Rollback();
            return false;
        }

        Execute(connection, transaction,
            "DELETE FROM catalog_title_genre " +
            "WHERE generation_id=$gen AND source_id=$source AND title_id=$title;",
            ("$gen", title.GenerationId), ("$source", title.SourceId), ("$title", title.TitleId));

        var unique = genres
            .Where(genre => !string.IsNullOrWhiteSpace(genre.Key))
            .GroupBy(genre => genre.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        foreach (var genre in unique)
        {
            Execute(connection, transaction, """
                INSERT INTO catalog_genre(genre_key, display_name, ordinal)
                VALUES(
                    $genre, $name,
                    COALESCE((SELECT MAX(ordinal) + 1 FROM catalog_genre), 0))
                ON CONFLICT(genre_key) DO UPDATE SET display_name=excluded.display_name;
                """,
                ("$genre", genre.Key), ("$name", genre.DisplayName));
            Execute(connection, transaction, """
                INSERT OR IGNORE INTO catalog_title_genre(
                    generation_id, source_id, title_id, genre_key)
                VALUES($gen, $source, $title, $genre);
                """,
                ("$gen", title.GenerationId), ("$source", title.SourceId),
                ("$title", title.TitleId), ("$genre", genre.Key));
        }

        Execute(connection, transaction, """
            INSERT INTO catalog_title_genre_enrichment(
                generation_id, source_id, title_id, state,
                genre_count, error, updated_utc)
            VALUES($gen, $source, $title, 'ready', $count, NULL, $now)
            ON CONFLICT(generation_id, source_id, title_id) DO UPDATE SET
                state='ready', genre_count=excluded.genre_count,
                error=NULL, updated_utc=excluded.updated_utc;
            """,
            ("$gen", title.GenerationId), ("$source", title.SourceId),
            ("$title", title.TitleId), ("$count", unique.Length),
            ("$now", DateTimeOffset.UtcNow.ToString("o")));
        transaction.Commit();
        return true;
    }

    public void MarkTitleError(CatalogGenreWorkItem title, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(exception);
        _database.EnsureCreated();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            INSERT INTO catalog_title_genre_enrichment(
                generation_id, source_id, title_id, state,
                genre_count, error, updated_utc)
            VALUES($gen, $source, $title, 'error', 0, $error, $now)
            ON CONFLICT(generation_id, source_id, title_id) DO UPDATE SET
                state='error', error=excluded.error, updated_utc=excluded.updated_utc;
            """,
            ("$gen", title.GenerationId), ("$source", title.SourceId),
            ("$title", title.TitleId),
            ("$error", exception.GetBaseException().Message),
            ("$now", DateTimeOffset.UtcNow.ToString("o")));
        SetRunState(connection, transaction, title.GenerationId, "error");
        transaction.Commit();
    }

    public void SetState(string generationId, string state)
    {
        _database.EnsureCreated();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        SetRunState(connection, transaction, generationId, state);
        transaction.Commit();
    }

    private static void CleanupOrphans(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction,
            "DELETE FROM catalog_title_genre WHERE generation_id NOT IN " +
            "(SELECT generation_id FROM catalog_generation);" +
            "DELETE FROM catalog_title_genre_enrichment WHERE generation_id NOT IN " +
            "(SELECT generation_id FROM catalog_generation);" +
            "DELETE FROM catalog_genre_state WHERE generation_id NOT IN " +
            "(SELECT generation_id FROM catalog_generation);");
    }

    private static void SetRunState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        string state)
    {
        Execute(connection, transaction, """
            INSERT INTO catalog_genre_state(generation_id, state, completed_genres, updated_utc)
            VALUES($gen, $state, 0, $now)
            ON CONFLICT(generation_id) DO UPDATE SET
                state=excluded.state, updated_utc=excluded.updated_utc;
            """,
            ("$gen", generationId), ("$state", state),
            ("$now", DateTimeOffset.UtcNow.ToString("o")));
    }

    private static string? Scalar(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command.ExecuteScalar() as string;
    }

    private static long ScalarLong(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }
}
