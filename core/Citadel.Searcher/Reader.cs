using System.Text.Json;
using System.Text.Json.Nodes;
using Citadel.Core.Modules;

namespace Citadel.Searcher;

/// <summary>
/// Reads and validates a citizen folder's two JSON files. The searcher is the
/// only thing that parses them.
///
/// Two files, two jobs, deliberately not mixed: `module.json`
/// is identity and is validated strictly — a malformed one fails the folder.
/// `layout.json` is presentation and is fail-soft — a malformed one leaves the
/// screen registered with no editable slots, because its identity is still
/// valid.
///
/// Comments and trailing commas are allowed, and
/// property names are matched case-insensitively.
/// </summary>
internal static class Reader
{
    internal const string ManifestFileName = "module.json";
    internal const string LayoutFileName = "layout.json";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// True when the folder has no `module.json` at all. v0 skipped these
    /// silently (ModuleLoader.cs:32-33) and so do we: a folder without a
    /// manifest is *not a citizen*, which is a different thing from a citizen
    /// whose manifest is broken.
    /// </summary>
    internal static bool IsCitizenFolder(string folder) =>
        File.Exists(Path.Combine(folder, ManifestFileName));

    /// <summary>
    /// Parses and validates identity. <paramref name="error"/> carries the
    /// user-visible reason when this returns null.
    /// </summary>
    internal static ModuleManifest? ReadManifest(
        string folder,
        IReadOnlyCollection<string> reservedRoutes,
        out string? error)
    {
        var path = Path.Combine(folder, ManifestFileName);
        JsonObject root;
        try
        {
            var text = File.ReadAllText(path);
            var node = JsonNode.Parse(text, documentOptions: DocumentOptions);
            if (node is not JsonObject obj)
            {
                error = $"{ManifestFileName} must be a JSON object";
                return null;
            }
            root = obj;
        }
        catch (Exception exception)
        {
            // IOException here is usually a half-finished copy; the caller
            // retries before settling on a failure.
            error = $"{ManifestFileName} could not be read: {exception.Message}";
            return null;
        }

        var title = Text(root, "title");
        var route = Text(root, "route");
        var entry = Text(root, "entry");
        var type = Text(root, "type");

        var missing = new List<string>();
        if (title is null) missing.Add("title");
        if (route is null) missing.Add("route");
        if (entry is null) missing.Add("entry");
        if (type is null) missing.Add("type");
        if (missing.Count > 0)
        {
            error = $"{ManifestFileName} is missing or blank: {string.Join(", ", missing)}";
            return null;
        }

        if (!IsRouteWellFormed(route!))
        {
            error = $"route '{route}' is not a valid route name";
            return null;
        }
        if (reservedRoutes.Any(reserved => string.Equals(reserved, route, StringComparison.Ordinal)))
        {
            // The gate refuses this too, but refusing it here keeps a reserved
            // route from ever reaching the loader — no assembly loaded, no ALC
            // retained, for a folder that could never register.
            error = $"route '{route}' is a reserved core route";
            return null;
        }

        var resolvedEntry = ResolveEntry(folder, entry!, out var entryError);
        if (resolvedEntry is null)
        {
            error = entryError;
            return null;
        }

        error = null;
        return new ModuleManifest(
            title!,
            route!,
            resolvedEntry,
            type!,
            Text(root, "icon"),
            Integer(root, "order") ?? 0);
    }

    /// <summary>
    /// Reads `layout.json` if present. A missing file is normal and yields null,
    /// which the shell treats as "no editable slots". A present-but-unusable file
    /// also yields null, with <paramref name="warning"/> set — identity is still
    /// valid, so the screen registers anyway.
    /// </summary>
    internal static LayoutDeclaration? ReadLayout(string folder, out string? warning)
    {
        var path = Path.Combine(folder, LayoutFileName);
        if (!File.Exists(path))
        {
            warning = null;
            return null;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: DocumentOptions);
            if (node is not JsonObject root)
            {
                warning = $"{LayoutFileName} must be a JSON object; no editable slots";
                return null;
            }
            if (!root.TryGetPropertyValue("slots", out var slotsNode)
                || slotsNode is not JsonObject slots)
            {
                warning = $"{LayoutFileName} has no 'slots' object; no editable slots";
                return null;
            }

            // A slot without a kind is unappliable and uneditable, and the shell
            // would log it once per navigation. Reject the declaration here so
            // the reason is stated once, where the file was read.
            var kindless = slots
                .Where(pair => pair.Value is not JsonObject slot
                    || Text(slot, LayoutDeclaration.KindProperty) is null)
                .Select(pair => pair.Key)
                .ToList();
            if (kindless.Count > 0)
            {
                warning =
                    $"{LayoutFileName} slots without a kind: {string.Join(", ", kindless)}; "
                    + "no editable slots";
                return null;
            }

            warning = null;
            return new LayoutDeclaration(slots);
        }
        catch (Exception exception)
        {
            warning = $"{LayoutFileName} could not be read: {exception.Message}; no editable slots";
            return null;
        }
    }

    /// <summary>
    /// Resolves `entry` against the folder and refuses anything that escapes it.
    /// Not sandboxing — input validation: one folder must not load another
    /// folder's assembly.
    /// </summary>
    private static string? ResolveEntry(string folder, string entry, out string? error)
    {
        if (Path.IsPathRooted(entry))
        {
            error = $"entry '{entry}' must be relative to the screen folder";
            return null;
        }

        string full;
        string root;
        try
        {
            root = Path.GetFullPath(folder);
            full = Path.GetFullPath(Path.Combine(root, entry));
        }
        catch (Exception exception)
        {
            error = $"entry '{entry}' is not a usable path: {exception.Message}";
            return null;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = $"entry '{entry}' resolves outside the screen folder";
            return null;
        }

        error = null;
        return full;
    }

    /// <summary>
    /// A route is a slash-separated path of dot/dash/underscore-friendly
    /// segments. Reserved names are checked separately; this only rejects shapes
    /// that could never be navigated to.
    /// </summary>
    private static bool IsRouteWellFormed(string route)
    {
        if (route.Length > 128) return false;
        if (route.StartsWith('/') || route.EndsWith('/')) return false;

        var segments = route.Split('/');
        return segments.Length > 0 && segments.All(segment =>
            segment.Length > 0
            && segment.All(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.'));
    }

    private static string? Text(JsonObject root, string name)
    {
        var node = Property(root, name);
        if (node is not JsonValue value
            || value.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }
        var text = value.GetValue<string>();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int? Integer(JsonObject root, string name)
    {
        var node = Property(root, name);
        if (node is not JsonValue value
            || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        // JsonValue.TryGetValue does no numeric conversion, so reading the raw
        // JSON number is what covers both `10` and `10.0` — the same trap
        // LayoutApplier documents.
        return double.TryParse(
            value.ToJsonString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var number) && double.IsFinite(number)
            ? (int)number
            : null;
    }

    /// <summary>Case-insensitive property lookup for manifest and layout fields.</summary>
    private static JsonNode? Property(JsonObject root, string name)
    {
        if (root.TryGetPropertyValue(name, out var exact)) return exact;
        foreach (var (key, value) in root)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }
}
