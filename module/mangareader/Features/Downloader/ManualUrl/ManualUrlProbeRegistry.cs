using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Features.Downloader.Sources.Comix;
using Module.Mangareader.Features.Downloader.Sources.CucumberManga;
using Module.Mangareader.Features.Downloader.Sources.DrakeScans;
using Module.Mangareader.Features.Downloader.Sources.AsuraScans;
using Module.Mangareader.Features.Downloader.Sources.ThunderScans;

namespace Module.Mangareader.Features.Downloader.ManualUrl;

/// <summary>Builds the explicit, fixed Manual URL probe order without changing the browse dropdown order.</summary>
public static class ManualUrlProbeRegistry
{
    public static IReadOnlyList<IManualUrlProbe> Create(MangaSourceRegistry sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var probes = new List<IManualUrlProbe>(6);
        if (sources.Find(AsuraScansContract.SourceId)?.Source is AsuraScansSource asura)
        {
            probes.Add(new AsuraScansManualUrlProbe(asura));
        }
        if (sources.Find(ThunderScansContract.SourceId)?.Source is ThunderScansSource thunder)
        {
            probes.Add(new ThunderScansManualUrlProbe(thunder));
        }
        if (sources.Find(DrakeScansContract.SourceId)?.Source is DrakeScansSource drake)
        {
            probes.Add(new DrakeScansManualUrlProbe(drake));
        }
        if (sources.Find(CucumberMangaContract.SourceId)?.Source is CucumberMangaSource cucumber)
        {
            probes.Add(new CucumberMangaManualUrlProbe(cucumber));
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
