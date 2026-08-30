namespace Citadel.Core.Tokens;

/// <summary>
/// Contrast needs declared pairs — a flat list cannot infer that
/// NavTitleFg is drawn on BgRail. Upstream silently adjusts
/// (EnsureContrast); Citadel warns instead, because silent adjustment
/// makes a direct-manipulation edit look like it didn't apply.
/// </summary>
public static class TokenPairs
{
    /// <summary>The minimum contrast pairs enforced at launch.</summary>
    public static readonly IReadOnlyList<(string Fg, string Bg)> Contrast =
    [
        ("Fg", "Bg"),
        ("Fg", "BgRail"),
        ("Dim", "BgRail"),
        ("Accent", "BgRail"),
        ("Fg", "Hover"),
        ("Fg", "Selected"),
        ("Fg", "Card"),
        ("Body", "Card"),
    ];

    /// <summary>WCAG AA for normal text.</summary>
    public const double MinContrast = 4.5;
}
