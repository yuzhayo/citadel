using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace Citadel.Setting.Components;

/// <summary>
/// Applies a preset's values to a control, and nothing else.
///
/// Three levels stay separate: what the control can do lives in
/// the control, a named setup lives in its presets file, and which setup a screen
/// wants lives in that screen. This class is the bridge for the middle one — it
/// takes values by id and never consults a route.
/// </summary>
public static class PresetApplier
{
    /// <summary>
    /// Applies the setup with this id, or leaves the plain control alone and
    /// returns false. A missing id is the documented fallback, not an error.
    /// </summary>
    public static bool Apply(FrameworkElement control, PresetStore store, string id)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(id);

        var values = store.Resolve(id);
        if (values is null) return false;

        ApplyValues(control, values);
        return true;
    }

    /// <summary>Applies Gallery's unsaved draft to its preview control.</summary>
    internal static void ApplyValues(FrameworkElement control, JsonObject values)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(values);

        foreach (var (key, value) in values)
        {
            ApplyOne(control, key, value);
        }
    }

    private static void ApplyOne(FrameworkElement control, string key, JsonNode? value)
    {
        switch (key)
        {
            case "label" when control is ContentControl content:
                content.Content = Text(value);
                break;
            case "placeholder" when control is SettingField field:
                field.Placeholder = Text(value) ?? string.Empty;
                break;
            case "width" when Number(value) is { } width and > 0:
                control.Width = width;
                break;
            case "min" when control is SettingSlider slider && Number(value) is { } min:
                slider.Minimum = min;
                break;
            case "max" when control is SettingSlider slider && Number(value) is { } max:
                slider.Maximum = max;
                break;
            case "step" when control is SettingSlider slider && Number(value) is { } step and > 0:
                slider.Step = step;
                break;
            case "columns" when control is SettingTable table && value is JsonArray columns:
                table.SetColumns(columns.Select(Text).Where(t => t is not null).Select(t => t!));
                break;
            case "descending" when control is SettingTable table && Boolean(value) is { } descending:
                table.SortDescending = descending;
                break;
            default:
                // Unknown keys are ignored rather than fatal: a presets file may
                // outlive a control property, exactly like a layout declaration
                // outliving an element.
                break;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value
            && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && double.TryParse(
                value.ToJsonString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number)
            && double.IsFinite(number)
            ? number
            : null;

    private static bool? Boolean(JsonNode? node) =>
        node is JsonValue value
            && value.GetValueKind() is System.Text.Json.JsonValueKind.True
                or System.Text.Json.JsonValueKind.False
            ? value.GetValueKind() == System.Text.Json.JsonValueKind.True
            : null;
}
