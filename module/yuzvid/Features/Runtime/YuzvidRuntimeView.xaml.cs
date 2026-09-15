using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Module.Yuzvid.Features.Browser;

namespace Module.Yuzvid.Features.Runtime;

public partial class YuzvidRuntimeView : UserControl
{
    private Func<BrowserState>? _getBrowserState;
    private Func<Task>? _retryBrowserInit;
    private Func<(bool enabled, string masked, int available)>? _getProxyStatus;
    private bool _activated;

    public YuzvidRuntimeView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Wire browser state accessor and retry callback from parent view.
    /// </summary>
    internal void Wire(
        Func<BrowserState> getBrowserState,
        Func<Task> retryBrowserInit,
        Func<(bool enabled, string masked, int available)>? getProxyStatus = null)
    {
        _getBrowserState = getBrowserState;
        _retryBrowserInit = retryBrowserInit;
        _getProxyStatus = getProxyStatus;
    }

    internal async Task ActivateAsync()
    {
        if (_activated) return;
        _activated = true;
        await RefreshStatusAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshStatusAsync();

    private async Task RefreshStatusAsync()
    {
        RefreshButton.IsEnabled = false;
        try
        {
            // Check runtime (WebView2Loader.dll location)
            var runtimeOk = CheckRuntime(out var runtimeDetail);
            SetRow(RuntimeMark, RuntimeDetail, runtimeOk, runtimeDetail);

            // Check SDK (assembly loaded)
            var sdkOk = CheckSdk(out var sdkDetail);
            SetRow(SdkMark, SdkDetail, sdkOk, sdkDetail);

            // Check browser state
            var browserState = _getBrowserState?.Invoke();
            var stateOk = browserState == BrowserState.Ready;
            var stateDetail = browserState switch
            {
                BrowserState.Initializing => "initializing…",
                BrowserState.Ready => "ready",
                BrowserState.Failed => "failed — check Browser tab for details",
                _ => "unknown"
            };
            SetRow(StateMark, StateDetail, stateOk, stateDetail);
            RetryButton.IsEnabled = browserState is BrowserState.Failed;

            // Check proxy status
            var (proxyEnabled, masked, available) = _getProxyStatus?.Invoke() ?? (false, "none", 0);
            var proxyOk = proxyEnabled;
            var proxyDetail = proxyEnabled
                ? $"routing via {masked} ({available} in pool)"
                : available > 0
                    ? $"direct ({available} proxies available in pool)"
                    : "direct (no proxy pool)";
            SetRow(ProxyMark, ProxyDetail, proxyOk || available > 0, proxyDetail);

            StatusText.Text = "Status checked at " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            StatusText.Text = "status check failed: " + ex.Message;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private static bool CheckRuntime(out string detail)
    {
        // Look for WebView2Loader.dll in the Shell output directory
        var baseDir = AppContext.BaseDirectory;
        var loaderPath = Path.Combine(baseDir, "WebView2Loader.dll");

        if (!File.Exists(loaderPath))
        {
            detail = "WebView2Loader.dll not found at " + baseDir;
            return false;
        }

        var size = new FileInfo(loaderPath).Length;
        detail = "WebView2Loader.dll (" + (size / 1024) + " KB) at " + baseDir;
        return true;
    }

    private static bool CheckSdk(out string detail)
    {
        try
        {
            var asm = typeof(Microsoft.Web.WebView2.Wpf.WebView2).Assembly;
            var version = asm.GetName().Version;
            if (version is null)
            {
                detail = "Microsoft.Web.WebView2 loaded (version unknown)";
                return true;
            }
            detail = "Microsoft.Web.WebView2 v" + version.ToString(3);
            return true;
        }
        catch
        {
            detail = "Microsoft.Web.WebView2 not loaded";
            return false;
        }
    }

    private static void SetRow(TextBlock mark, TextBlock detail, bool ok, string text)
    {
        mark.Text = ok ? "✓" : "✗";
        mark.Foreground = ok
            ? (Brush)Application.Current.FindResource("Accent")
            : (Brush)Application.Current.FindResource("Body");
        detail.Text = text;
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_retryBrowserInit is null) return;

        RetryButton.IsEnabled = false;
        StatusText.Text = "Retrying browser initialization…";
        await _retryBrowserInit();
        await RefreshStatusAsync();
    }
}
