using System.Windows;
using System.Windows.Controls;

namespace Module.Mangareader;

public sealed class ReaderFeatureHostTests
{
    [Fact]
    public void CatalogOnlyFeature_MountsOnceAndDisposesInReverseOrder()
    {
        WpfTest.Run(() =>
        {
            var lifecycle = new List<string>();
            var view = new Border();
            var drawer = new TestDrawerFeature(lifecycle);
            var feature = new TestVisualFeature("Dummy", ReaderLayer.Overlay, view, lifecycle);
            var catalog = new ReaderFeatureCatalog()
                .Add("Drawer", () => drawer)
                .Add("Dummy", () => feature);
            var layer = new ContentControl();
            var hosts = new Dictionary<ReaderLayer, ContentControl>
            {
                [ReaderLayer.Overlay] = layer,
            };

            var host = new ReaderFeatureHost(ReaderTestContext.Create(), hosts, catalog);

            Assert.Same(view, layer.Content);
            Assert.Equal(["Drawer:attach", "Dummy:attach"], lifecycle);
            Assert.Equal(["early", "late"], drawer.ReceivedContributions.Select(item => item.Key));

            host.Dispose();
            host.Dispose();

            Assert.Null(layer.Content);
            Assert.Equal(
                ["Drawer:attach", "Dummy:attach", "Dummy:dispose", "Drawer:dispose"],
                lifecycle);
        });
    }

    [Fact]
    public void DuplicateLayer_IsRejectedAndPartiallyAttachedFeaturesAreCleanedUp()
    {
        WpfTest.Run(() =>
        {
            var lifecycle = new List<string>();
            var layer = new ContentControl();
            var catalog = new ReaderFeatureCatalog()
                .Add("Drawer", () => new TestDrawerFeature(lifecycle))
                .Add("First", () => new TestVisualFeature(
                    "First", ReaderLayer.Overlay, new Border(), lifecycle))
                .Add("Second", () => new TestVisualFeature(
                    "Second", ReaderLayer.Overlay, new Border(), lifecycle));

            var error = Assert.Throws<InvalidOperationException>(() =>
                new ReaderFeatureHost(
                    ReaderTestContext.Create(),
                    new Dictionary<ReaderLayer, ContentControl>
                    {
                        [ReaderLayer.Overlay] = layer,
                    },
                    catalog));

            Assert.Contains("already occupied", error.Message, StringComparison.Ordinal);
            Assert.Null(layer.Content);
            Assert.Equal(
                [
                    "Drawer:attach",
                    "First:attach",
                    "Second:attach",
                    "Second:dispose",
                    "First:dispose",
                    "Drawer:dispose",
                ],
                lifecycle);
        });
    }

    [Fact]
    public void MissingLayerAndMissingDrawerHost_AreStructuralErrors()
    {
        WpfTest.Run(() =>
        {
            var missingLayer = new ReaderFeatureCatalog()
                .Add("Drawer", () => new TestDrawerFeature([]))
                .Add("Visual", () => new TestVisualFeature(
                    "Visual", ReaderLayer.Toast, new Border(), []));
            Assert.Throws<InvalidOperationException>(() =>
                new ReaderFeatureHost(
                    ReaderTestContext.Create(),
                    new Dictionary<ReaderLayer, ContentControl>(),
                    missingLayer));

            var missingDrawer = new ReaderFeatureCatalog()
                .Add("Plain", () => new TestFeature("Plain", []));
            var error = Assert.Throws<InvalidOperationException>(() =>
                new ReaderFeatureHost(
                    ReaderTestContext.Create(),
                    new Dictionary<ReaderLayer, ContentControl>(),
                    missingDrawer));
            Assert.Contains("exactly one Drawer", error.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CatalogRejectsDuplicateFeatureKeys()
    {
        var catalog = new ReaderFeatureCatalog()
            .Add("One", () => new TestFeature("One", []));

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Add("One", () => new TestFeature("One", [])));
    }

    [Fact]
    public void DisposeDuringStart_DoesNotInvalidateTheLifecycleIteration()
    {
        WpfTest.Run(() =>
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var catalog = new ReaderFeatureCatalog()
                .Add("Drawer", () => new TestDrawerFeature([]))
                .Add("Startable", () => new TestStartableFeature(completion.Task));
            var host = new ReaderFeatureHost(
                ReaderTestContext.Create(),
                new Dictionary<ReaderLayer, ContentControl>(),
                catalog);

            var start = host.StartAsync();
            host.Dispose();
            completion.SetResult();

            start.GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void HostRejectsDuplicateDrawerContributionKeys()
    {
        WpfTest.Run(() =>
        {
            var catalog = new ReaderFeatureCatalog()
                .Add("Drawer", () => new TestDrawerFeature([]))
                .Add("Duplicate", () => new TestContributionFeature());

            var error = Assert.Throws<InvalidOperationException>(() =>
                new ReaderFeatureHost(
                    ReaderTestContext.Create(),
                    new Dictionary<ReaderLayer, ContentControl>(),
                    catalog));

            Assert.Contains("registered more than once", error.Message, StringComparison.Ordinal);
        });
    }

    private class TestFeature(string name, List<string> lifecycle) : IReaderFeature
    {
        public string FeatureName => name;

        public virtual void Attach(ReaderFeatureContext context) =>
            lifecycle.Add($"{name}:attach");

        public virtual void Dispose() => lifecycle.Add($"{name}:dispose");
    }

    private sealed class TestVisualFeature(
        string name,
        ReaderLayer layer,
        FrameworkElement view,
        List<string> lifecycle)
        : TestFeature(name, lifecycle), IReaderVisualFeature
    {
        public IReadOnlyList<ReaderVisualContribution> Visuals { get; } =
            [new ReaderVisualContribution(layer, view)];
    }

    private sealed class TestDrawerFeature(List<string> lifecycle)
        : TestFeature("Drawer", lifecycle),
            IReaderDrawerContributionHost,
            IReaderDrawerContributionProvider
    {
        public IReadOnlyList<ReaderDrawerContribution> DrawerContributions { get; } =
            [new OrderedContribution("late", 20), new OrderedContribution("early", 10)];
        public IReadOnlyList<ReaderDrawerContribution> ReceivedContributions { get; private set; } = [];

        public void SetContributions(IReadOnlyList<ReaderDrawerContribution> contributions) =>
            ReceivedContributions = contributions;
    }

    private sealed class OrderedContribution(string key, int order)
        : ReaderDrawerContribution(key, order);

    private sealed class TestContributionFeature : IReaderFeature, IReaderDrawerContributionProvider
    {
        public string FeatureName => "Duplicate";
        public IReadOnlyList<ReaderDrawerContribution> DrawerContributions { get; } =
            [new OrderedContribution("early", 99)];
        public void Attach(ReaderFeatureContext context) { }
        public void Dispose() { }
    }

    private sealed class TestStartableFeature(Task start) : IReaderFeature, IReaderStartableFeature
    {
        public string FeatureName => "Startable";
        public void Attach(ReaderFeatureContext context) { }
        public Task StartAsync() => start;
        public void Dispose() { }
    }
}
