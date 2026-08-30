using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using Citadel.Core;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;
using Citadel.Core.Tokens;

namespace Citadel.Shell;

/// <summary>
/// Applies a screen's layout to its view, from the outside.
///
/// The seam exists because a module cannot do this itself: IModule.CreateView
/// receives a Lifetime and nothing else — no token store — so a module has no
/// way to read its own layout. The shell therefore applies the declared layout
/// before hosting the view, without changing the citizen contract.
///
/// Declared defaults merge with the route's sparse override, and only the
/// properties a slot's kind permits are applied. Position is
/// a delta expressed as a TranslateTransform rather than a margin: the view
/// already positions itself, so adding an absolute offset would count the
/// declared default twice. Zero delta leaves the transform untouched.
///
/// Re-applies on TokensChanged under the *view's* lifetime, so an edit in
/// Settings lands live and the subscription dies on nav-away.
/// </summary>
public static class LayoutApplier
{
    public const string PositionKind = "position";
    public const string SizeKind = "size";
    public const string VisibilityKind = "visibility";

    /// <summary>Applies now, then on every token change until the lifetime dies.</summary>
    public static void Attach(
        FrameworkElement view,
        string route,
        LayoutDeclaration? declaration,
        Tokens tokens,
        Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(lifetime);
        if (declaration is null) return;

        void Apply() => LayoutApplier.Apply(view, route, declaration, tokens);

        Apply();
        tokens.TokensChanged += Apply;
        lifetime.Add(() => tokens.TokensChanged -= Apply);
    }

    /// <summary>One pass. Public so a fake view can pin the behaviour directly.</summary>
    public static void Apply(
        FrameworkElement view,
        string route,
        LayoutDeclaration declaration,
        Tokens tokens)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(tokens);

        var overrides = tokens.LayoutOverrides(route);
        var declared = declaration.Slots;

        foreach (var slotName in declaration.SlotNames)
        {
            var kind = declaration.KindOf(slotName);
            if (kind is null)
            {
                Log.Modules($"[Layout] '{route}' slot '{slotName}' has no kind; skipped");
                continue;
            }

            var target = view.FindName(slotName) as FrameworkElement
                ?? (string.Equals(view.Name, slotName, StringComparison.Ordinal) ? view : null);
            if (target is null)
            {
                // A declaration can outlive the element it names — an editable
                // slot for something the view no longer has. Not fatal.
                Log.Modules($"[Layout] '{route}' declares slot '{slotName}' with no matching element");
                continue;
            }

            var declaredSlot = declared[slotName] as JsonObject;
            var merged = Merge(declaredSlot, overrides?[slotName] as JsonObject);
            ApplySlot(target, route, slotName, kind, declaredSlot, merged);
        }
    }

    private static JsonObject Merge(JsonObject? declared, JsonObject? overridden)
    {
        var merged = declared is null ? new JsonObject() : declared.DeepClone().AsObject();
        if (overridden is null) return merged;

        foreach (var (key, value) in overridden)
        {
            merged[key] = value?.DeepClone();
        }
        return merged;
    }

    private static void ApplySlot(
        FrameworkElement target,
        string route,
        string slotName,
        string kind,
        JsonObject? declared,
        JsonObject resolved)
    {
        switch (kind)
        {
            case PositionKind:
                ApplyPosition(target, declared, resolved);
                break;
            case SizeKind:
                ApplySize(target, resolved);
                break;
            case VisibilityKind:
                ApplyVisibility(target, resolved);
                break;
            default:
                Log.Modules($"[Layout] '{route}' slot '{slotName}' has unknown kind '{kind}'; skipped");
                break;
        }
    }

    private static void ApplyPosition(
        FrameworkElement target,
        JsonObject? declared,
        JsonObject resolved)
    {
        var defaultX = declared is null ? 0d : Number(declared, "x") ?? 0d;
        var defaultY = declared is null ? 0d : Number(declared, "y") ?? 0d;
        var x = (Number(resolved, "x") ?? defaultX) - defaultX;
        var y = (Number(resolved, "y") ?? defaultY) - defaultY;

        // The unchanged view already embodies the declared default. The
        // transform therefore carries only resolved - declared; applying the
        // resolved coordinate itself would count that baseline twice.
        if (x == 0d && y == 0d)
        {
            if (target.RenderTransform is TranslateTransform) target.RenderTransform = Transform.Identity;
            return;
        }

        if (target.RenderTransform is TranslateTransform existing)
        {
            existing.X = x;
            existing.Y = y;
            return;
        }
        target.RenderTransform = new TranslateTransform(x, y);
    }

    private static void ApplySize(FrameworkElement target, JsonObject slot)
    {
        var width = Number(slot, "w");
        var height = Number(slot, "h");

        // A non-positive size would erase the element. The sanitizer sees
        // property names and JSON types only; the declared kind is what makes
        // this checkable, so it is checked here.
        if (width is > 0) target.Width = width.Value;
        if (height is > 0) target.Height = height.Value;
    }

    private static void ApplyVisibility(FrameworkElement target, JsonObject slot)
    {
        if (Boolean(slot, "visible") is { } visible)
        {
            target.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static double? Number(JsonObject slot, string property)
    {
        if (!slot.TryGetPropertyValue(property, out var node)
            || node is not JsonValue value
            || value.GetValueKind() != System.Text.Json.JsonValueKind.Number)
        {
            return null;
        }

        // JsonValue.TryGetValue does no numeric conversion, so an int written by
        // CommitLayout never satisfies TryGetValue<double> while the same literal
        // parsed from ui.json does. Reading the raw JSON number covers both, and
        // is the trap Tokens.cs:25-26 already documents for token values.
        return double.TryParse(
            value.ToJsonString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var number) && double.IsFinite(number)
            ? number
            : null;
    }

    private static bool? Boolean(JsonObject slot, string property) =>
        slot.TryGetPropertyValue(property, out var node)
            && node is JsonValue value
            && value.GetValueKind() is System.Text.Json.JsonValueKind.True
                or System.Text.Json.JsonValueKind.False
            ? value.GetValueKind() == System.Text.Json.JsonValueKind.True
            : null;
}
