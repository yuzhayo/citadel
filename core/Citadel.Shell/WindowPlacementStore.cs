using System.IO;
using System.Text.Json;
using System.Windows;

namespace Citadel.Shell;

/// <summary>Shell-owned durable geometry; modules and shared UI never own window placement.</summary>
internal sealed class WindowPlacementStore(string? path = null)
{
    private readonly string _path = Path.GetFullPath(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Citadel",
        "shell-window.json"));

    internal WindowPlacement? TryLoad()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(_path));
            return value is { Bounds.Width: > 0, Bounds.Height: > 0 }
                && double.IsFinite(value.Bounds.Left)
                && double.IsFinite(value.Bounds.Top)
                && double.IsFinite(value.Bounds.Width)
                && double.IsFinite(value.Bounds.Height)
                ? value
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal void Save(WindowPlacement placement)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("window placement path has no parent");
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(placement));
        File.Move(temporary, _path, overwrite: true);
    }
}

internal sealed record WindowPlacement(Rect Bounds, bool IsMaximized);
