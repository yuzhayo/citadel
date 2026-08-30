using System.Text.Json.Nodes;
using Citadel.Core;
using Citadel.Core.Modules;

namespace Citadel.Setting.LayoutEditor;

/// <summary>What a slot may be edited as. The three kinds, and nothing else.</summary>
public enum SlotKind { Position, Size, Visibility }

/// <summary>One editable slot: its kind, its declared defaults, its resolved values.</summary>
public sealed record SlotModel(
    string Name,
    SlotKind Kind,
    IReadOnlyList<string> Properties,
    IReadOnlyDictionary<string, double> DeclaredNumbers,
    IReadOnlyDictionary<string, double> Numbers,
    bool? Visible);

/// <summary>A normalized numeric edit and the user-visible correction, if any.</summary>
public sealed record NumberEdit(double Value, string? Warning);

/// <summary>
/// Turns a screen's LayoutDeclaration plus its sparse override into the model an
/// editor is generated from.
///
/// This is where layout *value* validation lives, and deliberately not in stage
/// 2's sanitizer: the sanitizer sees property names and JSON types only. It
/// cannot know that `position` means x/y, that `size` means w/h, or which slots
/// a screen still declares. So:
///
/// - properties foreign to the declared kind are dropped;
/// - non-positive w/h returns to the declared default with a warning;
/// - stale routes, slots, and properties are ignored at resolve time. Cleaning
///   them off disk waits for the next save, because an override for a slot that
///   is temporarily missing should survive a rebuild.
/// </summary>
public static class DeclarationReader
{
    public static IReadOnlyList<string> PropertiesOf(SlotKind kind) => kind switch
    {
        SlotKind.Position => ["x", "y"],
        SlotKind.Size => ["w", "h"],
        _ => ["visible"],
    };

    public static SlotKind? KindOf(string? declared) => declared switch
    {
        "position" => SlotKind.Position,
        "size" => SlotKind.Size,
        "visibility" => SlotKind.Visibility,
        _ => null,
    };

    public static string NameOf(SlotKind kind) => kind switch
    {
        SlotKind.Position => "position",
        SlotKind.Size => "size",
        _ => "visibility",
    };

    /// <summary>
    /// Validates an edit against the declaration that generated its field. Core
    /// cannot do this because it deliberately does not know a slot's kind or its
    /// declared default.
    /// </summary>
    public static NumberEdit NormalizeNumber(SlotModel slot, string property, double value)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(property);

        if (!slot.Properties.Contains(property, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"'{property}' does not belong to a {NameOf(slot.Kind)} slot",
                nameof(property));
        }

        if (slot.Kind != SlotKind.Size || value > 0) return new NumberEdit(value, null);

        var declared = slot.DeclaredNumbers[property];
        return new NumberEdit(
            declared,
            $"{slot.Name} {property} must be positive; restored declared default {declared}.");
    }

    /// <summary>
    /// Reads every declared slot. Undeclared or unknown-kind slots are skipped
    /// with a log line rather than throwing — a declaration is authored data.
    /// </summary>
    public static IReadOnlyList<SlotModel> Read(
        string route,
        LayoutDeclaration declaration,
        JsonObject? overrides)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(declaration);

        var models = new List<SlotModel>();
        foreach (var slotName in declaration.SlotNames)
        {
            var kind = KindOf(declaration.KindOf(slotName));
            if (kind is null)
            {
                Log.Modules($"[Layout] '{route}' slot '{slotName}' has no usable kind; not editable");
                continue;
            }

            var declared = declaration.Slot(slotName);
            var overridden = overrides?[slotName] as JsonObject;
            models.Add(Merge(route, slotName, kind.Value, declared, overridden));
        }

        // A stale override outlives the slot it names. Report it once so an
        // editor can offer to clean it, but never resolve it.
        if (overrides is not null)
        {
            var declaredNames = declaration.SlotNames.ToHashSet(StringComparer.Ordinal);
            foreach (var (slot, _) in overrides)
            {
                if (!declaredNames.Contains(slot))
                {
                    Log.Modules($"[Layout] '{route}' override names slot '{slot}' which is no longer declared; ignored");
                }
            }
        }

        return models;
    }

    private static SlotModel Merge(
        string route,
        string slotName,
        SlotKind kind,
        JsonObject? declared,
        JsonObject? overridden)
    {
        var properties = PropertiesOf(kind);
        var declaredNumbers = new Dictionary<string, double>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, double>(StringComparer.Ordinal);
        bool? visible = null;

        if (kind == SlotKind.Visibility)
        {
            visible = Boolean(declared, "visible") ?? true;
            var wanted = Boolean(overridden, "visible");
            if (wanted is not null) visible = wanted;
        }
        else
        {
            foreach (var property in properties)
            {
                var declaredValue = Number(declared, property) ?? 0d;
                declaredNumbers[property] = declaredValue;
                var wanted = Number(overridden, property);
                var value = wanted ?? declaredValue;

                // A size of zero erases the element. The declared default is the
                // safety net, exactly as an in-code token default is.
                if (kind == SlotKind.Size && value <= 0)
                {
                    if (wanted is not null)
                    {
                        Log.Modules(
                            $"[Layout] '{route}' slot '{slotName}' {property}={wanted} is not positive; " +
                            $"using declared default {declaredValue}");
                    }
                    value = declaredValue;
                }

                numbers[property] = value;
            }
        }

        ReportForeignProperties(route, slotName, kind, overridden, properties);
        return new SlotModel(slotName, kind, properties, declaredNumbers, numbers, visible);
    }

    private static void ReportForeignProperties(
        string route,
        string slotName,
        SlotKind kind,
        JsonObject? overridden,
        IReadOnlyList<string> allowed)
    {
        if (overridden is null) return;

        foreach (var (property, _) in overridden)
        {
            if (!allowed.Contains(property, StringComparer.Ordinal))
            {
                Log.Modules(
                    $"[Layout] '{route}' slot '{slotName}' is {NameOf(kind)}, so '{property}' does not apply; ignored");
            }
        }
    }

    private static double? Number(JsonObject? slot, string property)
    {
        if (slot is null
            || !slot.TryGetPropertyValue(property, out var node)
            || node is not JsonValue value
            || value.GetValueKind() != System.Text.Json.JsonValueKind.Number)
        {
            return null;
        }

        // JsonValue.TryGetValue does no numeric conversion, so an int written by
        // CommitLayout never satisfies TryGetValue<double> while the same literal
        // parsed from ui.json does (Tokens.cs:25-26 documents the same trap).
        return double.TryParse(
            value.ToJsonString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var number) && double.IsFinite(number)
            ? number
            : null;
    }

    private static bool? Boolean(JsonObject? slot, string property) =>
        slot is not null
            && slot.TryGetPropertyValue(property, out var node)
            && node is JsonValue value
            && value.GetValueKind() is System.Text.Json.JsonValueKind.True
                or System.Text.Json.JsonValueKind.False
            ? value.GetValueKind() == System.Text.Json.JsonValueKind.True
            : null;
}
