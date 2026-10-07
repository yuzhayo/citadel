using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Module.Yuzvid.Features.Browser;

namespace Module.Yuzvid.Features.Extraction;

/// <summary>One extraction pass over the current page (Source A ∪ Source B).</summary>
/// <param name="StoreKey">
/// Path of the persisted JSON for this pass, or null when persistence failed.
/// Callers pass this to <see cref="ExtractionFeature.ResolveStored"/> to read a
/// link back from disk instead of holding the live object.
/// </param>
public sealed record ExtractionResult(
    string PageUrl,
    IReadOnlyList<VideoLink> Links,
    bool StaticSkipped,
    string? StaticSkipReason,
    string? StoreKey = null);

/// <summary>
/// Entry point of the Extraction feature (parent talks ONLY to this).
/// Merges Source A (static DOM regex) with Source B (engine tap snapshot).
/// Source B wins ties so token-bearing CDN URIs survive dedup.
/// </summary>
public sealed class ExtractionFeature
{
    /// <summary>
    /// DOM dump guard: above this many chars the static scan is skipped
    /// with a status instead of freezing the UI on a giant regex.
    /// </summary>
    public const int MaxDomChars = 3_000_000;

    private const string DomDumpScript = "document.documentElement.outerHTML";

    private readonly IYuzvidBrowserController _browser;
    private ExtractionWindow? _window;

    public ExtractionFeature(IYuzvidBrowserController browser)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
    }

    /// <summary>Single-link copy requested from the extraction window.</summary>
    public event EventHandler<VideoLink>? CopyRequested;

    /// <summary>Single-link enqueue requested from the extraction window.</summary>
    public event EventHandler<VideoLink>? QueueRequested;

    /// <summary>Batch enqueue of every checked row.</summary>
    public event EventHandler<IReadOnlyList<VideoLink>>? QueueSelectionRequested;

    /// <summary>Re-run extraction for the current page.</summary>
    public event EventHandler? RescanRequested;

    /// <summary>
    /// Manual page-URL extraction requested from the window. The parent
    /// navigates the browser to the URL and runs extraction again (P2).
    /// </summary>
    public event EventHandler<string>? ExtractUrlRequested;

    /// <summary>
    /// Show (or refresh) the feature-owned floating extraction window. The window
    /// is created here — never by the parent — so the shell references no
    /// feature-declared type (guard YUZ-2). Reused on subsequent passes.
    /// </summary>
    public void ShowWindow(Window owner, ExtractionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (_window is null)
        {
            _window = new ExtractionWindow();
            if (owner is not null) _window.Owner = owner;
            _window.Closed += (_, _) => _window = null;
            _window.CopyRequested += (_, link) => CopyRequested?.Invoke(this, link);
            _window.QueueRequested += (_, link) => QueueRequested?.Invoke(this, link);
            _window.QueueSelectionRequested += (_, links) => QueueSelectionRequested?.Invoke(this, links);
            _window.RescanRequested += (_, _) => RescanRequested?.Invoke(this, EventArgs.Empty);
            _window.ExtractUrlRequested += (_, url) => ExtractUrlRequested?.Invoke(this, url);
        }

        _window.ShowResults(result);
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        // Owned + ShowInTaskbar=False windows can fail to come to front via
        // Activate() alone once the owner regains focus; force it (R2 fix).
        BringToFront(_window);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static void BringToFront(Window window)
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) SetForegroundWindow(handle);
        }
        catch
        {
            // best effort — never let focus handling break extraction
        }
    }

    public async Task<ExtractionResult> ExtractAsync()
    {
        var pageUrl = _browser.CurrentUrl ?? string.Empty;
        var merged = new List<VideoLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Source B first: live engine tap (token-bearing CDN URIs win ties).
        foreach (var uri in _browser.GetVideoRequestSnapshot())
        {
            var link = VideoLinkExtractor.TryBuildFromUri(uri);
            if (link is not null && seen.Add(link.DedupKey))
                merged.Add(link);
        }

        // Source A: rendered DOM regex (all embed servers on the page).
        bool skipped = false;
        string? skipReason = null;
        try
        {
            var json = await _browser.ExecuteScriptAsync(DomDumpScript);
            var html = JsonSerializer.Deserialize<string>(json);
            if (string.IsNullOrEmpty(html))
            {
                skipped = true;
                skipReason = "DOM kosong — halaman belum selesai dimuat.";
            }
            else if (html.Length > MaxDomChars)
            {
                skipped = true;
                skipReason = "Halaman terlalu besar, lewati scan statik.";
            }
            else
            {
                foreach (var link in VideoLinkExtractor.ExtractFromHtml(html))
                {
                    if (seen.Add(link.DedupKey))
                        merged.Add(link);
                }
            }
        }
        catch (InvalidOperationException)
        {
            skipped = true;
            skipReason = "Browser belum siap.";
        }

        // Persist as a backup: a restarted app (or a later Queue click) can
        // resolve links from disk. Failure only leaves StoreKey null.
        var result = new ExtractionResult(pageUrl, merged, skipped, skipReason);
        var storeKey = ExtractionStore.Save(result);
        return result with { StoreKey = storeKey };
    }

    /// <summary>
    /// Re-reads one link previously saved under <paramref name="storeKey"/>.
    /// Returns null when it cannot be resolved from disk — the caller falls
    /// back to its in-memory copy.
    /// </summary>
    public static VideoLink? ResolveStored(string? storeKey, string? linkKey)
        => ExtractionStore.ResolveLink(storeKey, linkKey);
}
