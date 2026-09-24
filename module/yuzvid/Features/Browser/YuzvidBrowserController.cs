using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using CitadelBridge;

namespace Module.Yuzvid.Features.Browser;

/// <summary>
/// Owns one <see cref="YuzvidBrowserView"/> plus its proxy subsystem
/// (<see cref="LocalProxyServer"/> + <see cref="YuzvidProxyPoolAdapter"/>).
///
/// R1 note: this controller is passive — it is constructed but <see cref="Activate"/>
/// is never called yet, so no listener starts and the parent's baseline Browser
/// stays the only active one. R2 performs the atomic cutover. The controller's
/// own view is never mounted before <see cref="Activate"/>, so its WebView2
/// never initializes early (init is OnLoaded-triggered).
/// </summary>
public sealed class YuzvidBrowserController : IYuzvidBrowserController, IDisposable
{
    private readonly YuzvidBrowserView _view = new();
    private readonly YuzvidProxyPoolAdapter _proxyAdapter = new();
    private LocalProxyServer? _localProxy;
    private string? _notice;
    private bool _disposed;

    public YuzvidBrowserController()
    {
        _view.Navigated += (s, url) => Navigated?.Invoke(this, url);
        _view.NavigationStateChanged += (s, navigating) => NavigationStateChanged?.Invoke(this, navigating);
        _view.NavigationFailed += (s, message) => NavigationFailed?.Invoke(this, message);
        _view.NavigationQueued += (s, message) => NavigationQueued?.Invoke(this, message);
        _view.StateChanged += (s, state) => StateChanged?.Invoke(this, state);
        _view.ScriptMessageReceived += (s, json) => ScriptMessageReceived?.Invoke(this, json);
    }

    /// <summary>
    /// Starts the local proxy listener and pins the owned view to it.
    /// Must run exactly once, before the view enters the visual tree
    /// (WebView2 reads the port at load). Falls back to direct mode
    /// (port 0) when the listener cannot start — same as baseline.
    /// </summary>
    public void Activate()
    {
        if (_localProxy is not null) return;

        try
        {
            var proxy = new LocalProxyServer();
            proxy.Start();
            _localProxy = proxy;
            _view.SetLocalProxyPort(proxy.Port);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Yuzvid] LocalProxy failed: {ex.Message}");
            // Browser runs direct (no proxy) — still functional.
            _view.SetLocalProxyPort(0);
        }
    }

    public FrameworkElement View => _view;
    public BrowserState State => _view.State;
    public string CurrentUrl => _view.CurrentUrl;
    public string CurrentTitle => _view.CurrentTitle;
    public bool CanGoBack => _view.CanGoBack;
    public bool CanGoForward => _view.CanGoForward;

    public BrowserRuntimeSnapshot Runtime => new(
        _localProxy?.ProxyEnabled ?? false,
        _localProxy?.Upstream?.Masked ?? "none",
        _proxyAdapter.AvailableCount,
        _notice);

    public event EventHandler<string>? Navigated;
    public event EventHandler<bool>? NavigationStateChanged;
    public event EventHandler<string>? NavigationFailed;
    public event EventHandler<string>? NavigationQueued;
    public event EventHandler<BrowserState>? StateChanged;
    public event EventHandler<string>? ScriptMessageReceived;

    public void Navigate(string input) => _view.Navigate(input);
    public void GoBack() => _view.GoBack();
    public void GoForward() => _view.GoForward();
    public void Refresh() => _view.Refresh();
    public Task RetryInitAsync() => _view.RetryInitAsync();
    public Task<string> ExecuteScriptAsync(string script) => _view.ExecuteScriptAsync(script);
    public IReadOnlyList<string> GetVideoRequestSnapshot() => _view.GetVideoRequestSnapshot();
    public void ClearVideoRequests() => _view.ClearVideoRequests();

    public System.Net.IWebProxy? DownloadProxy
    {
        get
        {
            var port = _view.LocalProxyPort;
            return port > 0
                ? new System.Net.WebProxy($"http://127.0.0.1:{port}")
                : null;
        }
    }

    /// <summary>
    /// Mirrors the baseline toolbar toggle semantics: adapter flag first,
    /// direct on disable, acquire+attach on enable, self-heal to direct on
    /// pool failure. Outcome lands in <see cref="Runtime"/> as Notice —
    /// no exception escapes to the parent.
    /// </summary>
    public void SetProxyEnabled(bool enabled)
    {
        _proxyAdapter.Enabled = enabled;

        if (!enabled)
        {
            _localProxy?.Upstream = null;
            _notice = "Browser memakai koneksi langsung.";
            return;
        }

        try
        {
            var endpoint = _proxyAdapter.Acquire();
            if (_localProxy is null)
            {
                throw new ProxyPoolException("LOCAL_PROXY_UNAVAILABLE", "Local browser proxy tidak aktif.");
            }

            _localProxy.Upstream = endpoint;
            _notice = "Proxy aktif: " + endpoint!.Masked;
        }
        catch (ProxyPoolException exception)
        {
            _proxyAdapter.Enabled = false;
            _localProxy?.Upstream = null;
            _notice = exception.Message + " Browser memakai koneksi langsung.";
        }
    }

    public void SetDnsMode(YuzvidDnsMode mode)
    {
        if (_localProxy is null) return;

        _localProxy.DnsMode = mode switch
        {
            YuzvidDnsMode.Cloudflare => BrowserDnsMode.Cloudflare,
            YuzvidDnsMode.Google => BrowserDnsMode.Google,
            _ => BrowserDnsMode.System,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Ordered teardown: popups + main WebView2 first (blocking — releases
        // WebView2ProfileV2), proxy last. Dispose must never throw — Lifetime
        // callbacks run at Stop on the UI thread.
        try { _view.Shutdown(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Yuzvid] browser shutdown: {ex.Message}");
        }
        _localProxy?.Dispose();
        _localProxy = null;
    }

    public void CloseTransientHosts() => _view.CloseSilentPopups("detach");

    public void ResumeTransientHosts() => _view.ResumeSilentPopups();
}
