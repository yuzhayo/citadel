using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Citadel.Core.Rpl;
using Citadel.Setting.Components;

namespace Citadel.Setting.Screens;

/// <summary>
/// The route `settings/gallery`: every control beside its saved setups, and where
/// a setup is created, edited, and given its id.
///
/// This screen is only the frontend — the paired `&lt;Name&gt;.presets.json` beside
/// each control is the source of truth. **That means editing setups requires the
/// application folder to be writable.** An installed read-only folder can still
/// *use* every shipped setup; it just cannot save new ones, and a failed write is
/// shown rather than swallowed.
///
/// Deleting a setup names every screen currently using that id and asks first.
/// The lookup covers the declarations the host currently holds.
/// </summary>
public sealed class GalleryScreen : SettingScreen
{
    private static readonly string[] Controls = ["Button", "Field", "Toggle", "Slider", "Table"];

    private readonly ISettingHost _host;
    private readonly string? _presetDirectory;
    private readonly ComboBox _controls = new();
    private readonly ListBox _presets = new();
    private readonly SettingField _id = new() { Placeholder = "setup id", Width = 180 };
    private readonly SettingField _label = new() { Placeholder = "label", Width = 180 };
    private readonly StackPanel _valueEditor = new();
    private readonly Dictionary<string, SettingField> _valueFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SettingToggle> _valueToggles = new(StringComparer.Ordinal);
    private readonly ContentControl _sample = new();
    private readonly TextBlock _status = Body(string.Empty);
    private readonly StackPanel _confirm = new();
    private PresetStore? _store;
    private bool _loadingDraft;

    public GalleryScreen(ISettingHost host, Lifetime lifetime, string? presetDirectory = null)
        : base(lifetime)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _presetDirectory = presetDirectory;

        AutomationProperties.SetAutomationId(this, "GalleryScreen");
        AutomationProperties.SetAutomationId(_controls, "GalleryControls");
        AutomationProperties.SetAutomationId(_presets, "GalleryPresets");
        AutomationProperties.SetAutomationId(_id, "PresetId");
        AutomationProperties.SetAutomationId(_label, "PresetLabel");
        AutomationProperties.SetAutomationId(_valueEditor, "PresetValues");
        AutomationProperties.SetAutomationId(_status, "GalleryStatus");
        AutomationProperties.SetAutomationId(_confirm, "GalleryConfirm");
        _controls.SetResourceReference(FrameworkElement.StyleProperty, "SettingComboBoxStyle");
        _presets.SetResourceReference(FrameworkElement.StyleProperty, "SettingListStyle");

        Add(Section("CONTROL"));
        Add(Card(Stack(
            Body("Each control keeps its named setups in a file beside it. A screen asks for one by id."),
            Row(_controls)),
            "GalleryControlCard"));

        Add(Section("SETUPS"));
        Add(Card(Stack(
            _presets,
            Row(_id, _label),
            _valueEditor,
            Row(
                Action("Save setup", "SavePreset", SaveCurrent),
                Action("Delete setup", "DeletePreset", AskDelete)),
            _confirm),
            "GalleryPresetsCard"));

        Add(Section("PREVIEW"));
        Add(Card(_sample, "GalleryPreviewCard"));
        Add(Card(_status, "GalleryStatusCard"));

        _controls.ItemsSource = Controls;
        _controls.SelectionChanged += (_, _) => LoadControl();
        _presets.SelectionChanged += (_, _) => ShowSelected();
        _controls.SelectedIndex = 0;
    }

    internal string SelectedControl => (string?)_controls.SelectedItem ?? string.Empty;

    internal IReadOnlyList<string> PresetIds => [.. _presets.Items.OfType<string>()];

    internal string Status => _status.Text;

    internal FrameworkElement? Sample => _sample.Content as FrameworkElement;

    internal IReadOnlyList<string> ConfirmPrompt =>
        [.. _confirm.Children.OfType<TextBlock>().Select(block => block.Text)];

    internal void SelectControl(string control)
    {
        if (!Controls.Contains(control, StringComparer.Ordinal)) return;
        _controls.SelectedItem = control;
    }

    internal void SelectPreset(string id)
    {
        if (!_presets.Items.Contains(id)) return;
        _presets.SelectedItem = id;
    }

    /// <summary>Test seam: type in a real generated value field.</summary>
    internal void SetValue(string property, string value)
    {
        if (!_valueFields.TryGetValue(property, out var field))
        {
            throw new InvalidOperationException($"no editor for preset value '{property}'");
        }
        field.Text = value;
    }

    internal void SetFlag(string property, bool value)
    {
        if (!_valueToggles.TryGetValue(property, out var toggle))
        {
            throw new InvalidOperationException($"no editor for preset value '{property}'");
        }
        toggle.IsChecked = value;
    }

    /// <summary>Test seam: fill the real identity fields and run the real save path.</summary>
    internal bool Save(string id, string label)
    {
        _id.Text = id;
        _label.Text = label;
        SaveCurrent();
        return _store?.Contains(id) == true && !_status.Text.Contains("NOT saved", StringComparison.Ordinal);
    }

    private void LoadControl()
    {
        var control = SelectedControl;
        if (control.Length == 0) return;

        _store = PresetStore.Load(control, _presetDirectory);
        _presets.ItemsSource = _store.All.Select(preset => preset.Id).ToList();
        _presets.SelectedItem = _presets.Items.OfType<string>().FirstOrDefault();
        _confirm.Children.Clear();
        _status.Text = $"{_store.All.Count} setup(s) for {control}.";
        ShowSelected();
    }

    private void ShowSelected()
    {
        var control = SelectedControl;
        if (control.Length == 0 || _store is null)
        {
            _sample.Content = null;
            return;
        }

        var sample = NewSample(control);
        _sample.Content = sample;

        if (_presets.SelectedItem is not string id)
        {
            _id.Text = string.Empty;
            _label.Text = string.Empty;
            BuildValueEditor(control, new JsonObject());
            return;
        }

        var preset = _store.All.FirstOrDefault(preset => preset.Id == id);
        _id.Text = id;
        _label.Text = preset?.Label ?? id;

        // A missing id is the documented fallback: the plain control plus a
        // logged warning, never a failure.
        if (preset is null)
        {
            _status.Text = $"'{id}' is not in {control}'s presets; showing the plain control.";
            BuildValueEditor(control, new JsonObject());
            return;
        }

        BuildValueEditor(control, preset.Copy());
    }

    private void SaveCurrent()
    {
        if (_store is null) return;

        var id = _id.Text.Trim();
        if (id.Length == 0)
        {
            _status.Text = "A setup needs an id — that is how a screen asks for it.";
            return;
        }

        if (!TryReadValues(out var values, out var problem))
        {
            _status.Text = problem;
            return;
        }

        if (!_store.Set(new Preset(id, _label.Text.Trim() is { Length: > 0 } label ? label : id, values)))
        {
            _status.Text = $"'{id}' was refused — a setup may not name a route.";
            return;
        }

        _presets.ItemsSource = _store.All.Select(preset => preset.Id).ToList();
        _presets.SelectedItem = id;

        _status.Text = _store.TrySave(out var error)
            ? $"Saved '{id}' to {Path(_store)}."
            : $"'{id}' is active but NOT saved: {error}. Gallery editing needs a writable app folder.";
    }

    private void BuildValueEditor(string control, JsonObject values)
    {
        _loadingDraft = true;
        try
        {
            _valueEditor.Children.Clear();
            _valueFields.Clear();
            _valueToggles.Clear();

            switch (control)
            {
                case "Button":
                case "Toggle":
                    AddValueField("label", Text(values, "label"));
                    if (control == "Button") AddValueField("width", Number(values, "width"));
                    break;
                case "Field":
                    AddValueField("placeholder", Text(values, "placeholder"));
                    AddValueField("width", Number(values, "width"));
                    break;
                case "Slider":
                    AddValueField("min", Number(values, "min"));
                    AddValueField("max", Number(values, "max"));
                    AddValueField("step", Number(values, "step"));
                    break;
                case "Table":
                    AddValueField("columns", Columns(values));
                    AddValueToggle("descending", Boolean(values, "descending"));
                    break;
            }
        }
        finally
        {
            _loadingDraft = false;
        }

        RefreshPreview();
    }

    private void AddValueField(string property, string value)
    {
        var label = Body(property);
        label.Width = 96;
        label.VerticalAlignment = VerticalAlignment.Center;

        var field = new SettingField { Text = value, Width = 220 };
        AutomationProperties.SetAutomationId(field, $"PresetValue:{property}");
        field.TextChanged += _ => RefreshPreview();
        _valueFields[property] = field;

        var row = Row(label, field);
        row.Margin = new Thickness(0, 6, 0, 0);
        _valueEditor.Children.Add(row);
    }

    private void AddValueToggle(string property, bool value)
    {
        var toggle = new SettingToggle { Content = property, IsChecked = value };
        AutomationProperties.SetAutomationId(toggle, $"PresetValue:{property}");
        toggle.Checked += (_, _) => RefreshPreview();
        toggle.Unchecked += (_, _) => RefreshPreview();
        _valueToggles[property] = toggle;
        _valueEditor.Children.Add(toggle);
    }

    private void RefreshPreview()
    {
        if (_loadingDraft) return;

        var sample = NewSample(SelectedControl);
        _sample.Content = sample;
        if (TryReadValues(out var values, out _)) PresetApplier.ApplyValues(sample, values);
    }

    private bool TryReadValues(out JsonObject values, out string problem)
    {
        values = new JsonObject();
        problem = string.Empty;

        foreach (var (property, field) in _valueFields)
        {
            var text = field.Text.Trim();
            if (property is "width" or "min" or "max" or "step")
            {
                // Preset values are sparse: blank means keep the plain control's
                // value rather than forcing every setup to restate every option.
                if (text.Length == 0) continue;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    || !double.IsFinite(number))
                {
                    problem = $"{property} must be a number.";
                    return false;
                }
                if ((property is "width" or "step") && number <= 0)
                {
                    problem = $"{property} must be positive.";
                    return false;
                }
                values[property] = number;
                continue;
            }

            if (property == "columns")
            {
                if (text.Length == 0) continue;
                var columns = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length == 0)
                {
                    problem = "columns needs at least one comma-separated name.";
                    return false;
                }
                values[property] = new JsonArray(
                    columns.Select(column => (JsonNode?)JsonValue.Create(column)).ToArray());
                continue;
            }

            if (text.Length > 0) values[property] = text;
        }

        foreach (var (property, toggle) in _valueToggles)
        {
            values[property] = toggle.IsChecked == true;
        }

        if (Number(values, "min") is { Length: > 0 } minText
            && Number(values, "max") is { Length: > 0 } maxText
            && double.Parse(minText, CultureInfo.InvariantCulture)
                > double.Parse(maxText, CultureInfo.InvariantCulture))
        {
            problem = "min must not exceed max.";
            return false;
        }

        return true;
    }

    private static string Text(JsonObject values, string property) =>
        values[property] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : string.Empty;

    private static string Number(JsonObject values, string property) =>
        values[property] is JsonValue value
            && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? value.ToJsonString()
            : string.Empty;

    private static bool Boolean(JsonObject values, string property) =>
        values[property] is JsonValue value
            && value.GetValueKind() is System.Text.Json.JsonValueKind.True
                or System.Text.Json.JsonValueKind.False
            && value.GetValueKind() == System.Text.Json.JsonValueKind.True;

    private static string Columns(JsonObject values) =>
        values["columns"] is JsonArray columns
            ? string.Join(", ", columns.OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var text) ? text : null)
                .Where(text => text is not null))
            : string.Empty;

    private void AskDelete()
    {
        if (_store is null || _presets.SelectedItem is not string id) return;

        _confirm.Children.Clear();
        var users = UsersOf(id);
        var warning = users.Count == 0
            ? $"Delete '{id}'? No screen currently asks for it."
            : $"Delete '{id}'? Still used by: {string.Join(", ", users)}.";

        var prompt = Body(warning);
        prompt.Margin = new Thickness(0, 6, 0, 6);
        _confirm.Children.Add(prompt);
        _confirm.Children.Add(Row(
            Action("Yes, delete", "ConfirmDelete", () => Delete(id)),
            Action("Keep it", "CancelDelete", () => _confirm.Children.Clear())));
    }

    private void Delete(string id)
    {
        if (_store is null) return;

        _confirm.Children.Clear();
        _store.Remove(id);
        _presets.ItemsSource = _store.All.Select(preset => preset.Id).ToList();
        _presets.SelectedItem = _presets.Items.OfType<string>().FirstOrDefault();

        _status.Text = _store.TrySave(out var error)
            ? $"Deleted '{id}'."
            : $"Deleted '{id}' in memory but NOT saved: {error}";
        ShowSelected();
    }

    /// <summary>
    /// Which screens ask for this id. Only the declarations the host holds are
    /// consulted; Gallery never searches module folders directly.
    /// </summary>
    private IReadOnlyList<string> UsersOf(string id)
    {
        var users = new List<string>();
        foreach (var descriptor in _host.Screens())
        {
            var declaration = descriptor.Layout;
            if (declaration is null) continue;

            foreach (var slotName in declaration.SlotNames)
            {
                var slot = declaration.Slot(slotName);
                if (slot?["preset"] is JsonValue value
                    && value.TryGetValue<string>(out var wanted)
                    && string.Equals(wanted, id, StringComparison.Ordinal))
                {
                    users.Add(descriptor.Route);
                    break;
                }
            }
        }
        return users;
    }

    private static FrameworkElement NewSample(string control) => control switch
    {
        "Button" => new SettingButton { Content = "Button" },
        "Field" => new SettingField { Placeholder = "Field", Width = 180 },
        "Toggle" => new SettingToggle { Content = "Toggle" },
        "Slider" => new SettingSlider { Minimum = 0, Maximum = 100, Value = 40, Width = 200 },
        _ => Table(),
    };

    private static SettingTable Table()
    {
        var table = new SettingTable();
        table.SetColumns(["Column A", "Column B"]);
        table.SetRows([["first", "1"], ["second", "2"]]);
        return table;
    }

    private static string Path(PresetStore store) => System.IO.Path.GetFileName(store.Path);
}
