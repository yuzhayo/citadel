using Module.Mangareader.Features.Catalog.Runtime;
using Module.Mangareader.Features.Catalog.Sources.Comix;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Catalog;

/// <summary>
/// Closed provider directory owned by the Catalog tab. It deliberately does
/// not import Downloader's registry, filter contribution, context, or browser.
/// </summary>
public sealed class CatalogSourceDirectory : IMangaSourceDirectory
{
    private readonly IReadOnlyList<IMangaSource> _sources;

    public CatalogSourceDirectory(CatalogBrowserClient browser)
    {
        ArgumentNullException.ThrowIfNull(browser);
        _sources = [new CatalogComixSource(browser)];
    }

    public IReadOnlyList<IMangaSource> AvailableSources => _sources;

    public IMangaSource? FindSource(string? sourceId) =>
        _sources.FirstOrDefault(source =>
            string.Equals(source.Id, sourceId, StringComparison.Ordinal));
}
