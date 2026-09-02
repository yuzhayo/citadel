using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Module.Mangareader;

public sealed record ReaderPreferenceValues(
    double DimPercent,
    double AutoScrollSecondsPerViewport)
{
    public static ReaderPreferenceValues Default { get; } = new(
        ReaderValuePolicy.MinimumDimPercent,
        ReaderValuePolicy.DefaultAutoScrollSeconds);

    public ReaderPreferenceValues Normalize() => new(
        ReaderValuePolicy.NormalizeDim(DimPercent),
        ReaderValuePolicy.NormalizeAutoScroll(AutoScrollSecondsPerViewport));
}

public sealed record ReaderPreferenceLoadResult(ReaderPreferenceValues Values, string? Warning);
public sealed record ReaderPreferenceSaveResult(bool Saved, string? Warning);

/// <summary>Validated, atomic, update-safe Reader preference persistence.</summary>
public sealed class ReaderPreferencesStore : IDisposable
{
    public const int MaximumContentBytes = 64 * 1024;
    private const int SchemaVersion = 1;
    private static readonly ConcurrentDictionary<string, object> SharedGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly object _fileGate;
    private readonly object _stateGate = new();
    private readonly object _scheduleGate = new();
    private readonly IReaderPreferencesFileIO _fileIO;
    private CancellationTokenSource? _debounceCancellation;
    private IReaderStateView? _boundState;
    private ReaderPreferenceValues _current;
    private bool _dirty;
    private long _revision;
    private volatile bool _disposed;

    public ReaderPreferencesStore(
        string? storagePath = null,
        IReaderPreferencesFileIO? fileIO = null)
    {
        _path = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citadel",
            "MangaReader",
            "reader-preferences.json");
        _path = Path.GetFullPath(_path);
        _fileGate = SharedGates.GetOrAdd(_path, static _ => new object());
        _fileIO = fileIO ?? ReaderPreferencesFileIO.Instance;
        var loaded = Load();
        _current = loaded.Values;
        LastWarning = loaded.Warning;
    }

    public event EventHandler<string>? WarningRaised;

    public string StoragePath => _path;
    public ReaderPreferenceValues Current
    {
        get
        {
            lock (_stateGate) return _current;
        }
    }
    public string? LastWarning { get; private set; }

    public void Bind(IReaderStateView state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(state);
        if (_boundState is not null)
            throw new InvalidOperationException("Reader preferences are already bound.");
        _boundState = state;
        lock (_stateGate)
        {
            _current = new ReaderPreferenceValues(
                state.DimPercent,
                state.AutoScrollSecondsPerViewport).Normalize();
        }
        state.PropertyChanged += OnStatePropertyChanged;
    }

    public ReaderPreferenceLoadResult Load()
    {
        lock (_fileGate)
        {
            try
            {
                if (!_fileIO.Exists(_path))
                    return new ReaderPreferenceLoadResult(ReaderPreferenceValues.Default, null);
                if (_fileIO.GetLength(_path) > MaximumContentBytes)
                {
                    return new ReaderPreferenceLoadResult(
                        ReaderPreferenceValues.Default,
                        "Reader preferences were oversized and ignored.");
                }

                var json = _fileIO.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new ReaderPreferenceLoadResult(
                        ReaderPreferenceValues.Default,
                        "Reader preferences were empty and ignored.");
                }

                using var document = JsonDocument.Parse(
                    json,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 8,
                    });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("version", out var version)
                    || version.ValueKind != JsonValueKind.Number
                    || !version.TryGetInt32(out var versionNumber)
                    || versionNumber != SchemaVersion)
                {
                    return new ReaderPreferenceLoadResult(
                        ReaderPreferenceValues.Default,
                        "Reader preferences used an unsupported schema and were ignored.");
                }

                var warnings = new List<string>();
                var dim = ReadIndependentNumber(
                    root,
                    "dimPercent",
                    ReaderPreferenceValues.Default.DimPercent,
                    ReaderValuePolicy.NormalizeDim,
                    warnings);
                var speed = ReadIndependentNumber(
                    root,
                    "autoScrollSecondsPerViewport",
                    ReaderPreferenceValues.Default.AutoScrollSecondsPerViewport,
                    ReaderValuePolicy.NormalizeAutoScroll,
                    warnings);
                return new ReaderPreferenceLoadResult(
                    new ReaderPreferenceValues(dim, speed),
                    warnings.Count == 0 ? null : string.Join(" ", warnings));
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or JsonException)
            {
                return new ReaderPreferenceLoadResult(
                    ReaderPreferenceValues.Default,
                    $"Reader preferences could not be read and defaults were used: {exception.Message}");
            }
        }
    }

    public ReaderPreferenceSaveResult Save(ReaderPreferenceValues values)
    {
        var normalized = values.Normalize();
        long revision;
        lock (_stateGate)
        {
            _current = normalized;
            _dirty = true;
            revision = ++_revision;
        }

        return SaveSnapshot(normalized, revision);
    }

    private ReaderPreferenceSaveResult SaveSnapshot(
        ReaderPreferenceValues normalized,
        long revision)
    {
        lock (_fileGate)
        {
            // A debounced write may have been waiting behind a newer flush. An
            // obsolete revision must never overwrite the newer preference file.
            lock (_stateGate)
            {
                if (revision != _revision)
                    return new ReaderPreferenceSaveResult(true, null);
            }

            var temporaryPath = Path.Combine(
                Path.GetDirectoryName(_path)!,
                $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                _fileIO.CreateDirectory(Path.GetDirectoryName(_path)!);
                var payload = JsonSerializer.Serialize(
                    new PreferenceDocument
                    {
                        Version = SchemaVersion,
                        DimPercent = normalized.DimPercent,
                        AutoScrollSecondsPerViewport = normalized.AutoScrollSecondsPerViewport,
                    },
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    });
                _fileIO.WriteAllText(temporaryPath, payload);
                _fileIO.Move(temporaryPath, _path, overwrite: true);
                lock (_stateGate)
                {
                    if (revision == _revision && _current == normalized)
                        _dirty = false;
                }
                return new ReaderPreferenceSaveResult(true, null);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                var warning = $"Reader preferences could not be saved: {exception.Message}";
                RaiseWarning(warning);
                return new ReaderPreferenceSaveResult(false, warning);
            }
            finally
            {
                try
                {
                    if (_fileIO.Exists(temporaryPath)) _fileIO.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public ReaderPreferenceSaveResult Flush()
    {
        CancelDebounce();
        ReaderPreferenceValues snapshot;
        long revision;
        lock (_stateGate)
        {
            if (!_dirty) return new ReaderPreferenceSaveResult(true, null);
            snapshot = _current;
            revision = _revision;
        }

        return SaveSnapshot(snapshot, revision);
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_boundState is null) return;
        if (e.PropertyName is not nameof(IReaderStateView.DimPercent)
            and not nameof(IReaderStateView.AutoScrollSecondsPerViewport))
        {
            return;
        }

        var current = new ReaderPreferenceValues(
            _boundState.DimPercent,
            _boundState.AutoScrollSecondsPerViewport).Normalize();
        lock (_stateGate)
        {
            if (_current == current) return;
            _current = current;
            _dirty = true;
            _revision++;
        }
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        lock (_scheduleGate)
        {
            _debounceCancellation?.Cancel();
            _debounceCancellation?.Dispose();
            _debounceCancellation = new CancellationTokenSource();
            var token = _debounceCancellation.Token;
            ReaderPreferenceValues snapshot;
            long revision;
            lock (_stateGate)
            {
                snapshot = _current;
                revision = _revision;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, token);
                    if (!token.IsCancellationRequested && !_disposed)
                        SaveSnapshot(snapshot, revision);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
            }, token);
        }
    }

    private void CancelDebounce()
    {
        lock (_scheduleGate)
        {
            _debounceCancellation?.Cancel();
            _debounceCancellation?.Dispose();
            _debounceCancellation = null;
        }
    }

    private void RaiseWarning(string warning)
    {
        LastWarning = warning;
        WarningRaised?.Invoke(this, warning);
    }

    private static double ReadIndependentNumber(
        JsonElement root,
        string propertyName,
        double fallback,
        Func<double, double> normalize,
        ICollection<string> warnings)
    {
        if (!root.TryGetProperty(propertyName, out var property)) return fallback;
        if (property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out var value)
            && double.IsFinite(value))
        {
            return normalize(value);
        }

        warnings.Add($"'{propertyName}' was invalid and its default was used.");
        return fallback;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_boundState is not null)
            _boundState.PropertyChanged -= OnStatePropertyChanged;
        CancelDebounce();
        _boundState = null;
        _disposed = true;
    }

    private sealed class PreferenceDocument
    {
        public int Version { get; init; }
        public double DimPercent { get; init; }
        public double AutoScrollSecondsPerViewport { get; init; }
    }
}

public interface IReaderPreferencesFileIO
{
    bool Exists(string path);
    long GetLength(string path);
    string ReadAllText(string path);
    void CreateDirectory(string path);
    void WriteAllText(string path, string content);
    void Move(string sourcePath, string destinationPath, bool overwrite);
    void Delete(string path);
}

public sealed class ReaderPreferencesFileIO : IReaderPreferencesFileIO
{
    public static ReaderPreferencesFileIO Instance { get; } = new();
    private ReaderPreferencesFileIO() { }

    public bool Exists(string path) => File.Exists(path);
    public long GetLength(string path) => new FileInfo(path).Length;
    public string ReadAllText(string path) => File.ReadAllText(path, Encoding.UTF8);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void WriteAllText(string path, string content) =>
        File.WriteAllText(path, content, new UTF8Encoding(false));
    public void Move(string sourcePath, string destinationPath, bool overwrite) =>
        File.Move(sourcePath, destinationPath, overwrite);
    public void Delete(string path) => File.Delete(path);
}
