using System.Text.Json.Nodes;

namespace Citadel.Core.Tokens;

/// <summary>
/// A theme is a sparse override set — only what differs from the
/// in-code defaults, so changing a default in a later version
/// propagates. One engine, two consumers: core chrome tokens and
/// module screen layouts keyed by route.
/// </summary>
public sealed class Theme(string name)
{
    public string Name { get; } = name;

    /// <summary>Sparse core-token overrides.</summary>
    public Dictionary<string, TokenValue> Core { get; } = new();

    /// <summary>Layout overrides: route → slot → properties.</summary>
    public Dictionary<string, JsonObject> Layouts { get; } = new();

    public Theme Clone()
    {
        var copy = new Theme(Name);
        foreach (var (k, v) in Core) copy.Core[k] = v;
        foreach (var (route, slots) in Layouts) copy.Layouts[route] = slots.DeepClone().AsObject();
        return copy;
    }
}
