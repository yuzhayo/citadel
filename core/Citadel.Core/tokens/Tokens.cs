using System.Globalization;
using System.Text.Json.Nodes;

namespace Citadel.Core.Tokens;

public enum TokenKind { Number, Color }

/// <summary>
/// One token value: either a metric (double) or a color (ARGB).
/// Colors format as #RRGGBB when opaque, #AARRGGBB otherwise.
/// </summary>
public readonly record struct TokenValue(TokenKind Kind, double Number, uint Argb)
{
    public static TokenValue OfNumber(double value) => new(TokenKind.Number, value, 0);

    public static TokenValue OfColor(uint argb) => new(TokenKind.Color, 0, argb);

    public static bool TryParse(TokenKind kind, JsonNode? node, out TokenValue value)
    {
        value = default;
        if (node is null) return false;

        if (kind == TokenKind.Number)
        {
            // JsonValue.TryGetValue does no numeric conversion (int never
            // satisfies double), so parse the raw JSON number instead.
            if (node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            {
                if (double.TryParse(v.ToJsonString(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
                {
                    value = OfNumber(d);
                    return true;
                }
            }
            return false;
        }

        if (node is JsonValue s && s.TryGetValue<string>(out var text))
        {
            return TryParseColor(text, out value);
        }
        return false;
    }

    public static bool TryParseColor(string text, out TokenValue value)
    {
        value = default;
        if (!text.StartsWith('#')) return false;
        var hex = text[1..];
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }
        value = hex.Length switch
        {
            6 => OfColor(0xFF000000 | rgb),
            8 => OfColor(rgb),
            _ => default,
        };
        return hex.Length is 6 or 8;
    }

    public JsonNode ToJson() => Kind == TokenKind.Number
        ? JsonValue.Create(Number)
        : JsonValue.Create(Format());

    public string Format()
    {
        if (Kind == TokenKind.Number) return Number.ToString(CultureInfo.InvariantCulture);
        var a = (Argb >> 24) & 0xFF;
        var rgb = Argb & 0xFFFFFF;
        return a == 0xFF
            ? $"#{rgb:X6}"
            : $"#{Argb:X8}";
    }

    public byte A => (byte)((Argb >> 24) & 0xFF);
    public byte R => (byte)((Argb >> 16) & 0xFF);
    public byte G => (byte)((Argb >> 8) & 0xFF);
    public byte B => (byte)(Argb & 0xFF);
}

/// <summary>
/// The token store. Read is override ?? default, so every token always
/// resolves — there is no partial state. One engine, two consumers:
/// core chrome tokens and module screen layout overrides keyed by
/// route. Not a static class: the shell owns one instance.
/// </summary>
public sealed class Tokens
{
    public const string DefaultThemeName = "default";

    private readonly object _gate = new();
    private readonly Dictionary<string, Theme> _themes = new(StringComparer.Ordinal);
    private Theme _active;

    /// <summary>
    /// Transient resolved snapshot, never persisted. Preview values still pass
    /// the guard before publication so every TokensChanged subscriber sees a
    /// coherent geometry set; release is the only path that mutates the active
    /// sparse override set and saves it.
    /// </summary>
    private Dictionary<string, TokenValue>? _preview;

    public Tokens(string? storePath = null)
    {
        StorePath = storePath ?? Overrides.DefaultStorePath();
        _active = new Theme(DefaultThemeName);
        _themes[DefaultThemeName] = _active;
    }

    /// <summary>One signal: anything custom-drawn or cached rebuilds on it.</summary>
    public event Action? TokensChanged;

    public string StorePath { get; }

    public string ActiveThemeName { get { lock (_gate) return _active.Name; } }

    public IReadOnlyList<string> ThemeNames
    {
        get { lock (_gate) return [.. _themes.Keys]; }
    }

    /// <summary>Override ?? default. Unknown tokens are caller bugs and throw.</summary>
    public TokenValue Resolve(string token)
    {
        lock (_gate)
        {
            // Preview first: a live drag has to repaint real chrome, and
            // anything custom-drawn reads through here rather than through a
            // resource dictionary.
            if (_preview is not null && _preview.TryGetValue(token, out var p)) return p;
            if (_active.Core.TryGetValue(token, out var o)) return o;
            if (Defaults.TryGet(token, out var d)) return d;
            throw new ArgumentException($"unknown token '{token}'", nameof(token));
        }
    }

    public double Number(string token) => Resolve(token).Number;

    public uint Color(string token) => Resolve(token).Argb;

    /// <summary>Layout overrides for a route (deep copy), or null when none.</summary>
    public JsonObject? LayoutOverrides(string route)
    {
        lock (_gate)
        {
            return _active.Layouts.TryGetValue(route, out var slots)
                ? slots.DeepClone().AsObject()
                : null;
        }
    }

    /// <summary>
    /// Set one core token. <see cref="TokenCommitResult.Applied"/> makes a
    /// clean success distinguishable from an ignored edit with no issues.
    /// </summary>
    public TokenCommitResult CommitCore(string token, JsonNode? value)
    {
        lock (_gate)
        {
            // A commit is the authoritative value; any stale preview for the
            // same token would otherwise keep shadowing it in Resolve.
            _preview = null;

            if (!Defaults.TryGet(token, out var def))
            {
                Log.Main($"[Tokens] commit to unknown token '{token}' ignored");
                return new TokenCommitResult(false, [], $"unknown token '{token}'");
            }
            if (!TokenValue.TryParse(def.Kind, value, out var parsed))
            {
                Log.Main($"[Tokens] invalid value for '{token}' ignored");
                return new TokenCommitResult(false, [], $"invalid value for '{token}'");
            }

            var hadPrior = _active.Core.TryGetValue(token, out var prior);
            var candidate = new Dictionary<string, TokenValue>(_active.Core) { [token] = parsed };
            var issues = Guard.Check(candidate);
            var applied = candidate.ContainsKey(token)
                && !issues.Any(issue => issue.Token == token && issue.Verdict == GuardVerdict.Refused);

            string? rejection = null;
            if (!applied)
            {
                // A rejected edit must change nothing. Leaving the key dropped
                // would fall back to the default and quietly discard whatever
                // good value was already there, while still reporting the edit
                // as rejected — the worst of both. Re-run the guard, because
                // the rejected value may have clamped other tokens on its way
                // through; the first pass's issues stay as the explanation.
                if (hadPrior) candidate[token] = prior; else candidate.Remove(token);
                Guard.Check(candidate);

                rejection = issues.FirstOrDefault(issue => issue.Token == token)?.Message;
                if (rejection is not null && hadPrior)
                {
                    rejection = $"{rejection}; kept previous {prior.Format()}";
                }
            }

            RemoveDefaultOverrides(candidate);
            Apply(candidate);
            return new TokenCommitResult(applied, issues, rejection);
        }
    }

    /// <summary>Swap a whole override set at once; the guard sees all of it.</summary>
    public IReadOnlyList<GuardIssue> ActivateTheme(string name)
    {
        lock (_gate)
        {
            // A theme switch invalidates whatever was being dragged: the
            // preview value belongs to the theme it started in.
            _preview = null;

            if (!_themes.TryGetValue(name, out var theme))
            {
                Log.Main($"[Tokens] unknown theme '{name}' activated as a new empty theme");
                theme = new Theme(name);
                _themes[name] = theme;
            }
            _active = theme;
            var issues = Guard.Check(theme.Core);
            RemoveDefaultOverrides(theme.Core);
            TokensChanged?.Invoke();
            return issues;
        }
    }

    /// <summary>
    /// Show a value without committing it. The candidate is guarded into a fully
    /// resolved transient snapshot before TokensChanged fires. Guarding here is
    /// deliberately silent and does not mutate the sparse active theme; release
    /// still performs the one authoritative commit whose issues are displayed.
    /// If the guard must drop the edited token, preview keeps the last committed
    /// value rather than publishing a contradictory intermediate state.
    /// </summary>
    public bool PreviewCore(string token, JsonNode? value)
    {
        lock (_gate)
        {
            if (!Defaults.TryGet(token, out var def)) return false;
            if (!TokenValue.TryParse(def.Kind, value, out var parsed)) return false;

            var candidate = new Dictionary<string, TokenValue>(_active.Core)
            {
                [token] = parsed,
            };
            var issues = Guard.Check(candidate);

            if (!candidate.ContainsKey(token)
                || issues.Any(issue =>
                    issue.Token == token && issue.Verdict == GuardVerdict.Refused))
            {
                candidate = new Dictionary<string, TokenValue>(_active.Core);
                Guard.Check(candidate);
            }

            _preview = ResolvedSnapshot(candidate);
        }
        TokensChanged?.Invoke();
        return true;
    }

    /// <summary>Drop the preview — the Esc path. Committed values are untouched.</summary>
    public void CancelPreview()
    {
        lock (_gate)
        {
            if (_preview is null) return;
            _preview = null;
        }
        TokensChanged?.Invoke();
    }

    /// <summary>True while a drag is in flight.</summary>
    public bool HasPreview
    {
        get { lock (_gate) return _preview is not null; }
    }

    /// <summary>
    /// The release path: drop the preview, then commit the value once so the
    /// guard runs exactly once for the whole drag.
    /// </summary>
    public TokenCommitResult CommitPreview(string token, JsonNode? value) =>
        CommitCore(token, value);

    public void CreateTheme(string name)
    {
        lock (_gate)
        {
            _themes.TryAdd(name, new Theme(name));
        }
    }

    /// <summary>Per-token reset: delete the key, fall back to the default.</summary>
    public void ResetCore(string token)
    {
        lock (_gate)
        {
            // Reset-this-token is the contrast warning's offered action, and it
            // can fire while a drag is live. A surviving preview would leave the
            // old value on screen and make the reset look like it did nothing.
            var hadPreview = _preview is not null;
            _preview = null;
            if (_active.Core.Remove(token) || hadPreview) TokensChanged?.Invoke();
        }
    }

    /// <summary>
    /// Sparse layout override write, keyed by route. Only declared layout
    /// properties are allowed.
    /// </summary>
    public bool CommitLayout(string route, string slot, string prop, JsonNode? value)
    {
        if (!Overrides.IsValidLayoutProperty(prop, value, out var problem))
        {
            Log.Main($"[Tokens] layout property '{prop}' ignored: {problem}");
            return false;
        }

        lock (_gate)
        {
            if (!_active.Layouts.TryGetValue(route, out var slots))
            {
                slots = new JsonObject();
                _active.Layouts[route] = slots;
            }
            if (slots[slot] is not JsonObject slotNode)
            {
                slotNode = new JsonObject();
                slots[slot] = slotNode;
            }
            slotNode[prop] = value?.DeepClone();
            TokensChanged?.Invoke();
            return true;
        }
    }

    public void ResetLayout(string route)
    {
        lock (_gate)
        {
            if (_active.Layouts.Remove(route)) TokensChanged?.Invoke();
        }
    }

    /// <summary>
    /// Reads the override file; guard sanitizes every theme it loads.
    /// Missing/corrupt file leaves pristine defaults. Every sanitation issue
    /// is returned and logged with its theme name.
    /// </summary>
    public IReadOnlyList<GuardIssue> Load()
    {
        lock (_gate)
        {
            _preview = null;
            var issues = new List<GuardIssue>();
            var loaded = Overrides.TryLoad(StorePath, out var activeName, out var themes, Log.Main);
            if (loaded)
            {
                _themes.Clear();
                foreach (var theme in themes)
                {
                    var themeIssues = Guard.Check(theme.Core);
                    RemoveDefaultOverrides(theme.Core);
                    issues.AddRange(themeIssues);
                    foreach (var issue in themeIssues)
                    {
                        Log.Main($"[Tokens] load sanitized theme '{theme.Name}', token '{issue.Token}': {issue.Message}");
                    }
                    _themes[theme.Name] = theme;
                }
                if (!_themes.ContainsKey(DefaultThemeName))
                {
                    _themes[DefaultThemeName] = new Theme(DefaultThemeName);
                }
                _active = _themes.TryGetValue(activeName, out var a) ? a : _themes[DefaultThemeName];
            }
            else
            {
                _themes.Clear();
                _active = new Theme(DefaultThemeName);
                _themes[DefaultThemeName] = _active;
            }
            TokensChanged?.Invoke();
            return issues;
        }
    }

    /// <summary>
    /// Sparse write: only what differs. Returns false and reports why instead of
    /// throwing, so a read-only folder shows "not saved" rather than crashing
    /// the app. The live values stay active either way — a failed persist must
    /// never be mistaken for a successful one.
    /// </summary>
    public bool TrySave(out string? error)
    {
        lock (_gate)
        {
            var saved = Overrides.TrySave(StorePath, _active.Name, _themes.Values, out error);
            if (!saved)
            {
                Log.Main($"[Tokens] could not save overrides: {error}");
            }
            return saved;
        }
    }

    /// <summary>Fire-and-forget save for shutdown paths that cannot react.</summary>
    public void Save() => TrySave(out _);

    /// <summary>
    /// Reset all live values and delete the persisted override file. The live
    /// reset always happens; the return value says whether it will also survive
    /// restart. A read-only file must not turn into a false success message in
    /// Appearance.
    /// </summary>
    public bool TryResetAll(out string? error)
    {
        lock (_gate)
        {
            _preview = null;
            _themes.Clear();
            _active = new Theme(DefaultThemeName);
            _themes[DefaultThemeName] = _active;
            var deleted = true;
            try
            {
                if (File.Exists(StorePath)) File.Delete(StorePath);
                error = null;
            }
            catch (Exception ex)
            {
                deleted = false;
                error = ex.Message;
                Log.Main($"[Tokens] could not delete override file: {ex.Message}");
            }
            TokensChanged?.Invoke();
            return deleted;
        }
    }

    /// <summary>Fire-and-forget reset for startup paths that can only log failure.</summary>
    public void ResetAll() => TryResetAll(out _);

    private void Apply(Dictionary<string, TokenValue> candidate)
    {
        var changed = candidate.Count != _active.Core.Count
            || candidate.Any(kv =>
                !_active.Core.TryGetValue(kv.Key, out var cur) || cur != kv.Value);
        if (!changed) return;

        _active.Core.Clear();
        foreach (var (k, v) in candidate) _active.Core[k] = v;
        TokensChanged?.Invoke();
    }

    private static void RemoveDefaultOverrides(Dictionary<string, TokenValue> core)
    {
        foreach (var (token, value) in core.ToList())
        {
            if (Defaults.TryGet(token, out var fallback) && value == fallback)
            {
                core.Remove(token);
            }
        }
    }

    private static Dictionary<string, TokenValue> ResolvedSnapshot(
        IReadOnlyDictionary<string, TokenValue> core)
    {
        var snapshot = new Dictionary<string, TokenValue>(StringComparer.Ordinal);
        foreach (var (token, fallback) in Defaults.All)
        {
            snapshot[token] = core.TryGetValue(token, out var value) ? value : fallback;
        }
        return snapshot;
    }
}

/// <summary>Outcome of a single editable core-token commit.</summary>
public sealed record TokenCommitResult(
    bool Applied,
    IReadOnlyList<GuardIssue> Issues,
    string? RejectionReason);
