using System.Windows;
using Citadel.Core.Modules;
using Citadel.Core.Rpl;

namespace Module.Mangareader;

public sealed class MangaReaderModule : IModule
{
    public string Route => "manga-reader";

    public FrameworkElement CreateView(Lifetime lifetime) => new MangaReaderView(lifetime);
}
