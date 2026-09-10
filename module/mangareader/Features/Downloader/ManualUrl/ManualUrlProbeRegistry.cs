using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Features.Downloader.Sources.Comix;
using Module.Mangareader.Features.Downloader.Sources.DrakeScans;

namespace Module.Mangareader.Features.Downloader.ManualUrl;

/// <summary>Builds the explicit, fixed Manual URL probe order without changing the browse dropdown order.</summary>
public static class ManualUrlProbeRegistry
{
    public static IReadOnlyList<IManualUrlProbe> Create(MangaSourceRegistry sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var probes = new List<IManualUrlProbe>(3);
        if (sources.Find(DrakeScansContract.SourceId)?.Source is DrakeScansSource drake)
        {
            probes.Add(new DrakeScansManualUrlProbe(drake));
        }
        if (sources.Find(ComixContract.SourceId)?.Source is ComixSource comix)
        {
            probes.Add(new ComixManualUrlProbe(comix));
        }
        if (sources.FindSource(DynamicManualSource.SourceId) is DynamicManualSource dynamicManual)
        {
            probes.Add(new DynamicManualUrlProbe(dynamicManual));
        }

        return probes;
    }
}
