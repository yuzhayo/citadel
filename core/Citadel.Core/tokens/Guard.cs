namespace Citadel.Core.Tokens;

public enum GuardVerdict { Warned, Clamped, Refused }

public sealed record GuardIssue(string Token, GuardVerdict Verdict, string Message);

/// <summary>
/// Runs on load, on theme activation, and on commit — theme activation
/// swaps a whole override set at once, so a theme carrying a
/// contrast-failing pair would otherwise walk straight past.
///
/// Verdicts: contrast below threshold → warn, allow; non-positive size →
/// clamp to default, warn; contradictory Full bounds → revert the
/// overrides; a rail wider than FullMax → revert both, because expanding
/// must never narrow the sidebar; TitleX past the rail → clamp to the rail;
/// FullDefault outside [max(FullMin, Rail), FullMax] → clamp; window initial
/// size below its minimum → clamp the initial size to the minimum; fully
/// transparent foreground → refuse.
///
/// FullMin below Rail is deliberate, so the rail acts
/// as FullDefault's effective floor instead of forcing FullMin upwards.
/// </summary>
public static class Guard
{
    /// <summary>
    /// Validates and sanitizes a sparse core-override set in place.
    /// Clamped/refused overrides are corrected or removed; contrast
    /// failures only warn. Returns what happened, so the settings UI
    /// can surface it.
    /// </summary>
    public static IReadOnlyList<GuardIssue> Check(Dictionary<string, TokenValue> core)
    {
        var issues = new List<GuardIssue>();
        TokenValue Resolve(string token) =>
            core.TryGetValue(token, out var o) ? o : Defaults.All[token];

        // Fully transparent foreground: refuse.
        var foregrounds = TokenPairs.Contrast.Select(p => p.Fg).Distinct();
        foreach (var fg in foregrounds.ToList())
        {
            if (core.TryGetValue(fg, out var v) && v.A == 0)
            {
                core.Remove(fg);
                issues.Add(new GuardIssue(fg, GuardVerdict.Refused,
                    "fully transparent foreground; override dropped"));
            }
        }

        // Non-positive metrics: clamp to default.
        foreach (var (token, value) in core.ToList())
        {
            if (value.Kind == TokenKind.Number && value.Number <= 0)
            {
                core.Remove(token);
                issues.Add(new GuardIssue(token, GuardVerdict.Clamped,
                    $"{value.Format()} is zero or negative; reverted to default {Defaults.All[token].Format()}"));
            }
        }

        // Title offset beyond the rail: clamp to the rail. Checked against
        // *resolved* values, not just overridden ones — narrowing Rail alone
        // breaks this invariant while TitleX sits untouched at its default,
        // and the violation is what gets drawn either way.
        var titleX = Resolve("TitleX").Number;
        if (titleX > Resolve("Rail").Number)
        {
            var railWidth = Resolve("Rail").Number;
            core["TitleX"] = TokenValue.OfNumber(railWidth);
            issues.Add(new GuardIssue("TitleX", GuardVerdict.Clamped,
                $"offset {titleX} is beyond rail width; clamped to {railWidth}"));
        }

        // Bounds are a relationship invariant. Revert their overrides rather
        // than swapping values: a typo must not silently reverse what the user
        // meant by minimum and maximum. This is a local reconciliation function
        // because repairing Rail/FullMax below can make the relationship invalid
        // again; every repair must leave all earlier postconditions true.
        (double Min, double Max) ReconcileFullBounds()
        {
            var resolvedMin = Resolve("FullMin").Number;
            var resolvedMax = Resolve("FullMax").Number;
            if (resolvedMin <= resolvedMax) return (resolvedMin, resolvedMax);

            if (core.Remove("FullMin"))
            {
                issues.Add(new GuardIssue("FullMin", GuardVerdict.Clamped,
                    $"minimum {resolvedMin} exceeds maximum {resolvedMax}; reverted to default"));
            }
            if (core.Remove("FullMax"))
            {
                issues.Add(new GuardIssue("FullMax", GuardVerdict.Clamped,
                    $"maximum {resolvedMax} is below minimum {resolvedMin}; reverted to default"));
            }

            return (Resolve("FullMin").Number, Resolve("FullMax").Number);
        }

        var (min, max) = ReconcileFullBounds();

        // The rail is the collapsed width, so the expanded ceiling can never be
        // below it — that state makes expanding *narrow* the sidebar. Rail and
        // FullMax are two sides of one relationship, so the same treatment as
        // FullMin/FullMax applies: revert the participating overrides rather
        // than picking a winner, because either one could be the typo.
        var rail = Resolve("Rail").Number;
        if (rail > max)
        {
            if (core.Remove("Rail"))
            {
                issues.Add(new GuardIssue("Rail", GuardVerdict.Clamped,
                    $"rail {rail} exceeds maximum width {max}; reverted to default"));
            }
            if (core.Remove("FullMax"))
            {
                issues.Add(new GuardIssue("FullMax", GuardVerdict.Clamped,
                    $"maximum width {max} is below rail {rail}; reverted to default"));
            }
            rail = Resolve("Rail").Number;
            (min, max) = ReconcileFullBounds();

            // Reverting Rail can leave TitleX past the restored rail, and that
            // check already ran. Re-apply it rather than leaving the earlier
            // invariant broken by this one's repair.
            var restoredTitleX = Resolve("TitleX").Number;
            if (restoredTitleX > rail)
            {
                core["TitleX"] = TokenValue.OfNumber(rail);
                issues.Add(new GuardIssue("TitleX", GuardVerdict.Clamped,
                    $"offset {restoredTitleX} is beyond rail width; clamped to {rail}"));
            }
        }

        // Full width stays inside [max(FullMin, Rail), FullMax] — again
        // resolved, not just overridden. Moving a bound alone is the common
        // edit, and it is exactly the case where FullDefault is absent from the
        // set. FullMin below Rail is deliberate, so the
        // rail is the effective floor without forcing FullMin up.
        var floor = Math.Max(min, rail);
        var full = Resolve("FullDefault").Number;
        var clamped = Math.Clamp(full, floor, max);
        if (clamped != full)
        {
            core["FullDefault"] = TokenValue.OfNumber(clamped);
            issues.Add(new GuardIssue("FullDefault", GuardVerdict.Clamped,
                $"{full} outside [{floor}, {max}]; clamped to {clamped}"));
        }

        // A WPF minimum is a live constraint while WindowW/WindowH are the
        // initial size. If the initial value is smaller, WPF silently grows the
        // window and the resolved tokens no longer describe what opens. Treat
        // the initial size like FullDefault: pull it up to the resolved bound.
        ClampWindowInitial("WindowW", "WindowMinW");
        ClampWindowInitial("WindowH", "WindowMinH");

        // Contrast: warn, allow. Checked last, against sanitized values.
        foreach (var (fg, bg) in TokenPairs.Contrast)
        {
            var ratio = ContrastRatio(Resolve(fg), Resolve(bg));
            if (ratio < TokenPairs.MinContrast)
            {
                issues.Add(new GuardIssue(fg, GuardVerdict.Warned,
                    $"contrast {ratio:F2} against {bg} is below {TokenPairs.MinContrast}"));
            }
        }

        return issues;

        void ClampWindowInitial(string initialToken, string minimumToken)
        {
            var initial = Resolve(initialToken).Number;
            var minimum = Resolve(minimumToken).Number;
            if (initial >= minimum) return;

            core[initialToken] = TokenValue.OfNumber(minimum);
            issues.Add(new GuardIssue(initialToken, GuardVerdict.Clamped,
                $"initial size {initial} is below {minimumToken} {minimum}; clamped to {minimum}"));
        }
    }

    /// <summary>WCAG 2.x relative-luminance contrast ratio.</summary>
    public static double ContrastRatio(TokenValue a, TokenValue b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(TokenValue c)
    {
        double Channel(byte v)
        {
            var s = v / 255d;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
