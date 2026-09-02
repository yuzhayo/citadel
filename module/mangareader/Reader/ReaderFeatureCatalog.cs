namespace Module.Mangareader;

public enum ReaderLayer
{
    Dim = 10,
    DrawerBackdrop = 20,
    Overlay = 30,
    Chrome = 40,
    Drawer = 50,
    Toast = 60,
}

public sealed class ReaderFeatureCatalog
{
    private readonly List<ReaderFeatureEntry> _entries = [];

    public IReadOnlyList<ReaderFeatureEntry> Entries => _entries;

    public ReaderFeatureCatalog Add(string name, Func<IReaderFeature> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        if (_entries.Any(entry => string.Equals(entry.Name, name, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Reader feature '{name}' is already registered.");

        _entries.Add(new ReaderFeatureEntry(name, factory));
        return this;
    }
}

public sealed record ReaderFeatureEntry(string Name, Func<IReaderFeature> Factory);
