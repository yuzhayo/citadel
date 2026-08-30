using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Citadel.Core.Tokens;

/// <summary>
/// The override file: sparse, keyed, never a snapshot. Absent key →
/// default. Unknown key → ignored and logged. Invalid value → that one
/// token dropped, rest kept. Reset a token = delete its key; reset all
/// = delete the file.
/// </summary>
public static class Overrides
{
    public const string FileName = "ui.json";
    public const string PortableMarker = "portable.txt";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>
    /// %AppData%\Citadel\ui.json, or beside the exe when the portable
    /// marker file is present there.
    /// </summary>
    public static string DefaultStorePath()
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, PortableMarker)))
        {
            return Path.Combine(exeDir, FileName);
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Citadel", FileName);
    }

    /// <summary>
    /// Sparse write, and failure-safe. A direct write can throw on a read-only
    /// folder or leave a truncated file if the process dies mid-write, and a
    /// half-written ui.json is worse than none: it loads as corrupt and silently
    /// discards every override. So: write a temp file beside the real one, flush
    /// it, then replace atomically. Returns false with a reason instead of
    /// throwing, because the caller has to show "not saved" rather than crash.
    /// </summary>
    public static bool TrySave(
        string path,
        string activeTheme,
        IEnumerable<Theme> themes,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(activeTheme);
        ArgumentNullException.ThrowIfNull(themes);

        var root = new JsonObject { ["activeTheme"] = activeTheme };
        var themesNode = new JsonObject();

        foreach (var theme in themes)
        {
            var empty = theme.Core.Count == 0 && theme.Layouts.Count == 0;

            // An empty theme is normally noise. The *active* non-default one is
            // not: dropping it writes a name under activeTheme that no longer
            // exists, so the next load falls back to default and the theme the
            // user just created is gone.
            var keepAsActive = empty
                && !string.Equals(theme.Name, Tokens.DefaultThemeName, StringComparison.Ordinal)
                && string.Equals(theme.Name, activeTheme, StringComparison.Ordinal);
            if (empty && !keepAsActive) continue;

            var node = new JsonObject();
            if (theme.Core.Count > 0)
            {
                var core = new JsonObject();
                foreach (var (token, value) in theme.Core.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    core[token] = value.ToJson();
                }
                node["core"] = core;
            }
            if (theme.Layouts.Count > 0)
            {
                var module = new JsonObject();
                foreach (var (route, slots) in theme.Layouts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    module[route] = slots.DeepClone();
                }
                node["module"] = module;
            }
            themesNode[theme.Name] = node;
        }

        if (themesNode.Count > 0) root["themes"] = themesNode;

        var payload = root.ToJsonString(Pretty);
        var directory = Path.GetDirectoryName(path)!;
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);

            // The temp file must share the directory: File.Move across volumes
            // is a copy, which is not atomic.
            using (var stream = new FileStream(
                temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                writer.Write(payload);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // A stranded temp file is harmless: the next save overwrites it,
                // and nothing reads it.
            }
            return false;
        }
    }

    /// <summary>
    /// Missing or unreadable file → empty result, no throw (defaults in
    /// code are the safety net). Reports unknown keys and dropped values
    /// through <paramref name="warn"/>.
    /// </summary>
    public static bool TryLoad(
        string path,
        out string activeTheme,
        out List<Theme> themes,
        Action<string> warn)
    {
        activeTheme = "default";
        themes = [];
        if (!File.Exists(path)) return false;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            warn($"unreadable override file, starting from defaults: {ex.Message}");
            return false;
        }

        if (root is not JsonObject rootObject)
        {
            warn("invalid override file root, starting from defaults");
            return false;
        }

        if (rootObject["activeTheme"] is JsonValue av && av.TryGetValue<string>(out var name))
        {
            activeTheme = name;
        }

        if (rootObject["themes"] is JsonObject themesNode)
        {
            foreach (var (themeName, themeNode) in themesNode)
            {
                if (themeNode is not JsonObject tn) continue;
                var theme = new Theme(themeName);

                if (tn["core"] is JsonObject core)
                {
                    foreach (var (token, valueNode) in core)
                    {
                        if (!Defaults.TryGet(token, out var def))
                        {
                            warn($"unknown token '{token}' in theme '{themeName}' ignored");
                            continue;
                        }
                        if (TokenValue.TryParse(def.Kind, valueNode, out var parsed))
                        {
                            theme.Core[token] = parsed;
                        }
                        else
                        {
                            warn($"invalid value for '{token}' in theme '{themeName}' dropped");
                        }
                    }
                }

                if (tn["module"] is JsonObject module)
                {
                    foreach (var (route, slots) in module)
                    {
                        if (slots is not JsonObject slotNodes)
                        {
                            warn($"invalid layout for route '{route}' in theme '{themeName}' dropped");
                            continue;
                        }

                        var sanitizedSlots = new JsonObject();
                        foreach (var (slot, properties) in slotNodes)
                        {
                            if (properties is not JsonObject propertyNodes)
                            {
                                warn($"invalid layout slot '{slot}' for route '{route}' in theme '{themeName}' dropped");
                                continue;
                            }

                            var sanitizedProperties = new JsonObject();
                            foreach (var (property, propertyValue) in propertyNodes)
                            {
                                if (!IsValidLayoutProperty(property, propertyValue, out var problem))
                                {
                                    warn($"invalid layout property '{property}' in slot '{slot}' for route '{route}' in theme '{themeName}' dropped: {problem}");
                                    continue;
                                }

                                sanitizedProperties[property] = propertyValue!.DeepClone();
                            }

                            if (sanitizedProperties.Count > 0)
                            {
                                sanitizedSlots[slot] = sanitizedProperties;
                            }
                        }

                        if (sanitizedSlots.Count > 0)
                        {
                            theme.Layouts[route] = sanitizedSlots;
                        }
                    }
                }

                themes.Add(theme);
            }
        }
        return true;
    }

    internal static bool IsValidLayoutProperty(string property, JsonNode? value, out string problem)
    {
        if (property is "x" or "y" or "w" or "h")
        {
            if (TokenValue.TryParse(TokenKind.Number, value, out _))
            {
                problem = string.Empty;
                return true;
            }

            problem = "expected a finite number";
            return false;
        }

        if (property == "visible")
        {
            if (value is JsonValue json && json.TryGetValue<bool>(out _))
            {
                problem = string.Empty;
                return true;
            }

            problem = "expected true or false";
            return false;
        }

        problem = "only x, y, w, h, visible are allowed";
        return false;
    }
}
