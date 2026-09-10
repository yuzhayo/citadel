using System.Windows;
using Module.Mangareader.Features.Downloader.Sources;

namespace Module.Mangareader.Features.Downloader.FilterSearch.DrakeScans;

/// <summary>Owns the single Drake filter draft hosted by Downloader.</summary>
public sealed class DrakeScansFilterContribution : IRemoteFilterContribution
{
    private DrakeScansFilterPanel? _panel;

    public IRemoteFilterState State => _panel ??= new DrakeScansFilterPanel();

    public FrameworkElement CreatePanel() => _panel ??= new DrakeScansFilterPanel();
}
