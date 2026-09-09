using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.CatalogMirror;

// Whole snapshot persistence lifecycle on SQLite: staging appends, durable
// checkpoints, recovery, bounds, dedupe/compaction, atomic activation and
// active/previous retention (plan 5.2-5.4, SQLite track P3). The public API is
// identical to the retired JSONL store, so traversal, query, detail and all
// callers are untouched. Staging is the building generation's rows; activation
// flips one state pointer inside a transaction. It never calls a provider,
// runs traversal/Stop state, queries/filters, enrichment, cover, queue or UI.
public sealed class CatalogSnapshotStore
{
    private static readonly JsonSerializerOptions JsonLines = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly CatalogMirrorPaths _paths;
    private readonly CatalogMirrorDatabase _database;

    public CatalogSnapshotStore(CatalogMirrorPaths paths)
    {
        _paths = paths;
        _database = new CatalogMirrorDatabase(paths);
    }

    /// <summary>Database backing this store, for query owners. Same instance.</summary>
    public CatalogMirrorDatabase Database => _database;

    /// <summary>
    /// Validates and commits one provider page, then checkpoints. Validation
    /// runs before any write, so a rejected page changes nothing. One page is
    /// one transaction: commit and checkpoint advance together or not at all.
    /// </summary>
    public async Task<CatalogSnapshotCheckpoint> AppendPageAsync(
        CatalogSnapshotPartition partition,
        int partitionIndex,
        CatalogSnapshotPage page,
        CancellationToken cancellationToken,
        bool isRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition.Key);
        if (partitionIndex < 0)
        {
            throw new CatalogSnapshotException("Partition index is negative.");
        }

        if (page.Page < 1)
        {
            throw new CatalogSnapshotException("Snapshot page is one-based.");
        }

        if (page.Page > CatalogSnapshotLimits.MaxPagesPerPartition)
        {
            throw new CatalogSnapshotException(
                $"Partition '{partition.Key}' exceeded {CatalogSnapshotLimits.MaxPagesPerPartition} pages.");
        }

        if (page.Items.Count == 0 && page.HasMore)
        {
            throw new CatalogSnapshotException(
                $"Partition '{partition.Key}' page {page.Page} reports HasMore with no items.");
        }

        foreach (var item in page.Items)
        {
            ValidateItem(item);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(item, JsonLines);
            if (bytes.Length > CatalogSnapshotLimits.MaxRecordBytes)
            {
                throw new CatalogSnapshotException(
                    $"Snapshot record exceeds {CatalogSnapshotLimits.MaxRecordBytes} bytes.");
            }
        }

        _database.EnsureCreated();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            var generation = await GetBuildingGenerationAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            string generationId;
            if (generation is null)
            {
                if (partitionIndex != 0 || page.Page != 1)
                {
                    throw new CatalogSnapshotException(
                        "Snapshot staging is empty; the first commit must be partition 0 page 1.");
                }

                generationId = CatalogMirrorPaths.NewSnapshotId();
                InsertGeneration(connection, transaction, generationId,
                    isRefresh ? "refresh" : "full", "syncing", DateTimeOffset.UtcNow);
                SetBuildingGeneration(connection, transaction, generationId);
            }
            else
            {
                generationId = generation;
            }

            var max = await MaxCheckpointAsync(connection, transaction, generationId, cancellationToken)
                .ConfigureAwait(false);
            if (max is null)
            {
                if (partitionIndex != 0 || page.Page != 1)
                {
                    throw new CatalogSnapshotException(
                        "Snapshot staging is empty; the first commit must be partition 0 page 1.");
                }
            }
            else if (partitionIndex < max.Value.PartitionIndex)
            {
                throw new CatalogSnapshotException("Snapshot traversal moved to an earlier partition.");
            }
            else if (partitionIndex == max.Value.PartitionIndex)
            {
                if (page.Page != max.Value.Page + 1)
                {
                    throw new CatalogSnapshotException(
                        $"Partition '{partition.Key}' expected page {max.Value.Page + 1}, got {page.Page}.");
                }

                var fingerprint = Fingerprint(page.Items);
                if (max.Value.Fingerprint is not null
                    && string.Equals(fingerprint, max.Value.Fingerprint, StringComparison.Ordinal))
                {
                    throw new CatalogSnapshotException(
                        $"Partition '{partition.Key}' repeated the page fingerprint: pagination loop.");
                }
            }
            else if (page.Page != 1)
            {
                throw new CatalogSnapshotException(
                    $"Partition '{partition.Key}' must start at page 1.");
            }

            var warnings = await UpsertPageAsync(
                    connection, transaction, generationId,
                    partition, partitionIndex, page, cancellationToken)
                .ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var pageFingerprint = Fingerprint(page.Items);
            UpsertCheckpoint(connection, transaction, generationId,
                partition.Key, partitionIndex, page.Page, page.ProviderTotal,
                page.Items.Count,
                pageFingerprint,
                now);
            transaction.Commit();

            var staged = await StagedTotalAsync(generationId, cancellationToken).ConfigureAwait(false);
            return new CatalogSnapshotCheckpoint(
                CatalogSnapshotSchema.Version,
                generationId,
                partitionIndex,
                partition.Key,
                page.Page,
                staged,
                pageFingerprint,
                now,
                await GenerationIsRefreshAsync(generationId, cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch (Exception)
            {
                // Best effort: dispose rolls back as well.
            }

            throw;
        }
    }

    /// <summary>
    /// Loads the durable checkpoint. Transactions make torn writes impossible,
    /// so there is nothing to trim: either the page committed with its
    /// checkpoint or neither did. Returns null when no traversal is staged.
    /// </summary>
    public Task<CatalogSnapshotCheckpoint?> LoadCheckpointAsync(
        CancellationToken cancellationToken = default) =>
        LoadCheckpointCoreAsync(cancellationToken);

    /// <summary>
    /// Discards an interrupted staging run: the building generation subtree.
    /// Active snapshots are never touched.
    /// </summary>
    public Task ClearStagingAsync(CancellationToken cancellationToken = default)
    {
        _database.EnsureCreated();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            ClearBuildingLocked(connection, transaction);
            transaction.Commit();
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch (Exception)
            {
            }

            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static void ClearBuildingLocked(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var building = ScalarString(connection, transaction,
            "SELECT value FROM catalog_state WHERE key='building_generation';");
        if (building is not null)
        {
            DeleteGeneration(connection, transaction, building);
        }

        Execute(connection, transaction, "DELETE FROM catalog_state WHERE key='building_generation';");
    }

    /// <summary>
    /// Seeds a fresh refresh staging from the active snapshot with one
    /// INSERT…SELECT plus its checkpoint row: no object crosses into memory.
    /// Returns the seed checkpoint plus the seeded identity keys, so the
    /// caller never reads the active titles twice.
    /// </summary>
    public async Task<(CatalogSnapshotCheckpoint Checkpoint, HashSet<string> IdentityKeys)>
        SeedStagingFromActiveAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.DatabasePath))
        {
            throw new CatalogSnapshotException("There is no active snapshot to refresh.");
        }

        _database.EnsureCreated();
        using var connection = _database.Open();
        var active = ScalarString(connection, null,
            "SELECT value FROM catalog_state WHERE key='active_generation';");
        if (active is null)
        {
            throw new CatalogSnapshotException("There is no active snapshot to refresh.");
        }

        var stagingId = CatalogMirrorPaths.NewSnapshotId();
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var transaction = connection.BeginTransaction();
        try
        {
            ClearBuildingLocked(connection, transaction);
            InsertGeneration(connection, transaction, stagingId, "refresh", "syncing", DateTimeOffset.UtcNow);
            Execute(connection, transaction,
                """
                INSERT INTO catalog_title (
                    generation_id, source_id, title_id, title_hid, title, search_text,
                    canonical_url, cover_url, rating, type, status, language, year,
                    latest_chapter, latest_chapter_label, synopsis, captured_utc, updated_utc,
                    is_title_placeholder, alternate_titles, partition_key, partition_index)
                SELECT $gen, source_id, title_id, title_hid, title, search_text,
                    canonical_url, cover_url, rating, type, status, language, year,
                    latest_chapter, latest_chapter_label, synopsis, captured_utc, updated_utc,
                    is_title_placeholder, alternate_titles, 'seed', -1
                FROM catalog_title
                WHERE generation_id=$active;
                """,
                ("$gen", stagingId),
                ("$active", active));
            var staged = ScalarLong(connection, transaction,
                "SELECT COUNT(*) FROM catalog_title WHERE generation_id=$gen;",
                ("$gen", stagingId));
            UpsertCheckpoint(connection, transaction, stagingId,
                SeedPartitionKey, SeedPartitionIndex, 0, null, staged, null,
                DateTimeOffset.UtcNow);
            SetBuildingGeneration(connection, transaction, stagingId);
            transaction.Commit();

            var keys = new HashSet<string>(StringComparer.Ordinal);
            using (var keysCommand = connection.CreateCommand())
            {
                keysCommand.CommandText = """
                    SELECT source_id, title_id FROM catalog_title WHERE generation_id=$gen;
                    """;
                keysCommand.Parameters.AddWithValue("$gen", stagingId);
                using var reader = keysCommand.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    keys.Add(reader.GetString(0) + "\0" + reader.GetString(1));
                }
            }

            return (new CatalogSnapshotCheckpoint(
                CatalogSnapshotSchema.Version,
                stagingId,
                SeedPartitionIndex,
                SeedPartitionKey,
                0,
                staged,
                null,
                DateTimeOffset.UtcNow,
                true), keys);
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch (Exception)
            {
            }

            throw;
        }
    }

    /// <summary>
    /// Streams identity keys of the building staging generation for overlap
    /// detection after an interrupted refresh. Only key strings accumulate.
    /// (Seeded rows already carry the baseline, so one generation suffices.)
    /// </summary>
    public Task<HashSet<string>> LoadRefreshBaselineAsync(
        string stagingSnapshotId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingSnapshotId);
        _database.EnsureCreated();
        using var connection = _database.Open();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_id, title_id FROM catalog_title WHERE generation_id=$gen;
            """;
        command.Parameters.AddWithValue("$gen", stagingSnapshotId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            keys.Add(reader.GetString(0) + "\0" + reader.GetString(1));
        }

        return Task.FromResult(keys);
    }

    /// <summary>
    /// Committed per-partition progress of one staging generation, ordered by
    /// partition. Backs the progress bar; cheap indexed reads, no titles.
    /// </summary>
    public Task<IReadOnlyList<CatalogPartitionProgress>> GetPartitionProgressAsync(
        string stagingSnapshotId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingSnapshotId);
        _database.EnsureCreated();
        using var connection = _database.Open();
        var rows = new List<CatalogPartitionProgress>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT partition_key, partition_index, page, staged_records, provider_total
            FROM catalog_partition_checkpoint
            WHERE generation_id=$gen
            ORDER BY partition_index;
            """;
        command.Parameters.AddWithValue("$gen", stagingSnapshotId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new CatalogPartitionProgress(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }

        return Task.FromResult<IReadOnlyList<CatalogPartitionProgress>>(rows);
    }

    /// <summary>
    /// Records that one partition traversal finished: the checkpoint advances
    /// to the next partition at page zero, so a later Resume never re-enters
    /// a completed partition. Atomic like every checkpoint write.
    /// </summary>
    public async Task<CatalogSnapshotCheckpoint> CompletePartitionAsync(
        int partitionIndex,
        string partitionKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        _database.EnsureCreated();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            var building = ScalarString(connection, transaction,
                "SELECT value FROM catalog_state WHERE key='building_generation';");
            if (building is null)
            {
                throw new CatalogSnapshotException("Snapshot checkpoint is stale; reload before activation.");
            }

            var max = await MaxCheckpointAsync(connection, transaction, building, cancellationToken)
                .ConfigureAwait(false);
            if (max is null || max.Value.PartitionIndex != partitionIndex)
            {
                throw new CatalogSnapshotException("Snapshot checkpoint is stale; reload before activation.");
            }

            UpsertCheckpoint(connection, transaction, building,
                partitionKey, partitionIndex + 1, 0, null, 0, null,
                DateTimeOffset.UtcNow);
            transaction.Commit();
            return await LoadCheckpointCoreAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new CatalogSnapshotException("Snapshot checkpoint is missing.");
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch (Exception)
            {
            }

            throw;
        }
    }

    /// <summary>
    /// Terminal activation in the locked order: counts and guards first (no
    /// mutation), then one transaction flips the active pointer, records the
    /// watermark and retains active plus one previous snapshot. Any failure
    /// preserves the previous active snapshot.
    /// </summary>
    public async Task<CatalogActivationResult> ActivateAsync(
        CatalogSnapshotCheckpoint checkpoint,
        CancellationToken cancellationToken,
        bool isRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        _database.EnsureCreated();
        using var connection = _database.Open();
        var current = await LoadCheckpointFromConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (current is null
            || current.StagingSnapshotId != checkpoint.StagingSnapshotId
            || current.PartitionIndex != checkpoint.PartitionIndex
            || current.Page != checkpoint.Page)
        {
            throw new CatalogSnapshotException("Snapshot checkpoint is stale; reload before activation.");
        }

        var unique = ScalarLong(connection, null,
            "SELECT COUNT(*) FROM catalog_title WHERE generation_id=$gen;",
            ("$gen", checkpoint.StagingSnapshotId));
        if (unique > CatalogSnapshotLimits.MaxUniqueTitles)
        {
            throw new CatalogSnapshotException(
                $"Snapshot exceeds {CatalogSnapshotLimits.MaxUniqueTitles} unique titles.");
        }

        var placeholders = ScalarLong(connection, null,
            "SELECT COUNT(*) FROM catalog_title WHERE generation_id=$gen AND is_title_placeholder=1;",
            ("$gen", checkpoint.StagingSnapshotId));
        var staged = ScalarLong(connection, null,
            "SELECT COALESCE(SUM(staged_records),0) FROM catalog_partition_checkpoint WHERE generation_id=$gen;",
            ("$gen", checkpoint.StagingSnapshotId));
        var warnings = ReadWarnings(connection, null, checkpoint.StagingSnapshotId);

        using var transaction = connection.BeginTransaction();
        try
        {
            var previous = ScalarString(connection, transaction,
                "SELECT value FROM catalog_state WHERE key='active_generation';");
            Execute(connection, transaction,
                "INSERT INTO catalog_state(key, value) VALUES('active_generation', $gen) " +
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                ("$gen", checkpoint.StagingSnapshotId));
            Execute(connection, transaction,
                "DELETE FROM catalog_state WHERE key='building_generation';");
            Execute(connection, transaction,
                "UPDATE catalog_generation SET state='ready', completed_utc=$now, unique_titles=$unique " +
                "WHERE generation_id=$gen;",
                ("$now", DateTimeOffset.UtcNow.ToString("o")),
                ("$unique", unique),
                ("$gen", checkpoint.StagingSnapshotId));
            if (isRefresh)
            {
                Execute(connection, transaction,
                    "INSERT INTO catalog_state(key, value) VALUES('last_delta_utc', $now) " +
                    "ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                    ("$now", DateTimeOffset.UtcNow.ToString("o")));
            }
            else
            {
                Execute(connection, transaction,
                    "DELETE FROM catalog_state WHERE key='last_delta_utc';");
            }

            var keep = new HashSet<string>(StringComparer.Ordinal) { checkpoint.StagingSnapshotId };
            if (previous is not null)
            {
                keep.Add(previous);
            }

            foreach (var generation in ListGenerations(connection, transaction))
            {
                if (!keep.Contains(generation))
                {
                    DeleteGeneration(connection, transaction, generation);
                }
            }

            transaction.Commit();
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch (Exception)
            {
            }

            throw;
        }

        return new CatalogActivationResult(
            checkpoint.StagingSnapshotId,
            unique,
            staged - unique,
            placeholders,
            warnings,
            []);
    }

    /// <summary>Active manifest without loading titles, or null when no sync ever activated.</summary>
    public Task<CatalogSnapshotManifest?> TryLoadManifestAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.DatabasePath))
        {
            return Task.FromResult<CatalogSnapshotManifest?>(null);
        }

        _database.EnsureCreated();
        using var connection = _database.Open();
        return Task.FromResult(TryLoadManifestCore(connection));
    }

    /// <summary>
    /// Active manifest plus all its titles, or null when no sync ever
    /// activated. Runs on the caller's thread; the view marshals to the
    /// background.
    /// </summary>
    public async Task<(CatalogSnapshotManifest Manifest, IReadOnlyList<CatalogSnapshotItem> Items)?>
        TryLoadActiveAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.DatabasePath))
        {
            return null;
        }

        _database.EnsureCreated();
        using var connection = _database.Open();
        var manifest = TryLoadManifestCore(connection);
        if (manifest is null)
        {
            return null;
        }

        var items = new List<CatalogSnapshotItem>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT source_id, title_id, title_hid, title,
                    canonical_url, cover_url, rating, type, status, language, year,
                    latest_chapter, latest_chapter_label, synopsis, captured_utc, updated_utc,
                    is_title_placeholder, alternate_titles
                FROM catalog_title
                WHERE generation_id=$gen
                ORDER BY rowid;
                """;
            command.Parameters.AddWithValue("$gen", manifest.ActiveSnapshotId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(new CatalogSnapshotItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(4),
                    reader.GetString(3),
                    ReadAlternates(reader.GetString(17)),
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
                    reader.GetInt64(16) != 0,
                    reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15))));
            }
        }

        foreach (var item in items)
        {
            ValidateItem(item);
        }

        return (manifest, items);
    }

    private static IReadOnlyList<string> ReadAlternates(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonLines) ?? [];
        }
        catch (JsonException exception)
        {
            throw new CatalogSnapshotException(
                "Active snapshot holds malformed alternate titles.", exception);
        }
    }

    private static void ValidateItem(CatalogSnapshotItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.SourceId)
            || string.IsNullOrWhiteSpace(item.TitleId)
            || string.IsNullOrWhiteSpace(item.TitleHid))
        {
            throw new CatalogSnapshotException("Snapshot item has an invalid identity.");
        }

        if (string.IsNullOrWhiteSpace(item.Title))
        {
            if (!item.IsTitlePlaceholder)
            {
                throw new CatalogSnapshotException("Snapshot item has no title.");
            }

            throw new CatalogSnapshotException("Snapshot placeholder item has no title text.");
        }
    }

    private static string Fingerprint(IReadOnlyList<CatalogSnapshotItem> items)
    {
        var joined = string.Join("\n", items.Select(item =>
            item.SourceId + "\0" + item.TitleId + "\0" + item.TitleHid));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    private async Task<CatalogSnapshotCheckpoint?> LoadCheckpointCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.DatabasePath))
        {
            return null;
        }

        _database.EnsureCreated();
        using var connection = _database.Open();
        return await LoadCheckpointFromConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CatalogSnapshotCheckpoint?> LoadCheckpointFromConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var building = ScalarString(connection, null,
            "SELECT value FROM catalog_state WHERE key='building_generation';");
        if (building is null)
        {
            return null;
        }

        var max = await MaxCheckpointAsync(connection, null, building, cancellationToken)
            .ConfigureAwait(false);
        if (max is null)
        {
            return null;
        }

        var staged = ScalarLong(connection, null,
            "SELECT COALESCE(SUM(staged_records),0) FROM catalog_partition_checkpoint WHERE generation_id=$gen;",
            ("$gen", building));
        var mode = ScalarString(connection, null,
            "SELECT mode FROM catalog_generation WHERE generation_id=$gen;",
            ("$gen", building));
        return new CatalogSnapshotCheckpoint(
            CatalogSnapshotSchema.Version,
            building,
            max.Value.PartitionIndex,
            max.Value.PartitionKey,
            max.Value.Page,
            staged,
            max.Value.Fingerprint,
            DateTimeOffset.Parse(max.Value.UpdatedUtc),
            string.Equals(mode, "refresh", StringComparison.Ordinal));
    }

    private static async Task<CheckpointRow?> MaxCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string generationId,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT partition_key, partition_index, page, staged_records, fingerprint, updated_utc
            FROM catalog_partition_checkpoint
            WHERE generation_id=$gen
            ORDER BY partition_index DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$gen", generationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new CheckpointRow(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5));
    }

    private readonly record struct CheckpointRow(
        string PartitionKey,
        int PartitionIndex,
        int Page,
        long StagedRecords,
        string? Fingerprint,
        string UpdatedUtc);

    private async Task<string?> GetBuildingGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return ScalarString(connection, transaction,
            "SELECT value FROM catalog_state WHERE key='building_generation';");
    }

    private async Task<bool> GenerationIsRefreshAsync(
        string generationId,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        _database.EnsureCreated();
        using var connection = _database.Open();
        return string.Equals(
            ScalarString(connection, null,
                "SELECT mode FROM catalog_generation WHERE generation_id=$gen;",
                ("$gen", generationId)),
            "refresh",
            StringComparison.Ordinal);
    }

    private async Task<long> StagedTotalAsync(
        string generationId,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        _database.EnsureCreated();
        using var connection = _database.Open();
        return ScalarLong(connection, null,
            "SELECT COALESCE(SUM(staged_records),0) FROM catalog_partition_checkpoint WHERE generation_id=$gen;",
            ("$gen", generationId));
    }

    /// <summary>
    /// Upserts one page with last-wins dedupe plus drift warnings, all inside
    /// the caller's page transaction. Returns the drift warnings of this page.
    /// </summary>
    private static async Task<List<string>> UpsertPageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        CatalogSnapshotPartition partition,
        int partitionIndex,
        CatalogSnapshotPage page,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var existing = new Dictionary<string, (string Key, int Index)>(StringComparer.Ordinal);
        const int lookupChunk = 200;
        for (var offset = 0; offset < page.Items.Count; offset += lookupChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var found in LookupIdentities(
                connection, transaction, generationId, page.Items, offset,
                Math.Min(lookupChunk, page.Items.Count - offset)))
            {
                existing[found.Key] = (found.PartitionKey, found.PartitionIndex);
            }
        }

        foreach (var item in page.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = item.SourceId + "\0" + item.TitleId;
            if (existing.TryGetValue(key, out var prior)
                && prior.Index != partitionIndex
                && prior.Index != SeedPartitionIndex
                && partitionIndex != SeedPartitionIndex)
            {
                warnings.Add(
                    $"rating drift: '{item.SourceId}/{item.TitleId}' " +
                    $"first seen in '{prior.Key}', kept from '{partition.Key}'.");
            }

            UpsertTitle(connection, transaction, generationId,
                partition.Key, partitionIndex, item);
            existing[key] = (partition.Key, partitionIndex);
        }

        PersistWarnings(connection, transaction, generationId, warnings);
        await Task.CompletedTask.ConfigureAwait(false);
        return warnings;
    }

    private static List<(string Key, string PartitionKey, int PartitionIndex)> LookupIdentities(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        IReadOnlyList<CatalogSnapshotItem> items,
        int offset,
        int count)
    {
        var found = new List<(string Key, string PartitionKey, int PartitionIndex)>();
        using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        var terms = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var item = items[offset + index];
            var source = "$s" + index;
            var title = "$t" + index;
            terms.Add($"(source_id = {source} AND title_id = {title})");
            lookup.Parameters.AddWithValue(source, item.SourceId);
            lookup.Parameters.AddWithValue(title, item.TitleId);
        }

        lookup.CommandText =
            "SELECT source_id, title_id, partition_key, partition_index FROM catalog_title " +
            $"WHERE generation_id=$gen AND ({string.Join(" OR ", terms)});";
        lookup.Parameters.AddWithValue("$gen", generationId);
        using var reader = lookup.ExecuteReader();
        while (reader.Read())
        {
            found.Add((
                reader.GetString(0) + "\0" + reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return found;
    }

    private static void UpsertTitle(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        string partitionKey,
        int partitionIndex,
        CatalogSnapshotItem item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO catalog_title (
                generation_id, source_id, title_id, title_hid, title, search_text,
                canonical_url, cover_url, rating, type, status, language, year,
                latest_chapter, latest_chapter_label, synopsis, captured_utc, updated_utc,
                is_title_placeholder, alternate_titles, partition_key, partition_index)
            VALUES (
                $generation, $source, $titleId, $hid, $title, $search,
                $canonical, $cover, $rating, $type, $status, $language, $year,
                $latest, $latestLabel, $synopsis, $captured, $updated,
                $placeholder, $alternates, $partitionKey, $partitionIndex)
            ON CONFLICT (generation_id, source_id, title_id)
            DO UPDATE SET
                title_hid = excluded.title_hid, title = excluded.title,
                search_text = excluded.search_text, canonical_url = excluded.canonical_url,
                cover_url = excluded.cover_url, rating = excluded.rating,
                type = excluded.type, status = excluded.status, language = excluded.language,
                year = excluded.year, latest_chapter = excluded.latest_chapter,
                latest_chapter_label = excluded.latest_chapter_label,
                synopsis = excluded.synopsis, captured_utc = excluded.captured_utc,
                updated_utc = excluded.updated_utc,
                is_title_placeholder = excluded.is_title_placeholder,
                alternate_titles = excluded.alternate_titles,
                partition_key = excluded.partition_key, partition_index = excluded.partition_index;
            """;
        command.Parameters.AddWithValue("$generation", generationId);
        command.Parameters.AddWithValue("$source", item.SourceId);
        command.Parameters.AddWithValue("$titleId", item.TitleId);
        command.Parameters.AddWithValue("$hid", item.TitleHid);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$search",
            CatalogSnapshotText.NormalizeSearchText(item.Title, item.AlternateTitles));
        command.Parameters.AddWithValue("$canonical", item.CanonicalUrl);
        command.Parameters.AddWithValue("$cover", (object?)item.CoverUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$rating", item.Rating);
        command.Parameters.AddWithValue("$type", (object?)item.Type ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (object?)item.Status ?? DBNull.Value);
        command.Parameters.AddWithValue("$language", (object?)item.Language ?? DBNull.Value);
        command.Parameters.AddWithValue("$year", (object?)item.Year ?? DBNull.Value);
        command.Parameters.AddWithValue("$latest", (object?)item.LatestChapterValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$latestLabel", (object?)item.LatestChapterLabel ?? DBNull.Value);
        command.Parameters.AddWithValue("$synopsis", (object?)item.Synopsis ?? DBNull.Value);
        command.Parameters.AddWithValue("$captured", item.CapturedAtUtc.ToString("o"));
        command.Parameters.AddWithValue("$updated", (object?)item.UpdatedAtUtc?.ToString("o") ?? DBNull.Value);
        command.Parameters.AddWithValue("$placeholder", item.IsTitlePlaceholder ? 1 : 0);
        command.Parameters.AddWithValue("$alternates",
            JsonSerializer.Serialize(item.AlternateTitles, JsonLines));
        command.Parameters.AddWithValue("$partitionKey", partitionKey);
        command.Parameters.AddWithValue("$partitionIndex", partitionIndex);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Merges new drift warnings into the generation set, capped with one
    /// summary line. Rewritten whole per page: the set stays tiny by design.
    /// </summary>
    private static void PersistWarnings(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        List<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        var stored = new List<string>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT message FROM catalog_warning WHERE generation_id=$gen ORDER BY rank;
                """;
            read.Parameters.AddWithValue("$gen", generationId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var message = reader.GetString(0);
                if (!message.EndsWith(" more rating drifts.", StringComparison.Ordinal))
                {
                    stored.Add(message);
                }
            }
        }

        stored.AddRange(warnings);
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM catalog_warning WHERE generation_id=$gen;";
            clear.Parameters.AddWithValue("$gen", generationId);
            clear.ExecuteNonQuery();
        }

        var shown = stored.Take(MaxManifestWarnings).ToArray();
        var rank = 0;
        foreach (var message in shown)
        {
            InsertWarning(connection, transaction, generationId, rank, message);
            rank++;
        }

        if (stored.Count > MaxManifestWarnings)
        {
            InsertWarning(connection, transaction, generationId, rank,
                $"and {stored.Count - MaxManifestWarnings} more rating drifts.");
        }
    }

    private static void InsertWarning(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        long rank,
        string message)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO catalog_warning (generation_id, rank, message)
            VALUES ($generation, $rank, $message);
            """;
        command.Parameters.AddWithValue("$generation", generationId);
        command.Parameters.AddWithValue("$rank", rank);
        command.Parameters.AddWithValue("$message", message);
        command.ExecuteNonQuery();
    }

    private static void UpsertCheckpoint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        string partitionKey,
        int partitionIndex,
        int page,
        long? providerTotal,
        long stagedDelta,
        string? fingerprint,
        DateTimeOffset updatedUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO catalog_partition_checkpoint (
                generation_id, partition_key, partition_index, page,
                provider_total, staged_records, fingerprint, updated_utc)
            VALUES ($generation, $key, $index, $page, $total, $staged, $fingerprint, $now)
            ON CONFLICT (generation_id, partition_index)
            DO UPDATE SET
                partition_key = excluded.partition_key, page = excluded.page,
                provider_total = excluded.provider_total,
                staged_records = catalog_partition_checkpoint.staged_records + excluded.staged_records,
                fingerprint = excluded.fingerprint, updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$generation", generationId);
        command.Parameters.AddWithValue("$key", partitionKey);
        command.Parameters.AddWithValue("$index", partitionIndex);
        command.Parameters.AddWithValue("$page", page);
        command.Parameters.AddWithValue("$total", (object?)providerTotal ?? DBNull.Value);
        command.Parameters.AddWithValue("$staged", stagedDelta);
        command.Parameters.AddWithValue("$fingerprint", (object?)fingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", updatedUtc.ToString("o"));
        command.ExecuteNonQuery();
    }

    private static void InsertGeneration(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string generationId,
        string mode,
        string state,
        DateTimeOffset startedUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO catalog_generation (
                generation_id, mode, state, started_utc, completed_utc,
                unique_titles, warning_count)
            VALUES ($id, $mode, $state, $started, NULL, 0, 0);
            """;
        command.Parameters.AddWithValue("$id", generationId);
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$started", startedUtc.ToString("o"));
        command.ExecuteNonQuery();
    }

    private static void SetBuildingGeneration(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string generationId)
    {
        Execute(connection, transaction,
            "INSERT INTO catalog_state(key, value) VALUES('building_generation', $gen) " +
            "ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
            ("$gen", generationId));
    }

    private static List<string> ReadWarnings(SqliteConnection connection, SqliteTransaction? transaction, string generationId)
    {
        var warnings = new List<string>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT message FROM catalog_warning WHERE generation_id=$gen ORDER BY rank;
            """;
        command.Parameters.AddWithValue("$gen", generationId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            warnings.Add(reader.GetString(0));
        }

        if (warnings.Count > MaxManifestWarnings)
        {
            var capped = warnings.Take(MaxManifestWarnings).ToList();
            capped.Add($"and {warnings.Count - MaxManifestWarnings} more rating drifts.");
            return capped;
        }

        return warnings;
    }

    private static List<string> ListGenerations(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var generations = new List<string>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT generation_id FROM catalog_generation;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            generations.Add(reader.GetString(0));
        }

        return generations;
    }

    private static void DeleteGeneration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId)
    {
        Execute(connection, transaction,
            "DELETE FROM catalog_title_genre_enrichment WHERE generation_id=$gen;",
            ("$gen", generationId));
        Execute(connection, transaction,
            "DELETE FROM catalog_title_genre WHERE generation_id=$gen;",
            ("$gen", generationId));
        Execute(connection, transaction,
            "DELETE FROM catalog_genre_checkpoint WHERE generation_id=$gen;",
            ("$gen", generationId));
        Execute(connection, transaction,
            "DELETE FROM catalog_genre_state WHERE generation_id=$gen;",
            ("$gen", generationId));
        Execute(connection, transaction,
            "DELETE FROM catalog_warning WHERE generation_id=$gen;",
            ("$gen", generationId));
        Execute(connection, transaction,
            "DELETE FROM catalog_partition_checkpoint WHERE generation_id=$gen;",
            ("$gen", generationId));
        Execute(connection, transaction,
            "DELETE FROM catalog_title WHERE generation_id=$gen;",
            ("$gen", generationId));
        Execute(connection, transaction,
            "DELETE FROM catalog_generation WHERE generation_id=$gen;",
            ("$gen", generationId));
    }

    private static CatalogSnapshotManifest? TryLoadManifestCore(SqliteConnection connection)
    {
        var active = ScalarString(connection, null,
            "SELECT value FROM catalog_state WHERE key='active_generation';");
        if (active is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT unique_titles, completed_utc FROM catalog_generation WHERE generation_id=$gen;
            """;
        command.Parameters.AddWithValue("$gen", active);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new CatalogSnapshotException("Active catalog generation is missing.");
        }

        var placeholders = ScalarLong(connection, null,
            "SELECT COUNT(*) FROM catalog_title WHERE generation_id=$gen AND is_title_placeholder=1;",
            ("$gen", active));
        var delta = ScalarString(connection, null,
            "SELECT value FROM catalog_state WHERE key='last_delta_utc';");
        return new CatalogSnapshotManifest(
            CatalogSnapshotSchema.Version,
            active,
            reader.GetInt64(0),
            placeholders,
            DateTimeOffset.Parse(reader.IsDBNull(1) ? DateTimeOffset.UtcNow.ToString("o") : reader.GetString(1)),
            delta is null ? null : DateTimeOffset.Parse(delta),
            ReadWarnings(connection, null, active));
    }

    private static string? ScalarString(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private static long ScalarLong(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private const int MaxManifestWarnings = 100;
    private const int SeedPartitionIndex = -1;
    private const string SeedPartitionKey = "seed";
}
