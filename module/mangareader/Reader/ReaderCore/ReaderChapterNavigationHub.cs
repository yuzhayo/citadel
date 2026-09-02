using System.Collections.ObjectModel;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader.ReaderCore;

/// <summary>
/// Late-bound chapter-navigation facade owned by the composition root.
/// ReaderWindow creates it seeded only with chapter metadata; the ChapterLoading
/// feature registers the live implementation during attach; every other feature
/// consumes navigation through it. Seeding the static metadata keeps consumers
/// independent of catalog attach order, while surfaces and jumps forward to the
/// registered implementation once it exists.
/// </summary>
public sealed class ReaderChapterNavigationHub : IReaderChapterNavigation
{
    private static readonly ReadOnlyObservableCollection<ChapterSurfaceModel> NoSurfaces =
        new(new ObservableCollection<ChapterSurfaceModel>());

    private readonly MangaTitle _title;
    private readonly int _initialChapterIndex;
    private IReaderChapterNavigation? _implementation;
    private Func<Task>? _startLoading;

    public ReaderChapterNavigationHub(MangaTitle title, ChapterInfo initialChapter)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(initialChapter);
        _title = title;
        _initialChapterIndex = FindChapterIndex(title.Chapters, initialChapter);
    }

    public event EventHandler<OpenChapterRequestedEventArgs>? ActiveChapterChanged;

    public IReadOnlyList<ChapterInfo> Chapters => _title.Chapters;
    public ReadOnlyObservableCollection<ChapterSurfaceModel> Surfaces =>
        _implementation?.Surfaces ?? NoSurfaces;
    public int ActiveChapterIndex => _implementation?.ActiveChapterIndex ?? _initialChapterIndex;
    public ChapterInfo ActiveChapter =>
        _implementation?.ActiveChapter ?? _title.Chapters[_initialChapterIndex];
    public string MangaTitle => _title.Title;
    public bool CanNavigatePrevious =>
        _implementation?.CanNavigatePrevious ?? _initialChapterIndex > 0;
    public bool CanNavigateNext =>
        _implementation?.CanNavigateNext ?? _initialChapterIndex + 1 < _title.Chapters.Count;
    public bool IsAtAbsoluteBeginning => _implementation?.IsAtAbsoluteBeginning ?? false;
    public bool IsAtAbsoluteEnd => _implementation?.IsAtAbsoluteEnd ?? false;

    public void RegisterImplementation(
        IReaderChapterNavigation implementation,
        Func<Task>? startLoading = null)
    {
        ArgumentNullException.ThrowIfNull(implementation);
        if (_implementation is not null)
            throw new InvalidOperationException("Reader chapter navigation is already registered.");
        _implementation = implementation;
        _startLoading = startLoading;
        _implementation.ActiveChapterChanged += OnImplementationActiveChapterChanged;
    }

    public void UnregisterImplementation()
    {
        if (_implementation is null) return;
        _implementation.ActiveChapterChanged -= OnImplementationActiveChapterChanged;
        _implementation = null;
        _startLoading = null;
    }

    /// <summary>
    /// Triggers the initial chapter load through the registered implementation.
    /// Called by the composition root once the window is loaded and the viewport
    /// has a usable width. A no-op before registration.
    /// </summary>
    public Task StartLoadingAsync() => _startLoading?.Invoke() ?? Task.CompletedTask;

    public Task NavigateToChapterAsync(int index) =>
        RequireImplementation().NavigateToChapterAsync(index);

    public Task PrepareBoundaryAsync(int direction, CancellationToken cancellationToken) =>
        RequireImplementation().PrepareBoundaryAsync(direction, cancellationToken);

    public void NotifyZoomChanged() => RequireImplementation().NotifyZoomChanged();

    private IReaderChapterNavigation RequireImplementation() =>
        _implementation ?? throw new InvalidOperationException(
            "Reader chapter navigation is not ready.");

    private void OnImplementationActiveChapterChanged(
        object? sender,
        OpenChapterRequestedEventArgs e) =>
        ActiveChapterChanged?.Invoke(this, e);

    private static int FindChapterIndex(
        IReadOnlyList<ChapterInfo> chapters,
        ChapterInfo chapter)
    {
        for (var index = 0; index < chapters.Count; index++)
        {
            if (string.Equals(
                chapters[index].FilePath,
                chapter.FilePath,
                StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw new ArgumentException("The chapter does not belong to this title.", nameof(chapter));
    }
}
