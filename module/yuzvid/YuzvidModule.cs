using System.Windows;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Module.Yuzvid.Features.Browser;
using Module.Yuzvid.Features.Extraction;
using Module.Yuzvid.Features.Queue;
using Module.Yuzvid.Features.Runtime;

namespace Module.Yuzvid;

/// <summary>
/// Shell entry point for the independent Yuzvid citizen. Composition root:
/// creates the Browser controller exactly once per retained view, activates it
/// (proxy listener + port pinning before WebView2 loads), owns its disposal
/// via the retained lifetime, builds the pre-wired Settings view, and hands
/// both ready-made pieces to the shell. The Browser never depends on Runtime.
/// </summary>
public sealed class YuzvidModule : IModule
{
    public string Route => "yuzvid";

    public FrameworkElement CreateView(Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        var browser = new YuzvidBrowserController();
        browser.Activate();
        lifetime.Add(browser.Dispose);
        var settings = new YuzvidRuntimeView();
        settings.Wire(browser);
        var extraction = new ExtractionFeature(browser);
        var queue = new QueueEngine(() => browser.DownloadProxy);
        lifetime.Add(queue.Dispose);
        var queueView = new QueueView(queue);
        return new YuzvidView(browser, settings, extraction, queue, queueView);
    }
}
