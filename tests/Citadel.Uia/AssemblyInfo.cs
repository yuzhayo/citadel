using Xunit;

// WPF's resource loading is not thread-safe across assemblies: two STA threads
// calling InitializeComponent for the first time race inside
// System.IO.Packaging.PackagePart's stream bookkeeping and NRE. Loading a
// citizen's compiled BAML from a stream makes it reachable, so a searcher test
// and a Settings test could hit it together.
//
// The product does not have this problem — Router creates every view on the
// dispatcher thread — so serializing the suite pins the real constraint rather
// than papering over a product defect. It also stops this project's filesystem
// tests from starving Citadel.Core.Tests' timing assertions.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
