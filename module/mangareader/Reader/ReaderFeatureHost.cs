using System.Windows.Controls;

namespace Module.Mangareader;

/// <summary>
/// Attaches the explicit catalog to the stable XAML hosts. It never creates or
/// appends a second layer tree and always disposes in reverse attach order.
/// </summary>
public sealed class ReaderFeatureHost : IDisposable
{
    private readonly IReadOnlyDictionary<ReaderLayer, ContentControl> _hosts;
    private readonly List<IReaderFeature> _features = [];
    private Task? _startTask;
    private bool _disposed;

    public ReaderFeatureHost(
        ReaderFeatureContext context,
        IReadOnlyDictionary<ReaderLayer, ContentControl> hosts,
        ReaderFeatureCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(context);
        _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
        ArgumentNullException.ThrowIfNull(catalog);

        try
        {
            foreach (var entry in catalog.Entries)
            {
                var feature = entry.Factory()
                    ?? throw new InvalidOperationException($"Reader feature '{entry.Name}' returned null.");
                if (!string.Equals(entry.Name, feature.FeatureName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Reader feature key '{entry.Name}' does not match '{feature.FeatureName}'.");
                }

                _features.Add(feature);
                feature.Attach(context);
                MountVisuals(feature);
            }

            var drawerHosts = _features.OfType<IReaderDrawerContributionHost>().ToArray();
            if (drawerHosts.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Reader requires exactly one Drawer contribution host; found {drawerHosts.Length}.");
            }

            var contributions = _features
                .OfType<IReaderDrawerContributionProvider>()
                .SelectMany(provider => provider.DrawerContributions)
                .OrderBy(contribution => contribution.Order)
                .ThenBy(contribution => contribution.Key, StringComparer.Ordinal)
                .ToArray();
            var duplicateContribution = contributions
                .GroupBy(contribution => contribution.Key, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Skip(1).Any());
            if (duplicateContribution is not null)
            {
                throw new InvalidOperationException(
                    $"Reader Drawer contribution '{duplicateContribution.Key}' is registered more than once.");
            }
            drawerHosts[0].SetContributions(contributions);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void MountVisuals(IReaderFeature feature)
    {
        if (feature is not IReaderVisualFeature visualFeature) return;

        foreach (var contribution in visualFeature.Visuals)
        {
            if (!_hosts.TryGetValue(contribution.Layer, out var host))
                throw new InvalidOperationException($"No XAML host exists for Reader layer {contribution.Layer}.");
            if (host.Content is not null)
                throw new InvalidOperationException($"Reader layer {contribution.Layer} is already occupied.");
            host.Content = contribution.View;
        }
    }

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _startTask ??= StartFeaturesAsync();
    }

    private async Task StartFeaturesAsync()
    {
        var startableFeatures = _features.OfType<IReaderStartableFeature>().ToArray();
        foreach (var feature in startableFeatures)
            await feature.StartAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (var index = _features.Count - 1; index >= 0; index--)
            _features[index].Dispose();
        _features.Clear();

        foreach (var host in _hosts.Values) host.Content = null;
    }
}
