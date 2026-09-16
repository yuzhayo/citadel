using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

namespace Module.Yuzvid.Features.Browser;

/// <summary>
/// WebView2 readiness. Public contract — moved here from YuzvidBrowserView
/// so the parent can observe Browser state without importing view internals.
/// </summary>
public enum BrowserState
{
    Initializing,
    Ready,
    Failed
}

/// <summary>
/// Public DNS selector. The parent speaks this; the Browser feature translates
/// it to the internal <see cref="BrowserDnsMode"/> — internals never leak out.
/// </summary>
public enum YuzvidDnsMode
{
    System,
    Cloudflare,
    Google,
}

/// <summary>
/// Immutable point-in-time Browser/proxy status for the toolbar and Runtime tab.
/// <c>Notice</c> carries the human-readable outcome of the last proxy/DNS action
/// so the parent can display it without catching feature-infra exceptions.
/// </summary>
public sealed record BrowserRuntimeSnapshot(
    bool ProxyEnabled,
    string EndpointLabel,
    int AvailableCount,
    string? Notice);

/// <summary>
/// Public contract of the Browser feature. The parent (YuzvidView) and the
/// composition root (YuzvidModule) talk to the Browser ONLY through this.
/// </summary>
public interface IYuzvidBrowserController
{
    FrameworkElement View { get; }
    BrowserState State { get; }
    string CurrentUrl { get; }
    string CurrentTitle { get; }
    bool CanGoBack { get; }
    bool CanGoForward { get; }
    BrowserRuntimeSnapshot Runtime { get; }

    event EventHandler<string>? Navigated;
    event EventHandler<bool>? NavigationStateChanged;
    event EventHandler<string>? NavigationFailed;

    /// <summary>
    /// Raised when a valid URL is held until the browser is ready.
    /// Kept in the contract to preserve the baseline "menunggu browser" status.
    /// </summary>
    event EventHandler<string>? NavigationQueued;

    event EventHandler<BrowserState>? StateChanged;

    /// <summary>
    /// JS→C# half of the bridge. JSON string posted by page scripts.
    /// Added T2.1 (was the CS0067 dead event on the view).
    /// </summary>
    event EventHandler<string>? ScriptMessageReceived;

    void Navigate(string input);
    void GoBack();
    void GoForward();
    void Refresh();
    void SetProxyEnabled(bool enabled);
    void SetDnsMode(YuzvidDnsMode mode);
    Task RetryInitAsync();

    /// <summary>
    /// C#→JS half of the bridge. Returns the JSON-encoded result.
    /// Added T2.2 — the Extraction feature uses this for DOM dumps (T3.2).
    /// </summary>
    Task<string> ExecuteScriptAsync(string script);

    /// <summary>
    /// Immutable snapshot of recently seen direct-media URIs (Source B tap).
    /// Added T3.3 — the ONLY tap data crossing the feature boundary.
    /// </summary>
    IReadOnlyList<string> GetVideoRequestSnapshot();

    void ClearVideoRequests();

    /// <summary>
    /// Proxy for out-of-browser downloads (Queue engine). Framework-typed so
    /// consumers never learn Browser internals. Null = direct connection.
    /// Added T4.1 — routes downloads through the same local proxy as browsing.
    /// </summary>
    System.Net.IWebProxy? DownloadProxy { get; }

    /// <summary>
    /// Detach path: close every hidden popup session; new popup requests are
    /// rejected until <see cref="ResumeTransientHosts"/>. Sync + idempotent.
    /// </summary>
    void CloseTransientHosts();

    /// <summary>
    /// Attach path: accept new popup requests again. Old sessions stay closed.
    /// Sync + idempotent.
    /// </summary>
    void ResumeTransientHosts();
}
