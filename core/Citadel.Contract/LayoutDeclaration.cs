using System.Text.Json.Nodes;

namespace Citadel.Core.Modules;

/// <summary>
/// A screen's editable slots, as declared by its own layout.json.
///
/// One of the contract's four public types. Its shape is deliberately closed:
/// an immutable,
/// deep-cloned wrapper over the `slots` object, and nothing more. Three
/// parties, three jobs — the searcher validates the JSON before constructing
/// one, Settings reads it to generate an editor, the shell applies it. None of
/// them may mutate another's copy, which is why both the constructor and every
/// reader deep-clone.
///
/// Slot kinds stay strings rather than an enum: the contract is exactly four
/// public types, and a fifth would grow it. The permitted kinds are position,
/// size and visibility; the shell enforces that, because the
/// shell is what applies them.
/// </summary>
public sealed class LayoutDeclaration
{
    /// <summary>The JSON property naming a slot's kind.</summary>
    public const string KindProperty = "kind";

    private readonly JsonObject _slots;

    /// <param name="slots">slot name → slot object. Deep-cloned on the way in.</param>
    public LayoutDeclaration(JsonObject slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        _slots = slots.DeepClone().AsObject();
    }

    /// <summary>Declared slot names, in declaration order.</summary>
    public IReadOnlyList<string> SlotNames => [.. _slots.Select(pair => pair.Key)];

    /// <summary>A private copy of every slot. Mutating it changes nothing here.</summary>
    public JsonObject Slots => _slots.DeepClone().AsObject();

    /// <summary>One slot as a private copy, or null when undeclared.</summary>
    public JsonObject? Slot(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _slots.TryGetPropertyValue(name, out var slot) && slot is JsonObject obj
            ? obj.DeepClone().AsObject()
            : null;
    }

    /// <summary>A slot's declared kind, or null when absent or not a string.</summary>
    public string? KindOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _slots.TryGetPropertyValue(name, out var slot)
            && slot is JsonObject obj
            && obj.TryGetPropertyValue(KindProperty, out var kind)
            && kind is JsonValue value
            && value.TryGetValue<string>(out var text)
            ? text
            : null;
    }
}
