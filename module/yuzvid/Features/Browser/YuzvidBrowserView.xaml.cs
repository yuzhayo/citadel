using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace Module.Yuzvid.Features.Browser;

/// <summary>
/// WebView2 host control. Explicit init, state tracking, error display.
/// Browser always routes through a local proxy server (localhost:PORT).
/// The local proxy handles upstream/direct routing — no browser restart needed.
/// </summary>
public partial class YuzvidBrowserView : UserControl
{
    // Keep Yuzvid's web state isolated from both Chrome and the old implicit
    // WebView2 profile. The profile directory is stable after this reset, so
    // normal browser state persists across future Yuzvid launches.
    private static readonly string UserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Citadel",
        "Yuzvid",
        "WebView2ProfileV2");

    private string? _pendingUrl;
    private Task? _initializationTask;
    private BrowserState _state = BrowserState.Initializing;
    private int _proxyPort;
    private CoreWebView2Environment? _environment;
    private SilentPopupHostManager? _popupManager;

    // True while the next NewDocument navigation is ours (address bar, bookmark,
    // pending URL). Consumed by the first NewDocument start; a page racing us
    // in that micro-window is misattributed as ours (accepted risk, documented).
    private bool _expectSelfNav;

    // NavigationIds we cancelled in NavigationStarting: their Completed event
    // must stay silent instead of showing "Navigasi gagal".
    private readonly HashSet<ulong> _cancelledNavIds = new();

    // Same for symmetric duplicates (see Starting handler).
    private readonly HashSet<ulong> _dupCancelledNavIds = new();

    // In-flight main-tab navigations (id → normalized URL). Lets the popup
    // gateway skip duplicate main-tab routes for the same click (Fase 1 dedup):
    // without this, one click produced TWO tab navigations racing each other
    // (observed: ConnectionAborted + blink-back). UI thread only.

    // T3.3 Source B tap: recent directly-downloadable media URIs seen by the
    // engine. ConcurrentQueue because WebResourceRequested may fire off-UI.
    private readonly ConcurrentQueue<string> _videoTap = new();
    private const int MaxTapEntries = 200;

    /// <summary>
    /// Immutable snapshot of recently seen direct-media URIs (newest last).
    /// The ONLY tap data that leaves the Browser feature.
    /// </summary>
    public IReadOnlyList<string> GetVideoRequestSnapshot() => _videoTap.ToArray();

    public void ClearVideoRequests()
    {
        while (_videoTap.TryDequeue(out _)) { }
    }

    private void RecordVideoTap(string uri)
    {
        _videoTap.Enqueue(uri);
        while (_videoTap.Count > MaxTapEntries && _videoTap.TryDequeue(out _)) { }
    }

    /// <summary>Raised when the page URL changes (for toolbar URL field sync).</summary>
    public event EventHandler<string>? Navigated;

    /// <summary>Raised when navigation starts/completes (for toolbar loading indicator).</summary>
    public event EventHandler<bool>? NavigationStateChanged;

    /// <summary>Raised when a URL cannot be accepted by the browser.</summary>
    public event EventHandler<string>? NavigationFailed;

    /// <summary>Raised when a valid URL is held until the browser is ready.</summary>
    public event EventHandler<string>? NavigationQueued;

    /// <summary>Raised when browser state changes (Initializing/Ready/Failed).</summary>
    public event EventHandler<BrowserState>? StateChanged;

    /// <summary>Raised when a message is received from injected scripts (Phase 2).</summary>
    public event EventHandler<string>? ScriptMessageReceived;

    /// <summary>
    /// C#→JS half of the bridge. Returns the JSON-encoded result.
    /// Throws InvalidOperationException when the engine is not ready.
    /// </summary>
    public Task<string> ExecuteScriptAsync(string script)
    {
        var core = Browser.CoreWebView2;
        if (State != BrowserState.Ready || core is null)
            throw new InvalidOperationException("Browser CoreWebView2 not ready.");
        return core.ExecuteScriptAsync(script);
    }

    public YuzvidBrowserView()
    {
        InitializeComponent();
    }

    public BrowserState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            UpdateVisualState();
            StateChanged?.Invoke(this, value);
        }
    }

    // ─── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Sets the local proxy port. Must be called before initialization.
    /// Browser will route ALL traffic through localhost:port.
    /// </summary>
    internal void SetLocalProxyPort(int port)
    {
        _proxyPort = port;
    }

    /// <summary>Active local-proxy port (0 = direct). For controller routing only.</summary>
    internal int LocalProxyPort => _proxyPort;

    public void Navigate(string url)
    {
        if (!TryNormalizeUrl(url, out var target, out var error))
        {
            NavigationFailed?.Invoke(this, error);
            return;
        }

        try
        {
            if (State == BrowserState.Ready && Browser.CoreWebView2 is not null)
            {
                _expectSelfNav = true;
                Browser.CoreWebView2.Navigate(target);
            }
            else if (State == BrowserState.Initializing)
            {
                _pendingUrl = target;
                NavigationQueued?.Invoke(this, "Browser sedang disiapkan; URL akan dibuka otomatis.");
            }
            else
            {
                NavigationFailed?.Invoke(this, "Browser belum siap. Jalankan retry initialization.");
            }
        }
        catch (ArgumentException)
        {
            NavigationFailed?.Invoke(this, "URL tidak dapat dibuka.");
        }
    }

    public void GoBack()
    {
        if (State == BrowserState.Ready && Browser.CoreWebView2?.CanGoBack == true)
            Browser.CoreWebView2.GoBack();
    }

    public void GoForward()
    {
        if (State == BrowserState.Ready && Browser.CoreWebView2?.CanGoForward == true)
            Browser.CoreWebView2.GoForward();
    }

    public void Refresh()
    {
        if (State == BrowserState.Ready)
            Browser.CoreWebView2?.Reload();
    }

    public void Stop()
    {
        if (State == BrowserState.Ready)
            Browser.CoreWebView2?.Stop();
    }

    public string CurrentUrl => Browser.CoreWebView2?.Source?.ToString() ?? "";
    public string CurrentTitle => Browser.CoreWebView2?.DocumentTitle ?? "";
    public bool CanGoBack => State == BrowserState.Ready && Browser.CoreWebView2?.CanGoBack == true;
    public bool CanGoForward => State == BrowserState.Ready && Browser.CoreWebView2?.CanGoForward == true;

    // ─── Lifecycle ────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await EnsureInitializedAsync();
    }

    /// <summary>
    /// Starts one initialization attempt when the browser is not ready.
    /// </summary>
    public Task RetryInitAsync() => EnsureInitializedAsync();

    private Task EnsureInitializedAsync()
    {
        if (State == BrowserState.Ready)
        {
            return Task.CompletedTask;
        }

        if (_initializationTask is { IsCompleted: false })
        {
            return _initializationTask;
        }

        _initializationTask = InitWebView2Async();
        return _initializationTask;
    }

    private async Task InitWebView2Async()
    {
        State = BrowserState.Initializing;
        PopupTrace.Write("init", "start");

        try
        {
            // The local adapter owns proxy selection; WebView2 has one stable
            // localhost route for its entire retained session.
            var envOptions = new CoreWebView2EnvironmentOptions();
            if (_proxyPort > 0)
                envOptions.AdditionalBrowserArguments = $"--proxy-server=127.0.0.1:{_proxyPort}";

            Directory.CreateDirectory(UserDataFolder);

            // Same-profile re-init after Stop can race the previous browser
            // process exiting (0x8007139F). Retry only that transient state.
            _environment = await CreateEnvironmentWithRetryAsync(envOptions);
            PopupTrace.Write("init", "env-ok");

            await Browser.EnsureCoreWebView2Async(_environment);
            PopupTrace.Write("init", "core-ok");
        }
        catch (Exception ex)
        {
            State = BrowserState.Failed;
            PopupTrace.Write("init", "failed: " + ex.Message);
            ErrorTitle.Text = "Browser engine failed to initialize";
            ErrorDetail.Text = ex.Message;
            return;
        }

        _environment = Browser.CoreWebView2?.Environment;
        var cv2 = Browser.CoreWebView2;
        if (cv2 is null || _environment is null)
        {
            State = BrowserState.Failed;
            PopupTrace.Write("init", "failed: no-core");
            ErrorTitle.Text = "Browser engine failed to initialize";
            ErrorDetail.Text = "CoreWebView2 unavailable after EnsureCoreWebView2Async.";
            return;
        }

        // Silent popup sessions share this environment (R1: manager created now,
        // before any navigation can raise NewWindowRequested).
        _popupManager = new SilentPopupHostManager(
            _environment, Dispatcher, RecordVideoTap, ForwardPopupScriptMessage,
            () => PopupTrace.HostOf(Browser.CoreWebView2?.Source),
            NavigatePopupTarget);

        // Set realistic user-agent — default WebView2 UA looks like a bot
        cv2.Settings.UserAgent = SharedUserAgent;

        // Register ad-block filter (must be added before event fires)
        cv2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);

        // Block ad domains at network level
        cv2.WebResourceRequested += OnAdBlockResourceRequested;

        // Block new windows/popups; normal same-tab navigation is unaffected.
        cv2.NewWindowRequested += OnNewWindowRequested;

        // T2.1 JS→C# bridge: page calls chrome.webview.postMessage(json)
        // → re-raised as ScriptMessageReceived (kills CS0067).
        cv2.WebMessageReceived += (s, e) =>
            ScriptMessageReceived?.Invoke(this, e.WebMessageAsJson);

        // T2.2 JS bridge payload. post() = JS→C# (lands in WebMessageReceived).
        // onCommand = C#→JS entry (called via ExecuteScriptAsync); pages may
        // override it — default echoes back so the ping smoke works unmodified.
        await cv2.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);

        // Wire navigation events
        Browser.NavigationStarting += Browser_NavigationStarting;
        Browser.NavigationCompleted += Browser_NavigationCompleted;

        State = BrowserState.Ready;
        PopupTrace.Write("init", "ready");

        // Navigate to pending URL if user typed one before init completed
        if (!string.IsNullOrEmpty(_pendingUrl))
        {
            _expectSelfNav = true;
            cv2.Navigate(_pendingUrl);
            _pendingUrl = null;
        }
    }

    /// <summary>
    /// Gateway entry for content popups: navigate the main tab unless it is
    /// already there or already heading there (same click firing both the
    /// default same-tab navigation and window.open). Skips are traced.
    /// </summary>
    internal void NavigatePopupTarget(string url)
    {
        var norm = NormalizeNavUrl(url);
        var current = Browser.CoreWebView2?.Source;
        if (norm is not null && norm == NormalizeNavUrl(current))
        {
            PopupTrace.Write("maintab-dedup-skip", "already-here host=" + PopupTrace.HostOf(url) + " path=" + PopupTrace.PathOf(url));
            return;
        }
        foreach (var pending in _inflightNavs.Values)
        {
            if (norm is not null && norm == pending)
            {
                PopupTrace.Write("maintab-dedup-skip", "in-flight host=" + PopupTrace.HostOf(url) + " path=" + PopupTrace.PathOf(url));
                return;
            }
        }
        PopupTrace.Write("maintab-nav", "host=" + PopupTrace.HostOf(url) + " path=" + PopupTrace.PathOf(url));
        Navigate(url);
    }

    private static string? NormalizeNavUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return uri.AbsoluteUri.TrimEnd('/').ToLowerInvariant();
    }

    private readonly Dictionary<ulong, string> _inflightNavs = new();

    private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        PopupTrace.Write("nav-start", $"id={e.NavigationId} user={e.IsUserInitiated} host={PopupTrace.HostOf(e.Uri)} path={PopupTrace.PathOf(e.Uri)}");
        var norm = NormalizeNavUrl(e.Uri);
        if (norm is not null) _inflightNavs[e.NavigationId] = norm;
        if (ShouldCancelDocumentNav(e))
        {
            e.Cancel = true;
            _cancelledNavIds.Add(e.NavigationId);
            PopupTrace.Write("nav-cancel", "host=" + PopupTrace.HostOf(e.Uri) + " path=" + PopupTrace.PathOf(e.Uri));
            NavigationStateChanged?.Invoke(this, false);
            return;
        }
        // Symmetric duplicate: another in-flight NewDocument nav already targets
        // the identical URL (same click firing default + popup, double-clicks).
        // Cancel the newcomer — whoever started first landing is the same place.
        if (e.NavigationKind == CoreWebView2NavigationKind.NewDocument && norm is not null)
        {
            foreach (var kv in _inflightNavs)
            {
                if (kv.Key != e.NavigationId && kv.Value == norm)
                {
                    e.Cancel = true;
                    _dupCancelledNavIds.Add(e.NavigationId);
                    PopupTrace.Write("dup-cancel", $"id={e.NavigationId} dup-of={kv.Key} host={PopupTrace.HostOf(e.Uri)} path={PopupTrace.PathOf(e.Uri)}");
                    NavigationStateChanged?.Invoke(this, false);
                    return;
                }
            }
        }
        NavigationStateChanged?.Invoke(this, true);
    }

    /// <summary>
    /// Decides whether a starting top-level document navigation must die.
    /// Allowed, in order: non-documents (Reload/BackOrForward), our own
    /// programmatic navigations, explicit user gestures (click/keypress —
    /// a human asked for this, list or not), same-host moves. Only
    /// non-gesture hops to a listed redirector host are cancelled — the tab
    /// stays exactly where it was (no URL change, no blank page).
    /// Trade-off, stated plainly: a click handler that JS-redirects to an ad
    /// host passes (it carries the click's gesture). Timer/onload hijacks,
    /// which carry no gesture, still die.
    /// </summary>
    private bool ShouldCancelDocumentNav(CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.NavigationKind != CoreWebView2NavigationKind.NewDocument)
            return false;
        if (_expectSelfNav)
        {
            _expectSelfNav = false;
            return false;
        }
        if (e.IsUserInitiated)
            return false;
        var current = Browser.CoreWebView2?.Source;
        if (string.IsNullOrEmpty(current))
            return false;
        if (Uri.TryCreate(current, UriKind.Absolute, out var curi)
            && Uri.TryCreate(e.Uri, UriKind.Absolute, out var dest)
            && curi.Host.Equals(dest.Host, StringComparison.OrdinalIgnoreCase))
            return false;
        return RedirectBlockList.IsBlockedUri(e.Uri);
    }

    private void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _inflightNavs.Remove(e.NavigationId);
        if (_dupCancelledNavIds.Remove(e.NavigationId))
        {
            // Symmetric duplicate we cancelled — stay silent.
            PopupTrace.Write("nav-done", $"id={e.NavigationId} cancelled-dup");
            NavigationStateChanged?.Invoke(this, false);
            return;
        }
        if (_cancelledNavIds.Remove(e.NavigationId))
        {
            // Our own cancel — stay silent, keep the tab untouched.
            PopupTrace.Write("nav-done", $"id={e.NavigationId} cancelled-by-guard");
            NavigationStateChanged?.Invoke(this, false);
            return;
        }
        PopupTrace.Write("nav-done",
            $"id={e.NavigationId} ok={e.IsSuccess} host={PopupTrace.HostOf(CurrentUrl)} path={PopupTrace.PathOf(CurrentUrl)}"
            + (e.IsSuccess ? string.Empty : " err=" + e.WebErrorStatus));
        NavigationStateChanged?.Invoke(this, false);
        if (e.IsSuccess)
        {
            Navigated?.Invoke(this, CurrentUrl);
        }
        else
        {
            NavigationFailed?.Invoke(this, "Navigasi gagal: " + e.WebErrorStatus + ".");
        }
    }

    /// <summary>
    /// Resolves address bar input to a navigable URL using a 3-case heuristic:
    ///   1. Contains "://"  → treat as full URL, validate HTTP/HTTPS
    ///   2. No spaces, looks like domain → prepend "https://" and validate
    ///   3. Has spaces or doesn't parse as domain → Google Search
    /// Empty input returns false with an error message.
    /// </summary>
    private static bool TryNormalizeUrl(string value, out string target, out string error)
    {
        target = string.Empty;
        error = string.Empty;

        var input = value.Trim();
        if (input.Length == 0)
        {
            error = "Masukkan URL atau kata kunci pencarian.";
            return false;
        }

        // Case 1: Already has a scheme — treat as full URL
        if (input.Contains("://", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(input, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrWhiteSpace(uri.Host))
            {
                target = uri.AbsoluteUri;
                return true;
            }
            error = "URL tidak valid. Gunakan format https://example.com.";
            return false;
        }

        // Case 2: No spaces, looks like it could be a domain → try as URL
        if (!input.Contains(' '))
        {
            var candidate = "https://" + input;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrWhiteSpace(uri.Host)
                && uri.Host.Contains('.', StringComparison.Ordinal))
            {
                target = uri.AbsoluteUri;
                return true;
            }
        }

        // Case 3: Treat ordinary text as a Google query.
        target = "https://www.google.com/search?q=" + Uri.EscapeDataString(input);
        return true;
    }

    // ─── UI State ─────────────────────────────────────────────────────────

    private void UpdateVisualState()
    {
        LoadingOverlay.Visibility = _state == BrowserState.Initializing
            ? Visibility.Visible : Visibility.Collapsed;
        ErrorOverlay.Visibility = _state == BrowserState.Failed
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        await EnsureInitializedAsync();
    }

    // ─── Shared popup-session surface (one source of truth) ──────────────

    /// <summary>Realistic UA — default WebView2 UA looks like a bot.</summary>
    internal const string SharedUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";

    /// <summary>T2.2 bridge payload, injected into the main page and every popup.</summary>
    internal const string BridgeScript = @"
        window.__yuzvid = { version: '1.0.0', phase: 2 };
        window.__yuzvid.post = function (obj) { chrome.webview.postMessage(JSON.stringify(obj)); };
        window.__yuzvid.onCommand = function (cmd) { window.__yuzvid.post({ echo: cmd }); };
    ";

    internal static bool IsBlockedDomain(string uri)
    {
        foreach (var domain in BlockedDomains)
        {
            if (uri.Contains(domain, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ─── Ad Blocking ──────────────────────────────────────────────────────

    private static readonly string[] BlockedDomains =
    [
        "wearadmiration.com",
        "hikerfaquirs.com",
        "gibbingbegild.qpon",
        "runative-syndicate.com",
        "ads.google.com",
        "pagead2.googlesyndication.com",
        "www.googletagservices.com",
    ];

    private void OnAdBlockResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 cv2) return;
        var uri = e.Request.Uri;
        if (IsBlockedDomain(uri))
        {
            e.Response = cv2.Environment.CreateWebResourceResponse(
                null, 200, "Blocked", "Content-Type: text/plain");
            return;
        }

        // T3.3: non-blocked direct media → record for Extraction (Source B).
        if (IsLikelyMediaUri(uri))
            RecordVideoTap(uri);
    }

    /// <summary>
    /// Minimal extension check. The canonical media rule + host list live in
    /// Extraction.VideoLinkExtractor; this stays a local predicate on purpose —
    /// the hot request path must not call feature-to-feature, and hoisting 3
    /// lines into shared infra would be a tower for a predicate.
    /// </summary>
    internal static bool IsLikelyMediaUri(string uri)
    {
        var path = uri.Split('?', '#')[0];
        return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Each popup gets its own hidden session (concurrent, silent).
        // Before the manager exists (pre-init) fall back to silent drop so a
        // popup can never hijack the active Browser tab.
        var manager = _popupManager;
        if (manager is null)
        {
            e.Handled = true;
            return;
        }
        manager.HandleNewWindowRequest(e);
    }

    // ─── Silent-popup façade (R1: parent/controller talk only to these) ────

    /// <summary>Popup page scripts land here, then flow to ScriptMessageReceived.</summary>
    internal void ForwardPopupScriptMessage(string json)
        => ScriptMessageReceived?.Invoke(this, json);

    /// <summary>Synchronous + idempotent. Detach path: close all, reject new.</summary>
    internal void CloseSilentPopups(string reason)
    {
        if (Dispatcher.CheckAccess())
            _popupManager?.CloseAllTransient(reason, markDetached: true);
        else
            Dispatcher.Invoke(() => _popupManager?.CloseAllTransient(reason, markDetached: true));
    }

    /// <summary>Synchronous + idempotent. Attach path: accept new requests again.</summary>
    internal void ResumeSilentPopups()
    {
        if (Dispatcher.CheckAccess())
            _popupManager?.ClearDetached();
        else
            Dispatcher.Invoke(() => _popupManager?.ClearDetached());
    }

    /// <summary>
    /// Synchronous. Blocking Invoke (never BeginInvoke): Dispose must not
    /// return before every session is closed and the main WebView2 has
    /// released WebView2ProfileV2 (Stop → Start re-init fails with
    /// 0x8007139F otherwise).
    /// </summary>
    internal void Shutdown()
    {
        if (Dispatcher.CheckAccess())
            ShutdownCore();
        else
            Dispatcher.Invoke(ShutdownCore);
    }

    private void ShutdownCore()
    {
        // Popups first (they share the environment), then the main control.
        try { _popupManager?.Shutdown(); }
        catch { /* best effort */ }
        try { _popupManager?.Dispose(); }
        catch { /* best effort */ }
        _popupManager = null;

        _initializationTask = null;
        _pendingUrl = null;
        _environment = null;

        try
        {
            Browser.NavigationStarting -= Browser_NavigationStarting;
            Browser.NavigationCompleted -= Browser_NavigationCompleted;
            if (Browser.CoreWebView2 is not null)
            {
                Browser.CoreWebView2.WebResourceRequested -= OnAdBlockResourceRequested;
                Browser.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
            }
        }
        catch { /* control may never have initialized */ }

        // Critical: WPF WebView2 must be disposed or msedgewebview2 keeps
        // the user-data folder locked across Stop → Start.
        try { Browser.Dispose(); }
        catch { /* already dead */ }
    }

    /// <summary>
    /// CreateAsync on a profile whose previous browser process is still
    /// exiting throws 0x8007139F (ERROR_INVALID_STATE). Brief backoff and
    /// retry only that case; all other failures surface immediately.
    /// </summary>
    private async Task<CoreWebView2Environment> CreateEnvironmentWithRetryAsync(
        CoreWebView2EnvironmentOptions options)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: UserDataFolder,
                    options: options);
            }
            catch (Exception ex) when (
                attempt < maxAttempts && IsTransientProfileBusy(ex))
            {
                PopupTrace.Write("init", $"profile-busy retry {attempt}: {ex.Message}");
                await Task.Delay(100 * attempt);
            }
        }
    }

    private static bool IsTransientProfileBusy(Exception ex)
    {
        const int errorInvalidState = unchecked((int)0x8007139F);
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            if (current.HResult == errorInvalidState) return true;
            if (current.Message.Contains(
                    "correct state", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
