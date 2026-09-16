using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Module.Yuzvid.Features.Browser;

/// <summary>
/// Owns hidden WebView2 popup sessions. One <see cref="PopupSession"/> per
/// NewWindowRequested — concurrent requests never queue behind each other.
/// Owned by YuzvidBrowserView (never static, never leaves the Browser feature).
/// All state is touched on the UI Dispatcher only.
/// </summary>
internal sealed class SilentPopupHostManager : IDisposable
{
    private static readonly TimeSpan HardPopupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PopupIdleGrace = TimeSpan.FromSeconds(3);

    private readonly CoreWebView2Environment _environment;
    private readonly Dispatcher _ui;
    private readonly Action<string> _recordVideoTap;
    private readonly Action<string> _forwardScriptMessage;
    private readonly Func<string?> _getMainHost;
    private readonly Action<string> _navigateMain;

    private readonly Dictionary<Guid, PopupSession> _sessions = new();
    private bool _detached;
    private bool _shutdown;
    private bool _disposed;

    // Content hosts eligible for visible floating windows (gesture still
    // required — see gateway). COPIED (not imported) from
    // Extraction.VideoLinkExtractor.DefaultVideoHosts — same "port, don't
    // depend" rule as the media predicate (PLAN §2).
    // urllib_lib.py SHA256 DA64F48D…8BFD53. Same-host-as-tab is added dynamically.
    private static readonly string[] ContentVideoHosts =
    [
        "luluvdoo.com", "luluvid.com", "luluvdo.com", "dood.wf", "doodstream.co",
        "playmogo.com", "myvidplay.com",
        "voe.sx", "voe.pm", "voe.unblockit.top",
        "byseqekaho.com",
        "streamtape.com", "streamvid.net", "mixdrop.co",
        "alphaembed.cc", "embedsb.com",
    ];

    private bool IsContentHost(string? uri)
    {
        var host = PopupTrace.HostOf(uri);
        if (string.IsNullOrEmpty(host) || host == "?") return false;
        var main = _getMainHost();
        if (!string.IsNullOrEmpty(main) && main != "?"
            && host.Equals(main, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var known in ContentVideoHosts)
        {
            if (host.Equals(known, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + known, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public SilentPopupHostManager(
        CoreWebView2Environment environment,
        Dispatcher ui,
        Action<string> recordVideoTap,
        Action<string> forwardScriptMessage,
        Func<string?> getMainHost,
        Action<string> navigateMain)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _recordVideoTap = recordVideoTap ?? throw new ArgumentNullException(nameof(recordVideoTap));
        _forwardScriptMessage = forwardScriptMessage ?? throw new ArgumentNullException(nameof(forwardScriptMessage));
        _getMainHost = getMainHost ?? throw new ArgumentNullException(nameof(getMainHost));
        _navigateMain = navigateMain ?? throw new ArgumentNullException(nameof(navigateMain));
    }

    /// <summary>
    /// Shared NewWindowRequested handler (main view + every hidden core).
    /// Policy: content popups load in the MAIN tab (single visible tab);
    /// everything else runs hidden.
    /// Async-void is intentional: deferral pattern.
    /// </summary>
    internal async void HandleNewWindowRequest(CoreWebView2NewWindowRequestedEventArgs e)
    {
        var requestId = Guid.NewGuid();
        var toMain = IsContentHost(e.Uri);
        PopupTrace.Write("popup-req",
            $"req={requestId.ToString("N")[..8]} user={e.IsUserInitiated} host={PopupTrace.HostOf(e.Uri)} kind={(toMain ? "maintab" : "headless")}");
        if (toMain)
        {
            // No deferral dance: synchronous routing decision, main tab takes it.
            e.Handled = true;
            try { _navigateMain(e.Uri ?? string.Empty); }
            catch (Exception ex) { Trace("maintab navigate failed: " + ex.Message); }
            return;
        }
        var deferral = e.GetDeferral();
        try
        {
            await OpenSessionAsync(e, requestId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            e.Handled = true;
            Trace("request failed: " + ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task OpenSessionAsync(CoreWebView2NewWindowRequestedEventArgs request, Guid requestId)
    {
        if (_detached || _shutdown || _disposed)
        {
            request.Handled = true;
            return;
        }

        // Reserve BEFORE the first await so parallel requests each own a session.
        var session = new PopupSession { Id = Guid.NewGuid() };
        _sessions[session.Id] = session;
        Trace(session.Id + " accepted (req=" + requestId.ToString("N")[..8] + ")");
        ArmHardTimeout(session);

        try
        {
            session.Window = new HiddenPopupWindow();
            session.Window.Show(); // invisible host; required for reliable core init
            await session.Window.Web.EnsureCoreWebView2Async(_environment).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Trace(session.Id + " init-failed: " + ex.Message);
            CloseSession(session.Id, "init-failed");
            request.Handled = true;
            return;
        }

        // Detach/timeout may have closed the reservation while init was in flight.
        if (_detached || _shutdown || _disposed || !_sessions.ContainsKey(session.Id))
        {
            DisposeOrphan(session);
            request.Handled = true;
            return;
        }

        var core = session.Window.Web.CoreWebView2;
        if (core is null)
        {
            CloseSession(session.Id, "no-core");
            request.Handled = true;
            return;
        }

        session.Core = core;
        await ConfigureChildCoreAsync(core).ConfigureAwait(true);

        if (_detached || _shutdown || _disposed || !_sessions.ContainsKey(session.Id))
        {
            DisposeOrphan(session);
            request.Handled = true;
            return;
        }

        session.NavigationHandler = (_, _) => ArmIdleTimer(session);
        core.NavigationCompleted += session.NavigationHandler;
        session.NestedPopupHandler = (_, nested) => HandleNewWindowRequest(nested);
        core.NewWindowRequested += session.NestedPopupHandler;

        request.NewWindow = core;
        request.Handled = true;
        Trace(session.Id + " initialized");
    }

    /// <summary>
    /// One configuration for the hidden child core: same UA, same
    /// ad-block/tap filter, same bridge payload as the main Browser.
    /// </summary>
    private async Task ConfigureChildCoreAsync(CoreWebView2 core)
    {
        core.Settings.UserAgent = YuzvidBrowserView.SharedUserAgent;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnPopupResourceRequested;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(YuzvidBrowserView.BridgeScript)
            .ConfigureAwait(true);
        core.WebMessageReceived += OnPopupScriptMessage;
    }

    private void OnPopupResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core) return;
        var uri = e.Request.Uri;
        if (YuzvidBrowserView.IsBlockedDomain(uri))
        {
            e.Response = core.Environment.CreateWebResourceResponse(
                null, 200, "Blocked", "Content-Type: text/plain");
            return;
        }
        // Same fence as the main tab (shared RedirectBlockList).
        if (e.ResourceContext == CoreWebView2WebResourceContext.Document
            && RedirectBlockList.IsBlockedUri(uri))
        {
            PopupTrace.Write("redirect-block", "popup host=" + PopupTrace.HostOf(uri));
            e.Response = core.Environment.CreateWebResourceResponse(
                null, 200, "Blocked", "Content-Type: text/plain");
            return;
        }
        if (YuzvidBrowserView.IsLikelyMediaUri(uri))
            _recordVideoTap(uri);
    }

    private void OnPopupScriptMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try { _forwardScriptMessage(e.WebMessageAsJson); }
        catch (Exception ex) { Trace("script-forward failed: " + ex.Message); }
    }

    private void ArmHardTimeout(PopupSession session)
    {
        session.HardTimer = new DispatcherTimer(DispatcherPriority.Normal, _ui)
        {
            Interval = HardPopupTimeout,
        };
        var id = session.Id;
        session.HardTimer.Tick += (_, _) => CloseSession(id, "timeout");
        session.HardTimer.Start();
    }

    private void ArmIdleTimer(PopupSession session)
    {
        if (session.IdleTimer is null)
        {
            var id = session.Id;
            session.IdleTimer = new DispatcherTimer(DispatcherPriority.Background, _ui)
            {
                Interval = PopupIdleGrace,
            };
            session.IdleTimer.Tick += (_, _) => CloseSession(id, "idle");
        }
        session.IdleTimer.Stop();
        session.IdleTimer.Start();
        Trace(session.Id + " navigation-complete");
    }

    internal void CloseSession(Guid id, string reason)
    {
        if (!_ui.CheckAccess())
        {
            try { _ui.Invoke(() => CloseSession(id, reason)); }
            catch { /* dispatcher gone — timers below are dead with it */ }
            return;
        }

        if (!_sessions.Remove(id, out var session)) return; // idempotent

        try
        {
            session.HardTimer?.Stop();
            session.IdleTimer?.Stop();
            if (session.Core is not null)
            {
                if (session.NavigationHandler is not null)
                    session.Core.NavigationCompleted -= session.NavigationHandler;
                if (session.NestedPopupHandler is not null)
                    session.Core.NewWindowRequested -= session.NestedPopupHandler;
                session.Core.WebResourceRequested -= OnPopupResourceRequested;
                session.Core.WebMessageReceived -= OnPopupScriptMessage;
            }
            try { (session.Window?.Web as IDisposable)?.Dispose(); } catch { /* best effort */ }
            try { session.Window?.Close(); } catch { /* best effort */ }
        }
        catch { /* close must never throw */ }
        finally
        {
            Trace(id + " closed (" + reason + ")");
        }
    }

    private void DisposeOrphan(PopupSession session)
    {
        try { session.HardTimer?.Stop(); } catch { }
        try { session.IdleTimer?.Stop(); } catch { }
        try { (session.Window?.Web as IDisposable)?.Dispose(); } catch { }
        try { session.Window?.Close(); } catch { }
        _sessions.Remove(session.Id);
        Trace(session.Id + " orphan-disposed");
    }

    internal void CloseAllTransient(string reason, bool markDetached)
    {
        if (!_ui.CheckAccess())
        {
            try { _ui.Invoke(() => CloseAllTransient(reason, markDetached)); }
            catch { /* dispatcher gone */ }
            return;
        }

        if (markDetached) _detached = true;
        // Snapshot: closing mutates the dictionary.
        var ids = new List<Guid>(_sessions.Keys);
        foreach (var id in ids)
            CloseSession(id, reason);
    }

    internal void ClearDetached() => _detached = false;

    internal void Shutdown()
    {
        if (!_ui.CheckAccess())
        {
            try { _ui.Invoke(Shutdown); }
            catch { /* dispatcher gone — nothing left to close */ }
            return;
        }

        _shutdown = true;
        CloseAllTransient("dispose", markDetached: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Shutdown(); }
        catch { /* dispose never throws */ }
        _sessions.Clear();
    }

    private static void Trace(string message)
        => PopupTrace.Write("popup", message);

    /// <summary>Per-request state. Touched on the UI Dispatcher only.</summary>
    private sealed class PopupSession
    {
        public Guid Id;
        public HiddenPopupWindow? Window;
        public CoreWebView2? Core;
        public DispatcherTimer? HardTimer;
        public DispatcherTimer? IdleTimer;
        public EventHandler<CoreWebView2NavigationCompletedEventArgs>? NavigationHandler;
        public EventHandler<CoreWebView2NewWindowRequestedEventArgs>? NestedPopupHandler;
    }

    /// <summary>
    /// Invisible host: never in the taskbar, never activated, zero opacity,
    /// parked far off-screen. Shown only because WebView2 needs a shown host
    /// for reliable core initialization.
    /// </summary>
    private sealed class HiddenPopupWindow : Window
    {
        public WebView2 Web { get; }

        public HiddenPopupWindow()
        {
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = 1280;
            Height = 720;
            Left = -10000;
            Top = -10000;
            Opacity = 0;
            Web = new WebView2();
            Content = Web;
        }
    }
}
