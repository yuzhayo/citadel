using System.IO;
using System.Text.Json;
using CitadelBridge;

namespace Module.Proxy.SharedLogic;

internal sealed class ProxySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public ProxySettingsStore(string? path = null) =>
        _path = Path.GetFullPath(path ?? ProxyPoolContract.SettingsPath);

    public string? LastLoadWarning { get; private set; }

    public ProxySettings Load()
    {
        LastLoadWarning = null;
        if (!File.Exists(_path))
        {
            return new ProxySettings();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<ProxySettings>(File.ReadAllText(_path));
            return (loaded ?? new ProxySettings()).Validate();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LastLoadWarning = "Settings could not be read; defaults are active: " + ex.Message;
            return new ProxySettings();
        }
    }

    public ProxySettings Save(ProxySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var valid = settings.Validate();
        AtomicTextFile.WriteAllText(_path, JsonSerializer.Serialize(valid, JsonOptions));
        return valid;
    }

    public ProxySettings Reset() => Save(new ProxySettings());
}
