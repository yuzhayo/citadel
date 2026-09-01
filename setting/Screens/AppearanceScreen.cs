using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;
using Citadel.Setting.Components;
using TokensEngine = Citadel.Core.Tokens.Tokens;

namespace Citadel.Setting.Screens;

/// <summary>
/// The reserved Settings identity `settings/appearance`: edits core tokens
/// against the real main-shell chrome from a separate, stable editor window.
///
/// Live drag goes through the store's transient preview rather than committing
/// per frame. Each preview snapshot is silently guarded before publication;
/// release performs the one authoritative commit whose issues are displayed,
/// and Esc or view destruction drops the preview.
/// Related metrics impose one another's visible editor ranges before preview,
/// so changing one value cannot silently rewrite a sibling; Core guard repair
/// remains for load, theme activation, and non-UI callers.
///
/// Guard issues are shown, not swallowed. A contrast warning is *allowed* — it
/// warns and applies — and it offers reset-this-token, which is the same law as
/// everywhere else: the default is the way back.
///
/// Colour editing emits **opaque** foregrounds. The guard refuses alpha 0 and its
/// luminance ignores alpha entirely, so a semi-transparent foreground would be
/// scored as if opaque and the contrast number would be a lie.
/// </summary>
public sealed class AppearanceScreen : SettingScreen
{
    private static readonly string[] SliderMetrics =
        ["Rail", "Row", "IconSlot", "TitleX", "FullDefault", "FullMin", "FullMax"];

    private static readonly string[] WindowMetrics =
        ["WindowW", "WindowH", "WindowMinW", "WindowMinH"];

    private static readonly string[] EditableColors =
        ["BgRail", "Bg", "Fg", "Dim", "Accent", "Hover", "Selected", "Border", "Card", "Body"];

    private readonly TokensEngine _tokens;
    private readonly StackPanel _issues = new();
    private readonly TextBlock _status = Body(string.Empty);
    private readonly Dictionary<string, SettingSlider> _sliders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _metricReadouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SettingField> _windowFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _windowHints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SettingField> _colorFields = new(StringComparer.Ordinal);
    private readonly List<GuardIssue> _lastIssues = [];
    private string? _dragToken;
    private double _dragStart;
    private bool _syncing;

    public AppearanceScreen(TokensEngine tokens, Lifetime lifetime)
        : base(lifetime)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));

        AutomationProperties.SetAutomationId(this, "AppearanceScreen");
        AutomationProperties.SetAutomationId(_issues, "GuardIssues");
        AutomationProperties.SetAutomationId(_status, "AppearanceStatus");

        Add(Section("THEME"));
        Add(Card(BuildThemes(), "ThemeCard"));

        Add(Section("METRICS"));
        Add(Card(BuildMetrics(), "MetricsCard"));

        Add(Section("COLOURS"));
        Add(Card(BuildColors(), "ColoursCard"));

        Add(Section("GUARD"));
        Add(Card(Stack(_status, _issues), "GuardCard"));

        // Esc belongs to the whole screen: a drag can be abandoned with the
        // pointer anywhere.
        PreviewKeyDown += OnPreviewKeyDown;
        Lifetime.Add(() => PreviewKeyDown -= OnPreviewKeyDown);
        _tokens.TokensChanged += SyncFromStore;
        Lifetime.Add(() =>
        {
            _tokens.TokensChanged -= SyncFromStore;
            CancelDrag();
        });

        ShowIssues([], "Nothing to report.");
    }

    /// <summary>Test seam: the guard messages currently on screen.</summary>
    internal IReadOnlyList<string> VisibleIssues =>
        [.. _issues.Children.OfType<FrameworkElement>()
            .Select(element => element is StackPanel row
                ? row.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty
                : (element as TextBlock)?.Text ?? string.Empty)];

    internal string Status => _status.Text;

    internal IReadOnlyList<GuardIssue> LastIssues => _lastIssues;

    /// <summary>Test seam: the current value shown for a metric.</summary>
    internal double MetricValue(string token) =>
        _sliders.TryGetValue(token, out var slider)
            ? slider.Value
            : _windowFields.ContainsKey(token)
                ? _tokens.Number(token)
                : double.NaN;

    /// <summary>Test seam: the range currently imposed by related settings.</summary>
    internal (double Minimum, double Maximum) MetricRange(string token) =>
        _sliders.TryGetValue(token, out var slider)
            ? (slider.Minimum, slider.Maximum)
            : _windowFields.ContainsKey(token)
                ? WindowBounds(token)
                : throw new ArgumentException($"unknown editable metric '{token}'", nameof(token));

    /// <summary>Test seam: edit either a slider metric or a window field.</summary>
    internal void SetMetric(string token, double value)
    {
        if (_sliders.TryGetValue(token, out var slider))
        {
            slider.Value = value;
            return;
        }
        if (_windowFields.TryGetValue(token, out var field))
        {
            field.Text = Format(value);
            CommitWindowMetric(token, field);
            return;
        }
        throw new ArgumentException($"unknown editable metric '{token}'", nameof(token));
    }

    /// <summary>Test seam: type a colour, exactly as the field does.</summary>
    internal void SetColour(string token, string text)
    {
        if (_colorFields.TryGetValue(token, out var field)) field.Text = text;
    }

    /// <summary>Test seam: activate a theme through the screen's own path.</summary>
    internal void ActivateTheme(string name) => ActivateThemeCore(name);

    /// <summary>
    /// Test seam: press the reset offered beside a guard issue. Returns false if
    /// no issue for that token is on screen, so a test cannot pass by resetting
    /// something the UI never offered.
    /// </summary>
    internal bool ResetFromIssue(string token)
    {
        var row = _issues.Children.OfType<FrameworkElement>()
            .FirstOrDefault(element =>
                AutomationProperties.GetAutomationId(element) == $"Issue:{token}");
        if (row is not StackPanel panel) return false;

        var button = panel.Children.OfType<SettingButton>().FirstOrDefault();
        if (button is null) return false;

        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        return true;
    }

    /// <summary>Test seam for the drag lifecycle: preview → commit or cancel.</summary>
    internal void BeginDrag(string token)
    {
        _dragToken = token;
        _dragStart = _tokens.Number(token);
    }

    internal void DragTo(double value)
    {
        if (_dragToken is null) return;
        _tokens.PreviewCore(_dragToken, JsonValue.Create(value));
    }

    internal void EndDrag(double value)
    {
        if (_dragToken is null) return;
        var token = _dragToken;
        _dragToken = null;

        var result = _tokens.CommitPreview(token, JsonValue.Create(value));
        Persist(result);
    }

    internal void CancelDrag()
    {
        if (_dragToken is null)
        {
            _tokens.CancelPreview();
            return;
        }

        _dragToken = null;
        _tokens.CancelPreview();
        SyncFromStore();
        ShowIssues([], $"Preview reverted to {Format(_dragStart)}.");
    }

    private UIElement BuildThemes()
    {
        var name = new SettingField { Placeholder = "Theme name", Width = 180 };
        AutomationProperties.SetAutomationId(name, "ThemeName");

        var activate = Action("Activate", "ActivateTheme", () =>
        {
            var wanted = name.Text.Trim();
            if (wanted.Length == 0) return;

            ActivateThemeCore(wanted);
        });

        var reset = Action("Reset everything", "ResetAll", ResetEverything);

        return Stack(
            Body("A theme is a sparse override set. Activating an unknown name creates an empty one."),
            Row(name, activate, reset));
    }

    private UIElement BuildMetrics()
    {
        var panel = new StackPanel();
        foreach (var token in SliderMetrics)
        {
            panel.Children.Add(BuildMetric(token));
        }

        var windowNote = Body(
            "Window minimums update live. WindowW and WindowH are preferred next-launch sizes, " +
            "clamped to the active monitor work area; later edits never snap a manually resized window back.");
        windowNote.Margin = new Thickness(0, 8, 0, 8);
        panel.Children.Add(windowNote);

        foreach (var token in WindowMetrics)
        {
            panel.Children.Add(BuildWindowMetric(token));
        }
        return panel;
    }

    private UIElement BuildMetric(string token)
    {
        var label = new TextBlock
        {
            Text = token,
            Width = 96,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(StyleProperty, "SettingBodyStyle");

        var (minimum, maximum) = MetricBounds(token);
        var slider = new SettingSlider
        {
            Minimum = minimum,
            Maximum = maximum,
            Step = 1,
            Value = _tokens.Number(token),
            Width = 220,
            Margin = new Thickness(0, 0, 8, 0),
        };
        AutomationProperties.SetAutomationId(slider, $"Metric:{token}");
        _sliders[token] = slider;

        var readout = new TextBlock
        {
            Width = 132,
            VerticalAlignment = VerticalAlignment.Center,
        };
        readout.SetResourceReference(StyleProperty, "SettingBodyStyle");
        _metricReadouts[token] = readout;
        UpdateMetricReadout(slider, readout, slider.Value);

        slider.PreviewMouseLeftButtonDown += (_, _) => BeginDrag(token);
        slider.ValueChanged += (_, args) =>
        {
            var snapped = slider.Snap(args.NewValue);
            UpdateMetricReadout(slider, readout, snapped);
            if (_syncing) return;
            if (_dragToken == token)
            {
                DragTo(snapped);
                return;
            }

            // Keyboard and programmatic changes are not drags: commit directly.
            Persist(_tokens.CommitCore(token, JsonValue.Create(snapped)));
        };
        slider.PreviewMouseLeftButtonUp += (_, _) => EndDrag(slider.Snap(slider.Value));

        var row = Row(label, slider, readout, ResetButton(token));
        row.Margin = new Thickness(0, 0, 0, 6);
        return row;
    }

    private UIElement BuildWindowMetric(string token)
    {
        var label = new TextBlock
        {
            Text = token,
            Width = 96,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(StyleProperty, "SettingBodyStyle");

        var field = new SettingField
        {
            Text = Format(_tokens.Number(token)),
            Width = 120,
            Margin = new Thickness(0, 0, 8, 0),
        };
        AutomationProperties.SetAutomationId(field, $"Metric:{token}");
        _windowFields[token] = field;

        var timing = Body(WindowHint(token));
        timing.Width = 190;
        timing.VerticalAlignment = VerticalAlignment.Center;
        _windowHints[token] = timing;

        field.PreviewKeyDown += (_, args) =>
        {
            if (args.Key is not (Key.Enter or Key.Return)) return;
            CommitWindowMetric(token, field);
            args.Handled = true;
        };
        field.LostKeyboardFocus += (_, _) => CommitWindowMetric(token, field);

        var row = Row(label, field, timing, ResetButton(token));
        row.Margin = new Thickness(0, 0, 0, 6);
        return row;
    }

    private void CommitWindowMetric(string token, SettingField field)
    {
        if (_syncing) return;

        var text = field.Text.Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number)
            || number <= 0)
        {
            SyncFromStore();
            ShowIssues(_lastIssues, $"{token} must be a positive number.");
            return;
        }

        if (Math.Abs(_tokens.Number(token) - number) <= 0.0001) return;

        var (minimum, maximum) = WindowBounds(token);
        if (number < minimum || number > maximum)
        {
            SyncFromStore();
            ShowIssues(_lastIssues, WindowLimitMessage(token));
            return;
        }

        var success = token is "WindowW" or "WindowH"
            ? $"Saved. {token} applies on next launch."
            : "Saved.";
        Persist(_tokens.CommitCore(token, JsonValue.Create(number)), success);
    }

    private UIElement BuildColors()
    {
        var panel = new StackPanel();
        foreach (var token in EditableColors)
        {
            var label = new TextBlock
            {
                Text = token,
                Width = 96,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(StyleProperty, "SettingBodyStyle");

            var field = new SettingField
            {
                Text = _tokens.Resolve(token).Format(),
                Width = 120,
                Margin = new Thickness(0, 0, 8, 0),
            };
            AutomationProperties.SetAutomationId(field, $"Colour:{token}");
            _colorFields[token] = field;

            var captured = token;
            field.TextChanged += text =>
            {
                if (!_syncing) CommitColor(captured, text);
            };

            var row = Row(label, field, ResetButton(token));
            row.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(row);
        }

        panel.Children.Add(Body(
            "Colours are #RRGGBB. A foreground is always opaque, because contrast is " +
            "measured on the colour as drawn."));
        return panel;
    }

    private void CommitColor(string token, string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return;

        // Opaque only: an #AARRGGBB foreground would be scored for contrast as
        // if it were solid, so the warning would understate the real ratio.
        if (!TokenValue.TryParseColor(value, out var parsed))
        {
            ShowIssues(_lastIssues, $"{token}: '{value}' is not a #RRGGBB colour.");
            return;
        }
        if (parsed.A != 0xFF)
        {
            ShowIssues(_lastIssues, $"{token}: alpha is not editable; use #RRGGBB.");
            return;
        }

        Persist(_tokens.CommitCore(token, JsonValue.Create(value)));
    }

    private SettingButton ResetButton(string token) =>
        Action("Reset", $"Reset:{token}", () =>
        {
            _dragToken = null;
            _tokens.CancelPreview();
            _tokens.ResetCore(token);
            SyncFromStore();
            var saved = _tokens.TrySave(out var error);
            ShowIssues([], saved
                ? $"{token} back to its default."
                : $"{token} reset but NOT saved: {error}");
        });

    internal void ResetEverything()
    {
        _dragToken = null;
        var persisted = _tokens.TryResetAll(out var error);
        SyncFromStore();
        ShowIssues([], persisted
            ? "Every override cleared."
            : $"Overrides cleared for this session but NOT saved: {error}");
    }

    private void Persist(TokenCommitResult result, string successStatus = "Saved.")
    {
        SyncFromStore();

        if (!result.Applied)
        {
            ShowIssues(result.Issues, result.RejectionReason ?? "Edit was not applied.");
            return;
        }

        // A failed save must never read as success, and the live value stays
        // active either way.
        var saved = _tokens.TrySave(out var error);
        ShowIssues(
            result.Issues,
            saved ? successStatus : $"Applied but NOT saved: {error}");
    }

    /// <summary>
    /// Re-reads every editor and every related range from the store. The guard
    /// may still repair non-UI input, while a normal editor change updates sibling
    /// bounds before the user can cross them.
    /// </summary>
    private void SyncFromStore()
    {
        _syncing = true;
        try
        {
            foreach (var (token, slider) in _sliders)
            {
                SetBounds(slider, MetricBounds(token));
                var value = _tokens.Number(token);
                if (Math.Abs(slider.Value - value) > 0.0001) slider.Value = value;
                UpdateMetricReadout(slider, _metricReadouts[token], value);
            }
            foreach (var (token, field) in _windowFields)
            {
                var value = Format(_tokens.Number(token));
                if (!string.Equals(field.Text, value, StringComparison.Ordinal)) field.Text = value;
                _windowHints[token].Text = WindowHint(token);
            }
            foreach (var (token, field) in _colorFields)
            {
                var value = _tokens.Resolve(token).Format();
                if (!string.Equals(field.Text, value, StringComparison.Ordinal)) field.Text = value;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ActivateThemeCore(string name)
    {
        // Tokens cancels its preview; clear the screen's matching drag state too
        // so a later mouse-up cannot commit the old theme's value into the new one.
        _dragToken = null;
        var issues = _tokens.ActivateTheme(name);
        SyncFromStore();

        var saved = _tokens.TrySave(out var error);
        ShowIssues(issues, saved
            ? $"Activated '{name}'."
            : $"Activated '{name}' but NOT saved: {error}");
    }

    private void ShowIssues(IReadOnlyList<GuardIssue> issues, string status)
    {
        _lastIssues.Clear();
        _lastIssues.AddRange(issues);

        _status.Text = status;
        _issues.Children.Clear();

        if (issues.Count == 0)
        {
            _issues.Children.Add(Body("No guard issues."));
            return;
        }

        foreach (var issue in issues)
        {
            var text = Body($"{issue.Verdict}: {issue.Token} — {issue.Message}");
            text.Width = 420;
            text.VerticalAlignment = VerticalAlignment.Center;

            // A warning is actionable, so the action is offered right beside it.
            var row = Row(text, ResetButton(issue.Token));
            row.Margin = new Thickness(0, 0, 0, 6);
            AutomationProperties.SetAutomationId(row, $"Issue:{issue.Token}");
            _issues.Children.Add(row);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        CancelDrag();
        args.Handled = true;
    }

    private (double Minimum, double Maximum) MetricBounds(string token)
    {
        const double editorMinimum = 1;
        const double editorMaximum = 480;

        var rail = _tokens.Number("Rail");
        var titleX = _tokens.Number("TitleX");
        var fullMin = _tokens.Number("FullMin");
        var fullDefault = _tokens.Number("FullDefault");
        var fullMax = _tokens.Number("FullMax");
        var fullFloor = Math.Max(fullMin, rail);

        var bounds = token switch
        {
            "TitleX" => (editorMinimum, Math.Min(editorMaximum, rail)),
            "Rail" => (
                Math.Min(editorMaximum, titleX),
                Math.Min(editorMaximum, Math.Min(fullDefault, fullMax))),
            "FullMin" => (editorMinimum, Math.Min(editorMaximum, fullDefault)),
            "FullMax" => (
                Math.Min(editorMaximum, Math.Max(fullFloor, fullDefault)),
                editorMaximum),
            "FullDefault" => (
                Math.Min(editorMaximum, fullFloor),
                Math.Min(editorMaximum, fullMax)),
            _ => (editorMinimum, editorMaximum),
        };

        return bounds.Item1 <= bounds.Item2
            ? bounds
            : (bounds.Item2, bounds.Item2);
    }

    private (double Minimum, double Maximum) WindowBounds(string token) => token switch
    {
        "WindowW" => (_tokens.Number("WindowMinW"), double.PositiveInfinity),
        "WindowMinW" => (1, _tokens.Number("WindowW")),
        "WindowH" => (_tokens.Number("WindowMinH"), double.PositiveInfinity),
        "WindowMinH" => (1, _tokens.Number("WindowH")),
        _ => throw new ArgumentException($"unknown window metric '{token}'", nameof(token)),
    };

    private string WindowHint(string token) => token switch
    {
        "WindowW" => $"min {_tokens.Number("WindowMinW"):0.##} · preferred next launch",
        "WindowMinW" => $"max {_tokens.Number("WindowW"):0.##} · live minimum",
        "WindowH" => $"min {_tokens.Number("WindowMinH"):0.##} · preferred next launch",
        "WindowMinH" => $"max {_tokens.Number("WindowH"):0.##} · live minimum",
        _ => string.Empty,
    };

    private string WindowLimitMessage(string token) => token switch
    {
        "WindowW" => $"WindowW cannot be below WindowMinW ({Format(_tokens.Number("WindowMinW"))}).",
        "WindowMinW" => $"WindowMinW cannot exceed WindowW ({Format(_tokens.Number("WindowW"))}).",
        "WindowH" => $"WindowH cannot be below WindowMinH ({Format(_tokens.Number("WindowMinH"))}).",
        "WindowMinH" => $"WindowMinH cannot exceed WindowH ({Format(_tokens.Number("WindowH"))}).",
        _ => $"{token} is outside its related setting's limit.",
    };

    private static void UpdateMetricReadout(
        SettingSlider slider,
        TextBlock readout,
        double value) =>
        readout.Text = $"{Format(value)} · {Format(slider.Minimum)}–{Format(slider.Maximum)}";

    private static void SetBounds(
        SettingSlider slider,
        (double Minimum, double Maximum) bounds)
    {
        // Open the range before narrowing either side; RangeBase rejects a
        // temporary Minimum > Maximum even when the final pair is valid.
        slider.Minimum = 1;
        slider.Maximum = 480;
        slider.Minimum = bounds.Minimum;
        slider.Maximum = bounds.Maximum;
    }

    private static string Format(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
