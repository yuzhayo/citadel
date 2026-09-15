using System;
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

    void Navigate(string input);
    void GoBack();
    void GoForward();
    void Refresh();
    void SetProxyEnabled(bool enabled);
    void SetDnsMode(YuzvidDnsMode mode);
    Task RetryInitAsync();
}
