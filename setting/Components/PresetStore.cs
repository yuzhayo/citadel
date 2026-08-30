using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Citadel.Core;

namespace Citadel.Setting.Components;

/// <summary>One named setup for one control. The id is how a screen asks.</summary>
public sealed record Preset(string Id, string Label, JsonObject Values)
{
    public JsonObject Copy() => Values.DeepClone().AsObject();
}

/// <summary>
/// Reads and writes the paired `&lt;Name&gt;.presets.json` beside each control.
///
/// Two preset rules are enforced here rather than trusted:
///
/// - **A preset never names a route**. A screen opts in by id;
///   a setup is never applied by route. This is called the rule that erodes
///   quietly, so `Load` rejects any file containing a route reference.
/// - **A missing id falls back to the plain control and logs**
///   — the same law as tokens: defaults are the safety net and an override never
///   recreates one.
///
/// Duplicate ids are refused rather than resolved. Picking first-or-last would
/// silently give two screens different controls for the same id.
/// </summary>
public sealed class PresetStore
{
    public const string RouteKeyword = "route";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly Dictionary<string, Preset> _presets = new(StringComparer.Ordinal);

    private PresetStore(string control, string path)
    {
        Control = control;
        Path = path;
    }

    public string Control { get; }

    public string Path { get; }

    public IReadOnlyList<Preset> All => [.. _presets.Values];

    /// <summary>Where a control's presets live: beside the control, in output.</summary>
    public static string PathFor(string control, string? directory = null) =>
        System.IO.Path.Combine(
            directory ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Components"),
            $"{control}.presets.json");

    /// <summary>
    /// A missing or malformed file is not an error: the control still works, it
    /// just has no named setups. Every rejection is logged so a typo in an id is
    /// findable.
    /// </summary>
    public static PresetStore Load(string control, string? directory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(control);

        var path = PathFor(control, directory);
        var store = new PresetStore(control, path);
        if (!File.Exists(path))
        {
            Log.Main($"[Presets] '{control}' has no presets file at {path}");
            return store;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log.Main($"[Presets] '{control}' presets unreadable, using the plain control: {ex.Message}");
            return store;
        }

        if (root is not JsonObject rootObject
            || rootObject["presets"] is not JsonArray entries)
        {
            Log.Main($"[Presets] '{control}' presets malformed, using the plain control");
            return store;
        }

        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is not JsonObject preset) continue;

            var id = Text(preset, "id");
            if (id is null)
            {
                Log.Main($"[Presets] '{control}' has a setup with no id; skipped");
                continue;
            }

            // Once an id is ambiguous it stays refused. Without this tombstone,
            // a third entry with the same id would be accepted after the second
            // removed the first.
            if (duplicateIds.Contains(id)) continue;

            var values = preset["values"] as JsonObject ?? new JsonObject();
            if (MentionsRoute(values) || MentionsRoute(preset))
            {
                Log.Main($"[Presets] '{control}' setup '{id}' names a route; refused — a preset never knows its screen");
                continue;
            }

            if (!store._presets.TryAdd(id, new Preset(id, Text(preset, "label") ?? id, values.DeepClone().AsObject())))
            {
                // Ambiguous rather than wrong: two setups claim one id, so
                // neither can be trusted and both are dropped.
                store._presets.Remove(id);
                duplicateIds.Add(id);
                Log.Main($"[Presets] '{control}' has duplicate id '{id}'; both refused, using the plain control");
            }
        }

        return store;
    }

    /// <summary>
    /// Values for an id, or null when it is unknown — the caller then uses the
    /// plain control. Never throws on a missing id; that is the fallback path.
    /// </summary>
    public JsonObject? Resolve(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (_presets.TryGetValue(id, out var preset)) return preset.Copy();

        Log.Main($"[Presets] '{Control}' setup '{id}' not found; falling back to the plain control");
        return null;
    }

    public bool Contains(string id) => _presets.ContainsKey(id);

    /// <summary>Create or replace a setup. Refuses a values object naming a route.</summary>
    public bool Set(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrWhiteSpace(preset.Id)) return false;
        if (MentionsRoute(preset.Values))
        {
            Log.Main($"[Presets] '{Control}' setup '{preset.Id}' refused: a preset never names a route");
            return false;
        }

        _presets[preset.Id] = preset with { Values = preset.Values.DeepClone().AsObject() };
        return true;
    }

    public bool Remove(string id) => _presets.Remove(id);

    /// <summary>
    /// Writes the paired file atomically. Returns false with a reason rather
    /// than throwing: an installed app's folder may be read-only, and Gallery
    /// must show that rather than claim success.
    /// </summary>
    public bool TrySave(out string? error)
    {
        var entries = new JsonArray();
        foreach (var preset in _presets.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            entries.Add(new JsonObject
            {
                ["id"] = preset.Id,
                ["label"] = preset.Label,
                ["values"] = preset.Copy(),
            });
        }

        var payload = new JsonObject
        {
            ["control"] = Control,
            ["presets"] = entries,
        }.ToJsonString(Pretty);

        var temp = Path + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(temp, payload);
            File.Move(temp, Path, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Main($"[Presets] '{Control}' could not be saved: {ex.Message}");
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // A stranded temp file is harmless; nothing reads it.
            }
            return false;
        }
    }

    private static string? Text(JsonObject source, string property) =>
        source[property] is JsonValue value && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    /// <summary>
    /// A preset must not know which screen it is on, so any `route` key — at any
    /// depth — disqualifies the whole setup.
    /// </summary>
    private static bool MentionsRoute(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Any(pair =>
            pair.Key.Contains(RouteKeyword, StringComparison.OrdinalIgnoreCase)
            || MentionsRoute(pair.Value)),
        JsonArray array => array.Any(MentionsRoute),
        _ => false,
    };
}
