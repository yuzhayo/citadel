using System.IO;
using System.Text;
using System.Text.Json;
using Citadel.Core.Tokens;
using Citadel.Setting;

namespace Citadel.Shell;

/// <summary>
/// Shell-owned sidebar presentation state. Routes are opaque identities: this
/// store never discovers, creates, or owns module instances.
/// </summary>
internal sealed class SidebarGroupingStore
{
    private const string FileName = "sidebar-groups.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly List<MutableGroup> _groups = [];

    internal SidebarGroupingStore(string? path = null)
    {
        _path = path ?? DefaultPath();
        Load();
    }

    internal event Action? Changed;

    internal IReadOnlyList<SidebarGroup> Snapshot() =>
        [.. _groups.Select(group => group.Snapshot())];

    internal string Create(string name)
    {
        var normalized = ValidName(name);
        var group = new MutableGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = normalized,
            Expanded = true,
        };
        _groups.Add(group);
        Commit();
        return group.Id;
    }

    internal void Rename(string id, string name)
    {
        var group = Find(id);
        group.Name = ValidName(name);
        Commit();
    }

    internal void Delete(string id)
    {
        var group = Find(id);
        _groups.Remove(group);
        Commit();
    }

    internal void SetMembership(string id, string route, bool included)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var target = Find(id);
        if (included)
        {
            foreach (var group in _groups) group.Routes.RemoveAll(item => Same(item, route));
            target.Routes.Add(route);
        }
        else
        {
            target.Routes.RemoveAll(item => Same(item, route));
        }
        Commit();
    }

    internal void SetExpanded(string id, bool expanded)
    {
        var group = Find(id);
        if (group.Expanded == expanded) return;
        group.Expanded = expanded;
        Commit();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path));
            if (document?.Groups is null) return;
            var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in document.Groups)
            {
                if (string.IsNullOrWhiteSpace(input.Id) || string.IsNullOrWhiteSpace(input.Name)) continue;
                if (_groups.Any(group => Same(group.Id, input.Id))) continue;
                var group = new MutableGroup
                {
                    Id = input.Id.Trim(),
                    Name = input.Name.Trim(),
                    Expanded = input.Expanded,
                };
                foreach (var route in input.Routes ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(route) && assigned.Add(route)) group.Routes.Add(route);
                }
                _groups.Add(group);
            }
        }
        catch (Exception exception)
        {
            Citadel.Core.Log.Main($"[SidebarGroups] unreadable state ignored: {exception.Message}");
        }
    }

    private void Commit()
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("sidebar group store has no directory");
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            var payload = JsonSerializer.Serialize(new Document { Groups = _groups }, JsonOptions);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(payload);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw new InvalidOperationException($"sidebar groups could not be saved: {exception.Message}", exception);
        }
        Changed?.Invoke();
    }

    private MutableGroup Find(string id) => _groups.FirstOrDefault(group => Same(group.Id, id))
        ?? throw new ArgumentException($"unknown sidebar group '{id}'", nameof(id));

    private static string ValidName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim();
    }

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string DefaultPath()
    {
        var executableDirectory = AppContext.BaseDirectory;
        return File.Exists(Path.Combine(executableDirectory, Overrides.PortableMarker))
            ? Path.Combine(executableDirectory, FileName)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Citadel", FileName);
    }

    private sealed class Document
    {
        public List<MutableGroup>? Groups { get; set; }
    }

    private sealed class MutableGroup
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool Expanded { get; set; } = true;
        public List<string> Routes { get; set; } = [];

        public SidebarGroup Snapshot() => new(Id, Name, Expanded, [.. Routes]);
    }
}
