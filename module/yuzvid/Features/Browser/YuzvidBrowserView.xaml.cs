using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace Module.Yuzvid.Features.Browser;

public enum BrowserState
{
    Initializing,
    Ready,
    Failed
}

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

        try
        {
            // The local adapter owns proxy selection; WebView2 has one stable
            // localhost route for its entire retained session.
            var envOptions = new CoreWebView2EnvironmentOptions();
            if (_proxyPort > 0)
                envOptions.AdditionalBrowserArguments = $"--proxy-server=127.0.0.1:{_proxyPort}";

            Directory.CreateDirectory(UserDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: UserDataFolder,
                options: envOptions);

            await Browser.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            State = BrowserState.Failed;
            ErrorTitle.Text = "Browser engine failed to initialize";
            ErrorDetail.Text = ex.Message;
            return;
        }

        var cv2 = Browser.CoreWebView2;

        // Set realistic user-agent — default WebView2 UA looks like a bot
        cv2.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";

        // Register ad-block filter (must be added before event fires)
        cv2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);

        // Block ad domains at network level
        cv2.WebResourceRequested += OnAdBlockResourceRequested;

        // Block popups — redirect to same tab
        cv2.NewWindowRequested += OnNewWindowRequested;

        // Phase 2 placeholder — floating controls will be injected here
        await cv2.AddScriptToExecuteOnDocumentCreatedAsync(@"
            window.__yuzvid = { version: '1.0.0', phase: 1 };
        ");

        // Wire navigation events
        Browser.NavigationStarting += Browser_NavigationStarting;
        Browser.NavigationCompleted += Browser_NavigationCompleted;

        State = BrowserState.Ready;

        // Navigate to pending URL if user typed one before init completed
        if (!string.IsNullOrEmpty(_pendingUrl))
        {
            cv2.Navigate(_pendingUrl);
            _pendingUrl = null;
        }
    }

    private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        NavigationStateChanged?.Invoke(this, true);
    }

    private void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
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
        foreach (var domain in BlockedDomains)
        {
            if (uri.Contains(domain, StringComparison.OrdinalIgnoreCase))
            {
                e.Response = cv2.Environment.CreateWebResourceResponse(
                    null, 200, "Blocked", "Content-Type: text/plain");
                return;
            }
        }
    }

    private int _popupNavCount;
    private DateTime _popupNavReset = DateTime.UtcNow;

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        // Rate limit: max 5 new-window navigations per 3 seconds
        // Prevents popup bomb while allowing normal target="_blank" links
        var now = DateTime.UtcNow;
        if (now - _popupNavReset > TimeSpan.FromSeconds(3))
        {
            _popupNavCount = 0;
            _popupNavReset = now;
        }
        _popupNavCount++;
        if (_popupNavCount > 5) return; // bomb protection — stop navigating

        if (!string.IsNullOrEmpty(e.Uri) && State == BrowserState.Ready)
            Browser.CoreWebView2?.Navigate(e.Uri);
    }
}
