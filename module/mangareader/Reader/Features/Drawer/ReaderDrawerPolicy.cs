using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

/// <summary>Defines which outside Reader actions auto-close an unpinned Drawer.</summary>
public static class ReaderDrawerPolicy
{
    public static bool ShouldCloseForActivity(ReaderActivityOrigin origin, bool isPinned) =>
        !isPinned
        && origin is ReaderActivityOrigin.OverlayStep
            or ReaderActivityOrigin.ChapterJump
            or ReaderActivityOrigin.Zoom;
}
