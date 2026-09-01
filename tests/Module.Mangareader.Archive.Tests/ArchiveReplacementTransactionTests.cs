using System.IO;
using Module.Mangareader.Archive;

namespace Module.Mangareader.Archive.Tests;

public sealed class ArchiveReplacementTransactionTests : ArchiveTestFixture
{
    private static readonly byte[] PageOne = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] PageTwo = [9, 9, 8, 8, 7, 7, 6, 6, 5, 5];

    private string CreateChapter(string name = "chapter.cbz")
    {
        var path = ChapterPath(name);
        CreateZip(
            path,
            ("001.png", PageOne),
            ("pages/002.png", PageTwo));
        return path;
    }

    [Fact]
    public async Task SuccessfulBakePlacesExactlyOneGeneratedCoverFirst()
    {
        var chapter = CreateChapter();
        var transaction = CreateTransaction();

        var result = await transaction.BakeAsync(chapter, MinimalPng(), CancellationToken.None);

        Assert.Equal(CoverBakeStatus.Baked, result.Status);
        Assert.Equal(ArchiveFormat.Zip, result.Format);
        Assert.Equal(CoverArchiveWriter.GeneratedCoverEntryName, result.CoverEntryName);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));

        var entries = ReadZipEntries(chapter);
        Assert.Equal(CoverArchiveWriter.GeneratedCoverEntryName, entries[0].Name);
        Assert.Single(entries, entry =>
            string.Equals(entry.Name, CoverArchiveWriter.GeneratedCoverEntryName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(MinimalPng(), entries[0].Bytes);

        // No temporary litter beside the source.
        Assert.DoesNotContain(Directory.EnumerateFiles(Root), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatedBakeReplacesCoverWithoutDuplicates()
    {
        var chapter = CreateChapter();
        var transaction = CreateTransaction();

        await transaction.BakeAsync(chapter, MinimalPng(), CancellationToken.None);
        var second = await transaction.BakeAsync(chapter, AlternativePng(), CancellationToken.None);

        Assert.Equal(CoverBakeStatus.Baked, second.Status);
        var entries = ReadZipEntries(chapter);
        Assert.Equal(3, entries.Count);
        Assert.Single(entries, entry =>
            string.Equals(entry.Name, CoverArchiveWriter.GeneratedCoverEntryName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(AlternativePng(), entries[0].Bytes);
    }

    [Fact]
    public async Task OriginalEntryNamesAndHashesRemainPreserved()
    {
        var chapter = CreateChapter();
        var originalHashes = ReadZipEntries(chapter)
            .ToDictionary(entry => entry.Name, entry => Sha256Hex(entry.Bytes));
        var transaction = CreateTransaction();

        await transaction.BakeAsync(chapter, MinimalPng(), CancellationToken.None);

        var entries = ReadZipEntries(chapter);
        foreach (var (name, hash) in originalHashes)
        {
            var preserved = Assert.Single(entries, entry => entry.Name == name);
            Assert.Equal(hash, Sha256Hex(preserved.Bytes));
        }
    }

    [Fact]
    public async Task CorruptInputDoesNotMutateSource()
    {
        var chapter = ChapterPath("corrupt.cbz");
        CreateZip(chapter, ("001.png", PageOne));
        var full = File.ReadAllBytes(chapter);
        var truncated = full[..(full.Length / 2)];
        File.WriteAllBytes(chapter, truncated);
        var before = Sha256HexOfFile(chapter);
        var transaction = CreateTransaction();

        var result = await transaction.BakeAsync(chapter, MinimalPng(), CancellationToken.None);

        Assert.Equal(CoverBakeStatus.CorruptOrTruncated, result.Status);
        Assert.Null(result.BackupPath);
        Assert.Equal(before, Sha256HexOfFile(chapter));
        Assert.Empty(Directory.EnumerateFiles(BackupRoot()));
    }

    [Fact]
    public async Task UnsupportedFormatDoesNotMutateSourceOrCreateBackup()
    {
        var chapter = ChapterPath("chapter.cbr");
        File.WriteAllBytes(chapter, Rar4Content());
        var before = Sha256HexOfFile(chapter);
        var transaction = CreateTransaction();

        var result = await transaction.BakeAsync(chapter, MinimalPng(), CancellationToken.None);

        Assert.Equal(CoverBakeStatus.UnsupportedFormat, result.Status);
        Assert.Equal(ArchiveFormat.Rar4, result.Format);
        Assert.Contains("RAR4", result.Detail, StringComparison.Ordinal);
        Assert.Null(result.BackupPath);
        Assert.Equal(before, Sha256HexOfFile(chapter));
        Assert.Empty(Directory.EnumerateFiles(BackupRoot()));
    }

    [Fact]
    public async Task TwoSuccessfulBakesLeaveOnlyTheLatestValidBackup()
    {
        var chapter = CreateChapter();
        var backupRoot = BackupRoot();
        var transaction = CreateTransaction(backupRoot);

        await transaction.BakeAsync(chapter, MinimalPng(), CancellationToken.None);
        var second = await transaction.BakeAsync(chapter, AlternativePng(), CancellationToken.None);

        var backups = Directory.EnumerateFiles(backupRoot).ToArray();
        Assert.Single(backups);
        Assert.StartsWith(LatestCoverBackupStore.LatestBackupBaseName, Path.GetFileName(backups[0]), StringComparison.Ordinal);
        Assert.Equal(backups[0], second.BackupPath);

        // The remaining backup is itself a readable archive.
        var backupEntries = ReadZipEntries(backups[0]);
        Assert.NotEmpty(backupEntries);
    }

    [Fact]
    public async Task BackupBytesMatchTheOriginalImmediatelyBeforeReplacement()
    {
        var chapter = CreateChapter();
        var transaction = CreateTransaction();
        var originalBeforeFirstBake = File.ReadAllBytes(chapter);

        var first = await transaction.BakeAsync(chapter, MinimalPng(), CancellationToken.None);
        Assert.Equal(originalBeforeFirstBake, File.ReadAllBytes(first.BackupPath!));

        // Second bake: the backup must equal the chapter as it stood right
        // before the second replacement, not the pristine original.
        var stateBeforeSecondBake = File.ReadAllBytes(chapter);
        var second = await transaction.BakeAsync(chapter, AlternativePng(), CancellationToken.None);

        Assert.Equal(stateBeforeSecondBake, File.ReadAllBytes(second.BackupPath!));
        Assert.Single(Directory.EnumerateFiles(BackupRoot()));
    }

    [Fact]
    public async Task InjectedValidationFailurePreservesOriginalAndPreviousBackup()
    {
        var chapter = CreateChapter();
        var backupRoot = BackupRoot();
        var baseline = await CreateTransaction(backupRoot)
            .BakeAsync(chapter, MinimalPng(), CancellationToken.None);
        var chapterHash = Sha256HexOfFile(chapter);
        var backupHash = Sha256HexOfFile(baseline.BackupPath!);

        var failing = new FailingValidationTransaction(backupRoot);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => failing.BakeAsync(chapter, AlternativePng(), CancellationToken.None));

        Assert.Equal(chapterHash, Sha256HexOfFile(chapter));
        var backups = Directory.EnumerateFiles(backupRoot).ToArray();
        Assert.Single(backups);
        Assert.Equal(backupHash, Sha256HexOfFile(backups[0]));
    }

    [Fact]
    public async Task FailedReplacementKeepsThePreviousBackup()
    {
        var chapter = CreateChapter();
        var backupRoot = BackupRoot();
        var baseline = await CreateTransaction(backupRoot)
            .BakeAsync(chapter, MinimalPng(), CancellationToken.None);
        var chapterHash = Sha256HexOfFile(chapter);
        var previousBackupHash = Sha256HexOfFile(baseline.BackupPath!);

        var failing = new FailingReplacementTransaction(backupRoot);
        await Assert.ThrowsAsync<IOException>(
            () => failing.BakeAsync(chapter, AlternativePng(), CancellationToken.None));

        // Chapter untouched...
        Assert.Equal(chapterHash, Sha256HexOfFile(chapter));
        // ...and the previous bake's backup survives byte-for-byte instead
        // of being consumed by the failed attempt.
        var backups = Directory.EnumerateFiles(backupRoot).ToArray();
        var visible = backups.Where(path => !Path.GetFileName(path).StartsWith(".")).ToArray();
        Assert.Single(visible);
        Assert.Equal(previousBackupHash, Sha256HexOfFile(visible[0]));
        // No orphaned candidate remains.
        Assert.Empty(backups.Except(visible));
    }

    [Fact]
    public async Task StaleBackupCleanupFailureSurfacesStructuredWarning()
    {
        var chapter = CreateChapter();
        var backupRoot = BackupRoot();
        var stalePath = Path.Combine(backupRoot, "stale-legacy.cbz");
        CreateZip(stalePath, ("old.png", PageOne));
        File.SetAttributes(stalePath, FileAttributes.ReadOnly);

        try
        {
            var result = await CreateTransaction(backupRoot)
                .BakeAsync(chapter, MinimalPng(), CancellationToken.None);

            Assert.Equal(CoverBakeStatus.Baked, result.Status);
            Assert.Equal(chapter, result.ChapterPath);
            var warning = Assert.Single(result.BackupWarnings);
            Assert.Contains("stale-legacy.cbz", warning, StringComparison.Ordinal);
            Assert.True(File.Exists(stalePath));
            Assert.True(File.Exists(result.BackupPath));
        }
        finally
        {
            File.SetAttributes(stalePath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task CancellationBeforeCommitPreservesOriginal()
    {
        var chapter = CreateChapter();
        var before = Sha256HexOfFile(chapter);
        var transaction = CreateTransaction();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transaction.BakeAsync(chapter, MinimalPng(), cancellation.Token));

        Assert.Equal(before, Sha256HexOfFile(chapter));
        Assert.Empty(Directory.EnumerateFiles(BackupRoot()));
    }

    [Fact]
    public async Task MidFlowCancellationReportsCancelledAndPreservesOriginal()
    {
        var chapter = CreateChapter();
        var backupRoot = BackupRoot();
        var before = Sha256HexOfFile(chapter);
        using var cancellation = new CancellationTokenSource();
        var cancelling = new CancellingValidationTransaction(backupRoot, cancellation);

        var result = await cancelling.BakeAsync(chapter, MinimalPng(), cancellation.Token);

        Assert.Equal(CoverBakeStatus.Cancelled, result.Status);
        Assert.Equal(before, Sha256HexOfFile(chapter));
        Assert.Empty(Directory.EnumerateFiles(backupRoot));
    }

    [Fact]
    public async Task InvalidCoverPngIsRejectedBeforeAnyMutation()
    {
        var chapter = CreateChapter();
        var before = Sha256HexOfFile(chapter);
        var transaction = CreateTransaction();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => transaction.BakeAsync(chapter, [1, 2, 3, 4], CancellationToken.None));

        Assert.Equal(before, Sha256HexOfFile(chapter));
        Assert.Empty(Directory.EnumerateFiles(BackupRoot()));
    }

    [Fact]
    public async Task LockReleaseReportsReleasedProcessesWithoutTouchingRealApplications()
    {
        var chapter = CreateChapter();
        var backupRoot = BackupRoot();
        // The test locks its own fixture file; the fake coordinator releases
        // that exact handle. No real application is ever terminated.
        var lockHandle = new FileStream(chapter, FileMode.Open, FileAccess.Read, FileShare.Read);
        var fakeProcess = new ReleasedArchiveProcess(4242, "TestFixtureLocker");
        var transaction = new ArchiveReplacementTransaction(
            new LatestCoverBackupStore(backupRoot),
            new ReleasingLockCoordinator(lockHandle, fakeProcess));

        var result = await transaction.BakeAsync(chapter, MinimalPng(), CancellationToken.None);

        Assert.Equal(CoverBakeStatus.Baked, result.Status);
        var released = Assert.Single(result.ReleasedProcesses);
        Assert.Equal(4242, released.ProcessId);
        Assert.Equal("TestFixtureLocker", released.DisplayName);
    }

    private sealed class FailingValidationTransaction(string backupRoot)
        : ArchiveReplacementTransaction(
            new LatestCoverBackupStore(backupRoot),
            new NullLockCoordinator())
    {
        internal override void ValidateRewrittenArchive(
            string temporaryPath,
            byte[] expectedCoverBytes,
            IReadOnlyList<OriginalEntryManifest> preservedEntries,
            CancellationToken cancellationToken)
        {
            throw new InvalidDataException("Injected validation failure.");
        }
    }

    private sealed class FailingReplacementTransaction(string backupRoot)
        : ArchiveReplacementTransaction(
            new LatestCoverBackupStore(backupRoot),
            new NullLockCoordinator())
    {
        internal override IReadOnlyList<ReleasedArchiveProcess> CommitReplacement(
            string temporaryPath,
            string targetPath,
            CancellationToken cancellationToken)
        {
            throw new IOException("Injected replacement failure.");
        }
    }

    private sealed class CancellingValidationTransaction(
        string backupRoot,
        CancellationTokenSource callerCancellation)
        : ArchiveReplacementTransaction(
            new LatestCoverBackupStore(backupRoot),
            new NullLockCoordinator())
    {
        internal override void ValidateRewrittenArchive(
            string temporaryPath,
            byte[] expectedCoverBytes,
            IReadOnlyList<OriginalEntryManifest> preservedEntries,
            CancellationToken cancellationToken)
        {
            callerCancellation.Cancel();
            throw new OperationCanceledException(callerCancellation.Token);
        }
    }

    private sealed class ReleasingLockCoordinator(
        FileStream lockHandle,
        ReleasedArchiveProcess process) : IArchiveLockCoordinator
    {
        public IReadOnlyList<ReleasedArchiveProcess> ReleaseForReplacement(
            string archivePath,
            CancellationToken cancellationToken)
        {
            lockHandle.Dispose();
            return [process];
        }
    }
}
