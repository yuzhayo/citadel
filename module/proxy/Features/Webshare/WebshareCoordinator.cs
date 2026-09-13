using Module.Proxy.SharedLogic;

namespace Module.Proxy.Features.Webshare;

internal sealed class WebshareCoordinator(
    WebshareImportService service,
    WebshareCredentialStore credentials,
    ProxyPoolStore poolStore,
    ProxySettingsStore settingsStore)
{
    private readonly WebshareImportService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly WebshareCredentialStore _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    private readonly ProxyPoolStore _poolStore = poolStore ?? throw new ArgumentNullException(nameof(poolStore));
    private readonly ProxySettingsStore _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private WebshareImportProgress _current = new(false, "Add one or more API keys, then process Webshare.");

    public event EventHandler<WebshareImportProgress>? StateChanged;
    public event EventHandler? PoolCommitted;

    public WebshareImportProgress CurrentState { get { lock (_gate) return _current; } }
    public int KeyCount => _credentials.Load().Count;
    internal IReadOnlyList<string> SavedKeys => _credentials.Load();

    public int AddKeys(string text) => _credentials.AddFromPaste(text);
    public void ClearKeys() => _credentials.Clear();

    public async Task ProcessAsync()
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_cancellation is not null) return;
            cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
        }
        Publish(new WebshareImportProgress(true, "Starting Webshare import...", KeyCount, KeyCount));
        WebshareImportProgress terminal;
        try
        {
            var banned = _poolStore.LoadBanned().Endpoints.Select(endpoint => endpoint.Canonical)
                .ToHashSet(StringComparer.Ordinal);
            var progress = new Progress<WebshareImportProgress>(value =>
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_cancellation, cancellation)) return;
                }
                Publish(value);
            });
            var result = await _service.RunAsync(_credentials.Load(), _settingsStore.Load(), banned, progress, cancellation.Token)
                .ConfigureAwait(false);
            if (result.Reachable.Count == 0)
            {
                terminal = new WebshareImportProgress(false, "No reachable Webshare proxy; existing pool was preserved.",
                    KeyCount, KeyCount, result.Candidates, result.Candidates, 0, result.Candidates, TimeSpan.Zero);
            }
            else
            {
                _poolStore.Commit(result.Reachable, result.Health);
                PoolCommitted?.Invoke(this, EventArgs.Empty);
                terminal = new WebshareImportProgress(false,
                    $"Webshare complete: {result.Reachable.Count} reachable from {result.Candidates} checked · {result.Skipped} skipped.",
                    KeyCount, KeyCount, result.Candidates, result.Candidates, result.Reachable.Count,
                    result.Candidates - result.Reachable.Count, TimeSpan.Zero);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            terminal = new WebshareImportProgress(false, "Webshare import cancelled; existing pool was preserved.");
        }
        catch (Exception error)
        {
            terminal = new WebshareImportProgress(false, "Webshare import failed: " + error.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            }
            cancellation.Dispose();
        }
        Publish(terminal);
    }

    public void Stop() { lock (_gate) _cancellation?.Cancel(); }

    private void Publish(WebshareImportProgress state)
    {
        EventHandler<WebshareImportProgress>? handlers;
        lock (_gate)
        {
            _current = state;
            handlers = StateChanged;
        }
        handlers?.Invoke(this, state);
    }
}
