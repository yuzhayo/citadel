using System.Windows;
using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

/// <summary>
/// The single explicit registration point for ordinary Reader features.
/// ReaderWindow consumes the catalog without knowing any concrete feature.
/// </summary>
internal static class ReaderDefaultFeatureCatalog
{
    public static ReaderFeatureCatalog Create(
        Window window,
        ReaderSessionState state,
        ReaderCommandHub commands,
        ReaderActivityHub activity) =>
        new ReaderFeatureCatalog()
            .Add("ChapterLoading", static () => new ChapterLoadingFeature())
            .Add("Overlay", static () => new ReaderOverlay())
            .Add("Drawer", () => new ReaderDrawer(state, commands, activity))
            .Add("Chrome", static () => new ReaderChromeController())
            .Add("Toast", static () => new ReaderToast())
            .Add("ChapterNavigation", static () => new ReaderChapterNavigation())
            .Add("Fullscreen", () => new ReaderFullscreenController(window, state, commands))
            .Add("AutoScroll", () => new ReaderAutoScrollController(state, commands))
            .Add("Pin", () => new ReaderPinController(state, commands))
            .Add("Zoom", () => new ReaderZoomController(state, commands))
            .Add("Dim", () => new ReaderDimController(state, commands))
            .Add("Reset", () => new ReaderResetController(state, commands));
}
