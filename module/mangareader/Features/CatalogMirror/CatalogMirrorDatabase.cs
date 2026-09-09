using System.IO;
using Microsoft.Data.Sqlite;

namespace Module.Mangareader.Features.CatalogMirror;

// Connection factory, schema, migration version and PRAGMAs for the offline
// catalog database — and nothing else. No business queries, no traversal, no
// UI. Schema version lives in PRAGMA user_version (single source of truth,
// survives partial writes); a newer version is reported with the file
// preserved, never migrated forward blindly.
public sealed class CatalogMirrorDatabase(CatalogMirrorPaths paths)
{
    public const int SchemaVersion = 3;

    private const string CreateSchema = """
        CREATE TABLE IF NOT EXISTS catalog_state(
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS catalog_generation(
            generation_id TEXT PRIMARY KEY,
            mode TEXT NOT NULL,
            state TEXT NOT NULL,
            started_utc TEXT NOT NULL,
            completed_utc TEXT NULL,
            unique_titles INTEGER NOT NULL DEFAULT 0,
            warning_count INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS catalog_title(
            generation_id TEXT NOT NULL REFERENCES catalog_generation(generation_id),
            source_id TEXT NOT NULL,
            title_id TEXT NOT NULL,
            title_hid TEXT NOT NULL,
            title TEXT NOT NULL,
            search_text TEXT NOT NULL,
            canonical_url TEXT NOT NULL DEFAULT '',
            cover_url TEXT NULL,
            rating TEXT NOT NULL,
            type TEXT NULL,
            status TEXT NULL,
            language TEXT NULL,
            year INTEGER NULL,
            latest_chapter INTEGER NULL,
            latest_chapter_label TEXT NULL,
            synopsis TEXT NULL,
            captured_utc TEXT NOT NULL,
            updated_utc TEXT NULL,
            is_title_placeholder INTEGER NOT NULL DEFAULT 0,
            alternate_titles TEXT NOT NULL DEFAULT '[]',
            partition_key TEXT NOT NULL DEFAULT '',
            partition_index INTEGER NOT NULL DEFAULT -1,
            PRIMARY KEY (generation_id, source_id, title_id));
        CREATE INDEX IF NOT EXISTS ix_title_lookup
            ON catalog_title(generation_id, rating, type, status, language, year, latest_chapter);
        CREATE TABLE IF NOT EXISTS catalog_partition_checkpoint(
            generation_id TEXT NOT NULL,
            partition_key TEXT NOT NULL,
            partition_index INTEGER NOT NULL,
            page INTEGER NOT NULL,
            provider_total INTEGER NULL,
            staged_records INTEGER NOT NULL DEFAULT 0,
            fingerprint TEXT NULL,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (generation_id, partition_index));
        CREATE TABLE IF NOT EXISTS catalog_warning(
            generation_id TEXT NOT NULL,
            rank INTEGER NOT NULL,
            message TEXT NOT NULL,
            PRIMARY KEY (generation_id, rank));
        CREATE TABLE IF NOT EXISTS catalog_genre(
            genre_key TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            ordinal INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS catalog_title_genre(
            generation_id TEXT NOT NULL,
            source_id TEXT NOT NULL,
            title_id TEXT NOT NULL,
            genre_key TEXT NOT NULL REFERENCES catalog_genre(genre_key),
            PRIMARY KEY (generation_id, source_id, title_id, genre_key));
        CREATE INDEX IF NOT EXISTS ix_title_genre_lookup
            ON catalog_title_genre(generation_id, genre_key, source_id, title_id);
        CREATE TABLE IF NOT EXISTS catalog_genre_checkpoint(
            generation_id TEXT NOT NULL,
            genre_key TEXT NOT NULL,
            genre_index INTEGER NOT NULL,
            page INTEGER NOT NULL,
            tagged_records INTEGER NOT NULL DEFAULT 0,
            is_complete INTEGER NOT NULL DEFAULT 0,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (generation_id, genre_key));
        CREATE TABLE IF NOT EXISTS catalog_genre_state(
            generation_id TEXT PRIMARY KEY,
            state TEXT NOT NULL,
            completed_genres INTEGER NOT NULL DEFAULT 0,
            updated_utc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS catalog_title_genre_enrichment(
            generation_id TEXT NOT NULL,
            source_id TEXT NOT NULL,
            title_id TEXT NOT NULL,
            state TEXT NOT NULL,
            genre_count INTEGER NOT NULL DEFAULT 0,
            error TEXT NULL,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (generation_id, source_id, title_id));
        CREATE INDEX IF NOT EXISTS ix_title_genre_enrichment_state
            ON catalog_title_genre_enrichment(generation_id, state, source_id, title_id);
        """;

    private const string MigrateVersionOne = """
        ALTER TABLE catalog_title ADD COLUMN updated_utc TEXT NULL;
        CREATE TABLE IF NOT EXISTS catalog_genre(
            genre_key TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            ordinal INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS catalog_title_genre(
            generation_id TEXT NOT NULL,
            source_id TEXT NOT NULL,
            title_id TEXT NOT NULL,
            genre_key TEXT NOT NULL REFERENCES catalog_genre(genre_key),
            PRIMARY KEY (generation_id, source_id, title_id, genre_key));
        CREATE INDEX IF NOT EXISTS ix_title_genre_lookup
            ON catalog_title_genre(generation_id, genre_key, source_id, title_id);
        CREATE TABLE IF NOT EXISTS catalog_genre_checkpoint(
            generation_id TEXT NOT NULL,
            genre_key TEXT NOT NULL,
            genre_index INTEGER NOT NULL,
            page INTEGER NOT NULL,
            tagged_records INTEGER NOT NULL DEFAULT 0,
            is_complete INTEGER NOT NULL DEFAULT 0,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (generation_id, genre_key));
        CREATE TABLE IF NOT EXISTS catalog_genre_state(
            generation_id TEXT PRIMARY KEY,
            state TEXT NOT NULL,
            completed_genres INTEGER NOT NULL DEFAULT 0,
            updated_utc TEXT NOT NULL);
        """;

    private const string MigrateVersionTwo = """
        CREATE TABLE IF NOT EXISTS catalog_title_genre_enrichment(
            generation_id TEXT NOT NULL,
            source_id TEXT NOT NULL,
            title_id TEXT NOT NULL,
            state TEXT NOT NULL,
            genre_count INTEGER NOT NULL DEFAULT 0,
            error TEXT NULL,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (generation_id, source_id, title_id));
        CREATE INDEX IF NOT EXISTS ix_title_genre_enrichment_state
            ON catalog_title_genre_enrichment(generation_id, state, source_id, title_id);
        """;

    private readonly CatalogMirrorPaths _paths = paths;

    public string DatabasePath => _paths.DatabasePath;

    /// <summary>
    /// One short-lived connection. Foreign keys and the busy timeout are
    /// per-connection settings, so every opener applies them; WAL and the
    /// synchronous mode persist in the file from <see cref="EnsureCreated"/>.
    /// Never shared across threads.
    /// </summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _paths.DatabasePath,
                Pooling = false,
            }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }

    /// <summary>
    /// Idempotent bootstrap: creates the schema at version 1 when absent,
    /// no-ops when current, and reports a newer file with its bytes preserved.
    /// </summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.DatabasePath)!);
        using var connection = Open();
        var version = GetUserVersion(connection);
        if (version > SchemaVersion)
        {
            throw new CatalogSnapshotException(
                "Catalog database schema is newer and was preserved.");
        }

        if (version == SchemaVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = version switch
            {
                0 => CreateSchema,
                1 => MigrateVersionOne + MigrateVersionTwo,
                2 => MigrateVersionTwo,
                _ => throw new CatalogSnapshotException(
                    $"Unsupported catalog database schema version {version}.")
            };
            schema.ExecuteNonQuery();
        }

        SetUserVersion(connection, SchemaVersion, transaction);
        transaction.Commit();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }
    }

    private static long GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }

    private static void SetUserVersion(
        SqliteConnection connection,
        int version,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA user_version={version};";
        command.ExecuteNonQuery();
    }
}
