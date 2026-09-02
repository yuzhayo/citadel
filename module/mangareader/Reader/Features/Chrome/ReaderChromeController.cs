using Citadel.Setting.Components;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

public sealed class ReaderChromeController : IReaderFeature, IReaderVisualFeature
{
    private readonly SettingWindowChrome _chrome = new();
    private ReaderFeatureContext? _context;

    public ReaderChromeController()
    {
        Visuals = [new ReaderVisualContribution(ReaderLayer.Chrome, _chrome)];
    }

    public string FeatureName => "Chrome";
    public IReadOnlyList<ReaderVisualContribution> Visuals { get; }

    public void Attach(ReaderFeatureContext context)
    {
        _context = context;
        UpdateTitle();
        context.Chapters.ActiveChapterChanged += OnActiveChapterChanged;
    }

    private void OnActiveChapterChanged(object? sender, OpenChapterRequestedEventArgs e) =>
        UpdateTitle();

    private void UpdateTitle()
    {
        if (_context is null) return;
        _chrome.Title = $"{_context.Chapters.MangaTitle} — {_context.Chapters.ActiveChapter.Title} — Manga Reader";
    }

    public void Dispose()
    {
        if (_context is not null)
            _context.Chapters.ActiveChapterChanged -= OnActiveChapterChanged;
        _context = null;
    }
}
