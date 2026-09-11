using Citadel.Core.Modules;

namespace Citadel.Searcher;

/// <summary>
/// The searcher's one public face: watches the runtime module root, reconciles
/// each folder, and tells the gate. Core never sees a folder path, a manifest, or
/// a load context — this is where all three live.
///
/// **One reconcile path.** The initial scan, every watcher event, the manual
/// `Update modules` button, `FileSystemWatcher.Error` recovery, and the bounded
/// retry all funnel into <see cref="ReconcileFolder"/>. Two
/// paths would mean two behaviours and only one of them gets tested.
///
/// **Serialized, not merely debounced.** One pump task processes one folder at a
/// time, which is what makes route reservation atomic: two folders declaring the
/// same route cannot both win, no matter how they interleave on disk. Per-folder
/// debounce alone does not give that.
///
/// **Nothing polls.** The pump blocks on a signal and wakes for a watcher event
/// or an expiring debounce. Idle costs nothing.
///
/// **No Citadel.Core.** This project declares Contract only. Core's types are
/// reachable transitively, but using them would drag Core along the moment the
/// searcher is swapped for a package feed or a test double. App supplies a log
/// callback and marshals <see cref="FailuresChanged"/> to the main thread.
/// </summary>
public sealed class Watcher : IDisposable
{
    /// <summary>Coalesces the burst of events one folder copy produces.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    /// <summary>Backoff between retries of a folder that is still arriving.</summary>
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Attempts before a folder settles into a visible failure. Unbounded retry
    /// would keep background work alive forever and never let a failure settle.
    /// </summary>
    private const int MaxAttempts = 4;

    private readonly string _root;
    private readonly IModuleGate _gate;
    private readonly IReadOnlyCollection<string> _reservedRoutes;
    private readonly Action<string> _log;
    private readonly Loader _loader;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _signal = new(0, 1);

    private readonly object _state = new();
    private readonly Dictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _routeByFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _folderByRoute = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _contenders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModuleFailure> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _fileWatcher;
    private Task? _pump;
    private bool _disposed;

    /// <param name="root">Absolute runtime module root, supplied by App.</param>
    /// <param name="gate">The registry core exposes.</param>
    /// <param name="reservedRoutes">Routes core owns; refused before loading.</param>
    /// <param name="log">App's bridge to the Modules log sink.</param>
    public Watcher(
        string root,
        IModuleGate gate,
        IReadOnlyCollection<string> reservedRoutes,
        Action<string> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _reservedRoutes = reservedRoutes ?? throw new ArgumentNullException(nameof(reservedRoutes));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _loader = new Loader(log);
    }

    /// <summary>
    /// Raised after the failure list changed, on the pump thread. App marshals it
    /// to the main thread — the searcher has no queue of its own by design.
    /// </summary>
    public event Action? FailuresChanged;

    /// <summary>The absolute folder being watched.</summary>
    public string Root => _root;

    /// <summary>
    /// Current problems, keyed by folder and reconciled rather than appended: a
    /// folder that is fixed or deleted stops appearing.
    /// </summary>
    public IReadOnlyList<ModuleFailure> Failures()
    {
        lock (_state)
        {
            return [.. _failures.Values.OrderBy(f => f.Folder, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>
    /// Begins discovery. Returns immediately: the initial scan runs on the pump,
    /// so the complete first frame never waits on disk.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pump is not null) return;

        if (!EnsureRoot()) return;

        _pump = Task.Run(() => PumpAsync(_stop.Token));
        StartFileWatcher();
        RequestRediscovery();
    }

    /// <summary>
    /// The manual fallback behind `Update modules`, for cases a watcher misses —
    /// network paths, denied notifications. It re-raises the
    /// same scan; it is not a second implementation.
    /// </summary>
    public void RequestRediscovery()
    {
        if (_disposed) return;
        Queue(FullScanMarker, TimeSpan.Zero);
    }

    public void Dispose()
    {
        Task? pump;
        FileSystemWatcher? fileWatcher;
        lock (_state)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
            pump = _pump;
            fileWatcher = _fileWatcher;
            _fileWatcher = null;
            _pump = null;
        }

        DetachFileWatcher(fileWatcher);
        _stop.Cancel();
        Release();

        try
        {
            // Bounded: App disposes on the main thread during exit, and a pump
            // stuck on a dead network path must not hold the process open.
            if (pump is not null && !pump.Wait(TimeSpan.FromSeconds(2)))
            {
                _log("[Searcher] discovery pump did not stop within two seconds");
            }
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
        }

        _loader.Dispose();
        _stop.Dispose();
        _signal.Dispose();
    }

    /// <summary>Sentinel folder name meaning "reconcile everything".</summary>
    private const string FullScanMarker = "*";

    /// <summary>
    /// Creates the root if it is absent. `FileSystemWatcher` throws on a missing
    /// path, and a clean install has no citizens at all — so a missing root is
    /// normal and must not take startup down.
    /// </summary>
    private bool EnsureRoot()
    {
        try
        {
            Directory.CreateDirectory(_root);
            RemoveFailure(RootFailureKey);
            return true;
        }
        catch (Exception exception)
        {
            RecordFailure(new ModuleFailure(
                RootFailureKey,
                SearchStage.Root,
                $"module folder '{_root}' is unavailable: {exception.Message}"));
            _log($"[Searcher] cannot use module folder '{_root}': {exception.Message}");
            return false;
        }
    }

    /// <summary>Folder key for a root-level problem; not a real folder name.</summary>
    private const string RootFailureKey = "module folder";

    private void StartFileWatcher()
    {
        try
        {
            var fileWatcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size,
            };
            fileWatcher.Created += OnChanged;
            fileWatcher.Changed += OnChanged;
            fileWatcher.Deleted += OnChanged;
            fileWatcher.Renamed += OnRenamed;
            fileWatcher.Error += OnError;
            fileWatcher.EnableRaisingEvents = true;

            lock (_state)
            {
                if (_disposed)
                {
                    DetachFileWatcher(fileWatcher);
                    return;
                }
                _fileWatcher = fileWatcher;
            }
        }
        catch (Exception exception)
        {
            // No half-created watcher and no polling replacement: the manual
            // Update button remains the fallback and Settings says why.
            RecordFailure(new ModuleFailure(
                RootFailureKey,
                SearchStage.Root,
                $"module folder '{_root}' cannot be watched, so new screens need "
                + $"Update modules: {exception.Message}"));
            _log($"[Searcher] cannot watch '{_root}': {exception.Message}");
        }
    }

    private void DetachFileWatcher(FileSystemWatcher? fileWatcher)
    {
        if (fileWatcher is null) return;
        try
        {
            fileWatcher.EnableRaisingEvents = false;
            fileWatcher.Created -= OnChanged;
            fileWatcher.Changed -= OnChanged;
            fileWatcher.Deleted -= OnChanged;
            fileWatcher.Renamed -= OnRenamed;
            fileWatcher.Error -= OnError;
        }
        catch (Exception exception)
        {
            _log($"[Searcher] watcher detach failed: {exception.Message}");
        }
        fileWatcher.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => Touch(args.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        // Both sides matter: the old name may have owned a route, the new name
        // may become one.
        Touch(args.OldFullPath);
        Touch(args.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs args)
    {
        // The internal buffer can overflow and the watcher simply stops raising.
        // One full reconcile is the recovery.
        _log($"[Searcher] watcher error, reconciling everything: {args.GetException().Message}");
        RequestRediscovery();
    }

    /// <summary>Maps any path under the root to its top-level folder, then debounces it.</summary>
    private void Touch(string path)
    {
        var folder = FolderOf(path);
        if (folder is null) return;

        // A new event means a new chance: the retry budget resets so a folder
        // that settled into failure can be repaired and rediscovered.
        lock (_state) _attempts.Remove(folder);
        Queue(folder, Debounce);
    }

    private string? FolderOf(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }

        var prefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var relative = full[prefix.Length..];
        var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var folder = separator < 0 ? relative : relative[..separator];
        return string.IsNullOrEmpty(folder) ? null : folder;
    }

    private void Queue(string folder, TimeSpan delay)
    {
        lock (_state)
        {
            if (_disposed) return;
            var due = DateTime.UtcNow + delay;

            // One pending operation per folder. A later event pushes the debounce
            // out rather than adding a second pass.
            _pending[folder] = _pending.TryGetValue(folder, out var existing) && existing > due
                ? existing
                : due;
        }
        Release();
    }

    private void Release()
    {
        try
        {
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Always wait. NextWait returns Timeout.InfiniteTimeSpan (-1ms)
                // when nothing is pending, and a `> TimeSpan.Zero` guard would
                // treat that as "do not wait" and spin a core flat — measured at
                // 100% of one core before this was fixed, while the resident app
                // must remain idle.
                await _signal.WaitAsync(NextWait(), cancellationToken).ConfigureAwait(false);

                while (TryTakeDue(out var folder))
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    if (string.Equals(folder, FullScanMarker, StringComparison.Ordinal))
                    {
                        ReconcileAll();
                    }
                    else
                    {
                        ReconcileFolder(folder);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                // The pump is the only thing keeping discovery alive; it must
                // outlive any single folder's surprise.
                _log($"[Searcher] reconcile pass failed: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// How long the pump may sleep: until the earliest pending debounce expires,
    /// or forever when nothing is pending.
    ///
    /// Returning <see cref="Timeout.InfiniteTimeSpan"/> rather than a long finite
    /// wait is what makes idle genuinely free — no timer, no wakeup, nothing to
    /// measure. The caller must pass this straight to a wait that understands it.
    /// </summary>
    private TimeSpan NextWait()
    {
        lock (_state)
        {
            if (_pending.Count == 0) return Timeout.InfiniteTimeSpan;
            var earliest = _pending.Values.Min();
            var remaining = earliest - DateTime.UtcNow;

            // Zero would busy-wait; one millisecond costs nothing and the work is
            // already due, so it runs on the very next pass.
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
        }
    }

    private bool TryTakeDue(out string folder)
    {
        lock (_state)
        {
            folder = string.Empty;
            if (_disposed || _pending.Count == 0) return false;

            var now = DateTime.UtcNow;
            var candidate = _pending
                .Where(pair => pair.Value <= now)
                .OrderBy(pair => pair.Value)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            if (candidate is null) return false;

            _pending.Remove(candidate);
            folder = candidate;
            return true;
        }
    }

    /// <summary>
    /// Every folder plus every owner whose folder vanished. Used by the initial
    /// scan, `Update modules`, and watcher-error recovery.
    /// </summary>
    private void ReconcileAll()
    {
        List<string> names;
        try
        {
            names = [.. Directory
                .EnumerateDirectories(_root)
                .Select(directory => Path.GetFileName(directory))
                .Where(name => !string.IsNullOrEmpty(name))];
        }
        catch (Exception exception)
        {
            RecordFailure(new ModuleFailure(
                RootFailureKey,
                SearchStage.Root,
                $"module folder '{_root}' could not be listed: {exception.Message}"));
            _log($"[Searcher] cannot list '{_root}': {exception.Message}");
            return;
        }

        lock (_state)
        {
            foreach (var owned in _routeByFolder.Keys)
            {
                if (!names.Contains(owned, StringComparer.OrdinalIgnoreCase)) names.Add(owned);
            }
            foreach (var failed in _failures.Keys)
            {
                if (string.Equals(failed, RootFailureKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (!names.Contains(failed, StringComparer.OrdinalIgnoreCase)) names.Add(failed);
            }
        }

        foreach (var name in names) ReconcileFolder(name);
    }

    /// <summary>
    /// The one authoritative pass over a single folder: exists → manifest →
    /// layout → load → register → ownership. Runs only on the pump, so ownership
    /// decisions are serialized.
    /// </summary>
    private void ReconcileFolder(string folder)
    {
        var path = Path.Combine(_root, folder);

        if (!Directory.Exists(path))
        {
            Forget(folder, deleted: true);
            return;
        }

        if (!Reader.IsCitizenFolder(path))
        {
            // Not a citizen rather than a broken one: v0 skipped these silently
            // (ModuleLoader.cs:32-33). But a folder mid-copy also looks like
            // this, so an owner losing its manifest is still an unregister.
            if (Owns(folder)) Forget(folder, deleted: false);
            RemoveFailure(folder);
            return;
        }

        var manifest = Reader.ReadManifest(path, _reservedRoutes, out var manifestError);
        if (manifest is null)
        {
            Fail(folder, SearchStage.Manifest, manifestError!);
            return;
        }

        // Already registered under this exact route: stop here.
        //
        // Every reconcile trigger funnels through this method, and a folder that
        // is merely touched again produces one — copying two files into a
        // finished folder is enough. Reloading would register a second time, the
        // gate would report a duplicate route against a folder that is its own
        // only claimant, and each pass would retain another load context.
        //
        // This is also where "a changed DLL is picked up next launch"
        // actually lives: the file on disk changed, the loaded
        // screen did not.
        if (AlreadyRegistered(folder, manifest.Route)) return;

        var layout = Reader.ReadLayout(path, out var layoutWarning);
        if (layoutWarning is not null)
        {
            // Fail-soft: identity is valid, so the screen still registers. The
            // warning is recorded as this folder's failure entry and cleared when
            // the file is fixed.
            _log($"[Searcher] '{folder}' layout ignored: {layoutWarning}");
        }

        // Route claimed by another folder: hand it to the gate anyway so the
        // duplicate is reported by the one component that owns that policy, but
        // never record ownership — otherwise deleting the loser would unregister
        // the winner.
        var contested = ClaimOrContend(folder, manifest.Route);

        var module = _loader.Load(path, manifest, out var stage, out var loadError);
        if (module is null)
        {
            Fail(folder, stage, loadError!);
            return;
        }

        _gate.Register(new ModuleDescriptor(
            manifest.Route,
            manifest.Title,
            manifest.Icon,
            manifest.Order,
            module,
            layout));

        if (contested)
        {
            // Terminal, not transient: this folder's files are fine, another
            // folder simply got there first. Retrying would submit to the gate
            // once per attempt — four identical duplicate refusals for one
            // folder — and the state can only change when the owner goes away,
            // which PromoteContender already handles.
            Fail(
                folder,
                SearchStage.Manifest,
                $"route '{manifest.Route}' is already claimed by another screen folder",
                retry: false);
            return;
        }

        Settle(folder, manifest.Route);
        if (layoutWarning is not null)
        {
            RecordFailure(new ModuleFailure(folder, SearchStage.Layout, layoutWarning));
        }
        else
        {
            RemoveFailure(folder);
        }
        _log($"[Searcher] '{folder}' registered route '{manifest.Route}'");
    }

    /// <summary>
    /// Reserves the route for this folder, or records the folder as a contender.
    /// Returns true when the route already belongs to someone else.
    /// </summary>
    private bool ClaimOrContend(string folder, string route)
    {
        lock (_state)
        {
            if (_folderByRoute.TryGetValue(route, out var owner)
                && !string.Equals(owner, folder, StringComparison.OrdinalIgnoreCase))
            {
                if (!_contenders.TryGetValue(route, out var waiting))
                {
                    waiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _contenders[route] = waiting;
                }
                waiting.Add(folder);
                return true;
            }
            return false;
        }
    }

    /// <summary>Records the folder as the route's owner after a successful register.</summary>
    private void Settle(string folder, string route)
    {
        lock (_state)
        {
            if (_routeByFolder.TryGetValue(folder, out var previous)
                && !string.Equals(previous, route, StringComparison.Ordinal))
            {
                // Re-created with a different route: the old one must go, or a
                // stale sidebar entry outlives the folder that declared it.
                _folderByRoute.Remove(previous);
                _gate.Unregister(previous);
                _log($"[Searcher] '{folder}' changed route '{previous}' → '{route}'");
                PromoteContender(previous, folder);
            }

            _routeByFolder[folder] = route;
            _folderByRoute[route] = folder;
            _attempts.Remove(folder);
            if (_contenders.TryGetValue(route, out var waiting)) waiting.Remove(folder);
        }
    }

    private bool Owns(string folder)
    {
        lock (_state) return _routeByFolder.ContainsKey(folder);
    }

    /// <summary>
    /// True when this folder already holds this exact route. A route change is
    /// deliberately not covered: that has to fall through to a fresh load.
    /// </summary>
    private bool AlreadyRegistered(string folder, string route)
    {
        lock (_state)
        {
            return _routeByFolder.TryGetValue(folder, out var owned)
                && string.Equals(owned, route, StringComparison.Ordinal);
        }
    }

    /// <summary>Drops a folder's registration and ownership, and lets a contender try.</summary>
    private void Forget(string folder, bool deleted)
    {
        string? route;
        lock (_state)
        {
            _attempts.Remove(folder);
            if (!_routeByFolder.Remove(folder, out route))
            {
                // Never registered, or a rejected duplicate being deleted. The
                // real owner must not be disturbed.
                foreach (var waiting in _contenders.Values) waiting.Remove(folder);
                route = null;
            }
            else if (_folderByRoute.TryGetValue(route, out var owner)
                && string.Equals(owner, folder, StringComparison.OrdinalIgnoreCase))
            {
                _folderByRoute.Remove(route);
            }
        }
        if (route is null)
        {
            RemoveFailure(folder);
            return;
        }

        _gate.Unregister(route);
        _log(deleted
            ? $"[Searcher] '{folder}' deleted; unregistered route '{route}'"
            : $"[Searcher] '{folder}' is no longer a screen; unregistered route '{route}'");
        RemoveFailure(folder);
        PromoteContender(route, folder);
    }

    /// <summary>
    /// Re-queues folders that lost this route, so deleting an owner lets one
    /// remaining valid folder claim it through the same reconcile path.
    /// </summary>
    private void PromoteContender(string route, string leaving)
    {
        List<string> waiting;
        lock (_state)
        {
            if (!_contenders.TryGetValue(route, out var set) || set.Count == 0) return;
            waiting = [.. set.Where(folder =>
                !string.Equals(folder, leaving, StringComparison.OrdinalIgnoreCase))];
            _contenders.Remove(route);
        }

        foreach (var folder in waiting)
        {
            _log($"[Searcher] route '{route}' is free; retrying '{folder}'");
            Queue(folder, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Records the failure so Settings shows it immediately, then retries while
    /// budget remains — a half-copied folder is indistinguishable from a broken
    /// one until the copy finishes.
    /// </summary>
    /// <param name="retry">
    /// False for a failure that cannot resolve itself by waiting. Retrying one of
    /// those just repeats the same submission and the same refusal.
    /// </param>
    private void Fail(string folder, SearchStage stage, string message, bool retry = true)
    {
        int attempt;
        lock (_state)
        {
            attempt = _attempts.TryGetValue(folder, out var previous) ? previous + 1 : 1;
            _attempts[folder] = attempt;
        }

        // An owner that just broke must stop being offered even while we retry:
        // a registered screen whose DLL vanished cannot render.
        if (Owns(folder)) Forget(folder, deleted: false);

        if (retry && attempt < MaxAttempts)
        {
            _log($"[Searcher] '{folder}' attempt {attempt}/{MaxAttempts} failed at {stage}: {message}");
            RecordFailure(new ModuleFailure(
                folder,
                stage,
                $"{message} (attempt {attempt} of {MaxAttempts}; retrying)"));
            Queue(folder, RetryBackoff * attempt);
            return;
        }

        _log(retry
            ? $"[Searcher] '{folder}' failed at {stage} after {attempt} attempts: {message}"
            : $"[Searcher] '{folder}' failed at {stage}: {message}");
        RecordFailure(new ModuleFailure(folder, stage, message));
    }

    private void RecordFailure(ModuleFailure failure)
    {
        lock (_state)
        {
            if (_failures.TryGetValue(failure.Folder, out var existing) && existing == failure) return;
            _failures[failure.Folder] = failure;
        }
        FailuresChanged?.Invoke();
    }

    private void RemoveFailure(string folder)
    {
        lock (_state)
        {
            if (!_failures.Remove(folder)) return;
        }
        FailuresChanged?.Invoke();
    }
}
