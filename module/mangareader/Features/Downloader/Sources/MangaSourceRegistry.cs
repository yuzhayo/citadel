using System.Windows;

namespace Module.Mangareader.Features.Downloader.Sources;

/// <summary>
/// The provider-owned Advanced Filters contribution. Catalog hosts the panel
/// and reads the current immutable query; it never sees a concrete provider
/// filter type and never builds provider URLs.
/// </summary>
public interface IRemoteFilterContribution
{
    FrameworkElement CreatePanel();

    IRemoteFilterState State { get; }
}

/// <summary>
/// Filter input state and local validation, owned by the provider feature.
/// <see cref="CurrentFilter"/> is an immutable snapshot taken at Start.
/// </summary>
public interface IRemoteFilterState
{
    IRemoteBrowseFilter? CurrentFilter { get; }

    bool HasBlockingError { get; }

    string? ValidationMessage { get; }

    /// <summary>Restores provider defaults. Must not trigger a request.</summary>
    void Reset();
}

/// <summary>
/// One provider entry: the screen-blind source adapter paired with its
/// provider-specific filter contribution. Adding a provider adds its own
/// cohesive files plus one registration here, and edits neither the Downloader
/// parent nor Catalog composition.
/// </summary>
public sealed record MangaSourceRegistration(
    IMangaSource Source,
    Func<IRemoteFilterContribution> CreateFilters)
{
    public string Id => Source.Id;

    public string DisplayName => Source.DisplayName;
}

/// <summary>
/// An explicit, closed list of sources. No reflection, no filesystem scan, no
/// runtime discovery: this is an application seam, not a plugin framework.
/// </summary>
public sealed class MangaSourceRegistry
{
    private readonly Dictionary<string, MangaSourceRegistration> _byId;

    public MangaSourceRegistry(IReadOnlyList<MangaSourceRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _byId = new Dictionary<string, MangaSourceRegistration>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (!_byId.TryAdd(registration.Id, registration))
            {
                throw new ArgumentException(
                    "duplicate manga source id: " + registration.Id,
                    nameof(registrations));
            }
        }

        Sources = registrations;
    }

    public IReadOnlyList<MangaSourceRegistration> Sources { get; }

    /// <summary>
    /// The one explicit registration point. Adding a provider means adding its
    /// cohesive files plus one entry here — the Downloader parent and Catalog
    /// composition are never edited for it.
    /// </summary>
    public static MangaSourceRegistry CreateDefault(DownloaderPyHostClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var comix = new Comix.ComixSource(client);
        return new MangaSourceRegistry(
        [
            new MangaSourceRegistration(
                comix,
                () => new Comix.ComixFilterContribution(comix.LookupAsync)),
        ]);
    }

    public MangaSourceRegistration? Find(string? sourceId) =>
        sourceId is not null && _byId.TryGetValue(sourceId, out var registration)
            ? registration
            : null;

    public MangaSourceRegistration Require(string? sourceId) =>
        Find(sourceId) ?? throw new InvalidOperationException(
            "unknown manga source: " + (sourceId ?? "<null>"));
}
