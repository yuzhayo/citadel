using System.IO;
using Microsoft.Data.Sqlite;
using Module.Mangareader.Features.CatalogMirror;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// Database boundary: schema creation, idempotence, version reporting and the
/// effective PRAGMAs. No business queries, no traversal, no UI.
/// </summary>
public sealed class CatalogMirrorDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.CatalogMirror.Tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void EnsureCreatedBuildsSchemaWithVersionAndWal()
    {
        var database = Database();

        database.EnsureCreated();
        database.EnsureCreated();

        using var connection = database.Open();
        Assert.Equal(
            new[]
            {
                "catalog_generation", "catalog_genre", "catalog_genre_checkpoint",
                "catalog_genre_state", "catalog_partition_checkpoint", "catalog_state",
                "catalog_title", "catalog_title_genre", "catalog_title_genre_enrichment",
                "catalog_warning",
            },
            TableNames(connection));
        Assert.Equal(3L, Scalar<long>(connection, "PRAGMA user_version;"));
        Assert.Equal("wal", Scalar<string>(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, Scalar<long>(connection, "PRAGMA foreign_keys;"));
    }

    [Fact]
    public void NewerSchemaIsPreservedAndReported()
    {
        var database = Database();
        database.EnsureCreated();
        var before = File.ReadAllBytes(database.DatabasePath);
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version=99;";
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<CatalogSnapshotException>(() => database.EnsureCreated());

        Assert.Contains("newer", exception.Message);
        using (var check = database.Open())
        {
            Assert.Equal(99L, Scalar<long>(check, "PRAGMA user_version;"));
        }

        Assert.NotEqual(before, File.ReadAllBytes(database.DatabasePath));
    }

    [Fact]
    public void VersionOneMigratesWithoutReplacingExistingTitles()
    {
        var database = Database();
        Directory.CreateDirectory(_root);
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE catalog_title(
                    generation_id TEXT NOT NULL, source_id TEXT NOT NULL,
                    title_id TEXT NOT NULL, title_hid TEXT NOT NULL,
                    title TEXT NOT NULL, search_text TEXT NOT NULL,
                    canonical_url TEXT NOT NULL DEFAULT '', cover_url TEXT NULL,
                    rating TEXT NOT NULL, type TEXT NULL, status TEXT NULL,
                    language TEXT NULL, year INTEGER NULL, latest_chapter INTEGER NULL,
                    latest_chapter_label TEXT NULL, synopsis TEXT NULL,
                    captured_utc TEXT NOT NULL, is_title_placeholder INTEGER NOT NULL DEFAULT 0,
                    alternate_titles TEXT NOT NULL DEFAULT '[]', partition_key TEXT NOT NULL DEFAULT '',
                    partition_index INTEGER NOT NULL DEFAULT -1,
                    PRIMARY KEY(generation_id, source_id, title_id));
                INSERT INTO catalog_title(
                    generation_id, source_id, title_id, title_hid, title, search_text,
                    rating, captured_utc)
                VALUES('keep', 'comix', '1', 'hid', 'Keep me', 'keep me', 'safe', '2026-09-08T00:00:00Z');
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }

        database.EnsureCreated();

        using var migrated = database.Open();
        Assert.Equal(3L, Scalar<long>(migrated, "PRAGMA user_version;"));
        Assert.Equal(1L, Scalar<long>(migrated, "SELECT COUNT(*) FROM catalog_title WHERE title='Keep me';"));
        Assert.Contains("updated_utc", ColumnNames(migrated, "catalog_title"));
    }

    [Fact]
    public void CorruptFileIsReportedWithBytesPreserved()
    {
        var database = Database();
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(database.DatabasePath, [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Throws<SqliteException>(() => database.EnsureCreated());

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], File.ReadAllBytes(database.DatabasePath));
    }

    [Fact]
    public void ForeignKeysAreEnforcedPerConnection()
    {
        var database = Database();
        database.EnsureCreated();

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO catalog_title (generation_id, source_id, title_id, title_hid, title, search_text, rating, captured_utc) VALUES ('ghost', 'comix', 'a', 'h-a', 'A', 'a', 'safe', '2026-09-08T00:00:00Z');";

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void ClosedConnectionsReleaseTheFile()
    {
        var database = Database();
        database.EnsureCreated();
        database.Open().Dispose();

        File.Delete(database.DatabasePath);

        Assert.False(File.Exists(database.DatabasePath));
    }

    private CatalogMirrorDatabase Database() =>
        new(new CatalogMirrorPaths(_root));

    private static string[] TableNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static string[] ColumnNames(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(1));
        return names.ToArray();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }
}
