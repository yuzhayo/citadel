namespace Module.Mangareader;

/// <summary>Defines whether the center Overlay zone may toggle the Drawer.</summary>
public static class ReaderDrawerPolicy
{
    public static bool CanToggleFromOverlay(bool isOpen, bool isPinned) =>
        !isOpen || !isPinned;
}
