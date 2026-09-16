using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Module.Yuzvid.Features.Browser;

namespace Module.Yuzvid.Features.Extraction;

/// <summary>One extraction pass over the current page (Source A ∪ Source B).</summary>
public sealed record ExtractionResult(
    string PageUrl,
    IReadOnlyList<VideoLink> Links,
    bool StaticSkipped,
    string? StaticSkipReason);

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

    public ExtractionFeature(IYuzvidBrowserController browser)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
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

        return new ExtractionResult(pageUrl, merged, skipped, skipReason);
    }
}
