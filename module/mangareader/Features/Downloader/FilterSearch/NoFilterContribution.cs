using System.Windows;
using System.Windows.Controls;
using Module.Mangareader.Features.Downloader.Sources;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.FilterSearch;

/// <summary>Explicit empty filter contract for a source with keyword-only search.</summary>
public sealed class NoFilterContribution : IRemoteFilterContribution, IRemoteFilterState
{
    private static readonly Grid EmptyPanel = new();

    public IRemoteFilterState State => this;

    public FrameworkElement CreatePanel() => EmptyPanel;

    public IRemoteBrowseFilter? Snapshot(string? keyword) => null;

    public bool HasBlockingError => false;

    public string? ValidationMessage => null;
}
