using System.Windows;
using Module.Mangareader.Sources;

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
/// <see cref="Snapshot"/> returns the immutable keyword-aware state taken at Search.
///
/// Reset is deliberately not part of this contract: it belongs to the provider's
/// own Advanced Filters surface, and no host invokes it.
/// </summary>
public interface IRemoteFilterState
{
    IRemoteBrowseFilter? Snapshot(string? keyword);

    bool HasBlockingError { get; }

    string? ValidationMessage { get; }
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
public sealed class MangaSourceRegistry : IMangaSourceDirectory
{
    private readonly Dictionary<string, MangaSourceRegistration> _byId;
    private readonly IReadOnlyList<IMangaSource> _directory;

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
        _directory = registrations.Select(registration => registration.Source).ToArray();
    }

    public IReadOnlyList<MangaSourceRegistration> Sources { get; }

    /// <summary>
    /// The neutral projection a non-Downloader consumer resolves providers
    /// through. Same registrations, no filter contribution and no screen type.
    /// </summary>
    IReadOnlyList<IMangaSource> IMangaSourceDirectory.AvailableSources => _directory;

    IMangaSource? IMangaSourceDirectory.FindSource(string? sourceId) => Find(sourceId)?.Source;

    /// <summary>
    /// The one explicit registration point. Adding a provider means adding its
    /// cohesive files plus one entry here — the Downloader parent and Catalog
    /// composition are never edited for it.
    /// </summary>
    public static MangaSourceRegistry CreateDefault(DownloaderPyHostClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var comix = new Comix.ComixSource(client);
        var cucumberManga = new CucumberManga.CucumberMangaSource();
        var drakeScans = new DrakeScans.DrakeScansSource();
        return new MangaSourceRegistry(
        [
            new MangaSourceRegistration(
                comix,
                () => new global::Module.Mangareader.Features.Downloader.FilterSearch.Comix.ComixFilterContribution(
                    comix.LookupAsync)),
            new MangaSourceRegistration(
                cucumberManga,
                () => new global::Module.Mangareader.Features.Downloader.FilterSearch.CucumberManga.CucumberMangaFilterContribution()),
            new MangaSourceRegistration(
                drakeScans,
                () => new global::Module.Mangareader.Features.Downloader.FilterSearch.DrakeScans.DrakeScansFilterContribution()),
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
