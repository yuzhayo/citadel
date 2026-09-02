using System.Text;
using System.IO;
using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

public sealed class ReaderPreferencesStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.Reader.Tests",
        Guid.NewGuid().ToString("N"));

    public ReaderPreferencesStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void MissingFile_LoadsLockedDefaultsWithoutWarning()
    {
        using var store = new ReaderPreferencesStore(PathOf("missing.json"));

        Assert.Equal(ReaderPreferenceValues.Default, store.Current);
        Assert.Null(store.LastWarning);
    }

    [Fact]
    public void FieldsValidateIndependentlyAndNormalize()
    {
        var path = PathOf("partial.json");
        File.WriteAllText(
            path,
            """
            {"version":1,"dimPercent":"broken","autoScrollSecondsPerViewport":7.7}
            """,
            Encoding.UTF8);

        using var store = new ReaderPreferencesStore(path);

        Assert.Equal(0, store.Current.DimPercent);
        Assert.Equal(8, store.Current.AutoScrollSecondsPerViewport);
        Assert.Contains("dimPercent", store.LastWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("autoScrollSecondsPerViewport", store.LastWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedMalformedAndOversizedDocumentsFallBackSafely()
    {
        var unsupported = PathOf("unsupported.json");
        File.WriteAllText(unsupported, "{\"version\":2,\"dimPercent\":50}");
        using var unsupportedStore = new ReaderPreferencesStore(unsupported);
        Assert.Equal(ReaderPreferenceValues.Default, unsupportedStore.Current);
        Assert.Contains("unsupported schema", unsupportedStore.LastWarning, StringComparison.Ordinal);

        var malformed = PathOf("malformed.json");
        File.WriteAllText(malformed, "{not-json");
        using var malformedStore = new ReaderPreferencesStore(malformed);
        Assert.Equal(ReaderPreferenceValues.Default, malformedStore.Current);
        Assert.Contains("defaults were used", malformedStore.LastWarning, StringComparison.Ordinal);

        var oversized = PathOf("oversized.json");
        File.WriteAllBytes(oversized, new byte[ReaderPreferencesStore.MaximumContentBytes + 1]);
        using var oversizedStore = new ReaderPreferencesStore(oversized);
        Assert.Equal(ReaderPreferenceValues.Default, oversizedStore.Current);
        Assert.Contains("oversized", oversizedStore.LastWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundState_FlushesCurrentPersistedFieldsOnly()
    {
        var path = PathOf("flush.json");
        var state = new ReaderSessionState();
        using (var store = new ReaderPreferencesStore(path))
        {
            store.Bind(state);
            state.SetDimPercent(43);
            state.SetAutoScrollSecondsPerViewport(12.6);
            state.SetZoomScale(2);
            state.SetDrawerPinned(true);

            Assert.True(store.Flush().Saved);
        }

        using var reloaded = new ReaderPreferencesStore(path);
        Assert.Equal(new ReaderPreferenceValues(45, 13), reloaded.Current);
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("zoom", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drawer", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InFlightOldRevision_CannotWinOverCloseFlush()
    {
        var path = PathOf("revision.json");
        using var firstMoveEntered = new ManualResetEventSlim();
        using var releaseFirstMove = new ManualResetEventSlim();
        var fileIO = new BlockingFirstMoveFileIO(firstMoveEntered, releaseFirstMove);
        var state = new ReaderSessionState();
        using var store = new ReaderPreferencesStore(path, fileIO);
        store.Bind(state);

        state.SetDimPercent(5);
        Assert.True(firstMoveEntered.Wait(TimeSpan.FromSeconds(3)), "Debounced save did not start.");
        state.SetDimPercent(25);
        var flush = Task.Run(store.Flush);
        releaseFirstMove.Set();

        Assert.True((await flush).Saved);
        using var reloaded = new ReaderPreferencesStore(path);
        Assert.Equal(25, reloaded.Current.DimPercent);
    }

    [Fact]
    public void ConcurrentInstances_AlwaysLeaveOneCompleteCandidateAndNoTemps()
    {
        var path = PathOf("parallel.json");
        var candidates = Enumerable.Range(0, 16)
            .Select(index => new ReaderPreferenceValues(index * 5, index + 1))
            .ToArray();

        Parallel.For(
            0,
            64,
            index =>
            {
                using var store = new ReaderPreferencesStore(path);
                Assert.True(store.Save(candidates[index % candidates.Length]).Saved);
            });

        using var final = new ReaderPreferencesStore(path);
        Assert.Contains(final.Current, candidates.Select(value => value.Normalize()));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void FailedMove_IsNonBlockingAndCleansTemporaryFile()
    {
        var path = PathOf("move-fails.json");
        using var store = new ReaderPreferencesStore(path, new FailingMoveFileIO());
        string? warning = null;
        store.WarningRaised += (_, value) => warning = value;

        var result = store.Save(new ReaderPreferenceValues(20, 10));

        Assert.False(result.Saved);
        Assert.NotNull(warning);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string PathOf(string name) => Path.Combine(_root, name);

    private sealed class BlockingFirstMoveFileIO(
        ManualResetEventSlim entered,
        ManualResetEventSlim release) : IReaderPreferencesFileIO
    {
        private int _moveCount;

        public bool Exists(string path) => File.Exists(path);
        public long GetLength(string path) => new FileInfo(path).Length;
        public string ReadAllText(string path) => File.ReadAllText(path, Encoding.UTF8);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public void WriteAllText(string path, string content) =>
            File.WriteAllText(path, content, new UTF8Encoding(false));

        public void Move(string sourcePath, string destinationPath, bool overwrite)
        {
            if (Interlocked.Increment(ref _moveCount) == 1)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(3)), "Blocked save was not released.");
            }
            File.Move(sourcePath, destinationPath, overwrite);
        }

        public void Delete(string path) => File.Delete(path);
    }

    private sealed class FailingMoveFileIO : IReaderPreferencesFileIO
    {
        public bool Exists(string path) => File.Exists(path);
        public long GetLength(string path) => new FileInfo(path).Length;
        public string ReadAllText(string path) => File.ReadAllText(path, Encoding.UTF8);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public void WriteAllText(string path, string content) =>
            File.WriteAllText(path, content, new UTF8Encoding(false));
        public void Move(string sourcePath, string destinationPath, bool overwrite) =>
            throw new IOException("Injected move failure.");
        public void Delete(string path) => File.Delete(path);
    }
}
