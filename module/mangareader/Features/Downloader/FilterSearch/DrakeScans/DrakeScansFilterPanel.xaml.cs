using System.Windows.Controls;
using Module.Mangareader.Components;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Features.Downloader.Sources.DrakeScans;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.FilterSearch.DrakeScans;

/// <summary>
/// Drake-owned filter draft composed from the existing MangaReader checkbox
/// dropdown. Changing it never starts a network request.
/// </summary>
public sealed partial class DrakeScansFilterPanel : UserControl, IRemoteFilterState
{
    public DrakeScansFilterPanel()
    {
        InitializeComponent();
        StatusFilter.Options = CheckOptions(DrakeScansOptions.Statuses);
        GenreFilter.Options = CheckOptions(DrakeScansOptions.Genres);
    }

    public IRemoteBrowseFilter Snapshot(string? keyword) => new DrakeScansBrowseQuery
    {
        Statuses = StatusFilter.SelectedKeys,
        Genres = GenreFilter.SelectedKeys,
    };

    public bool HasBlockingError => false;

    public string? ValidationMessage => null;

    private static List<MangaFilterOption> CheckOptions(IReadOnlyList<RemoteOption> options) =>
        [.. options.Select(option => new MangaFilterOption(option.Key, option.DisplayName))];
}
