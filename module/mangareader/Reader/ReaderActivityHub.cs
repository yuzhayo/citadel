namespace Module.Mangareader;

public enum ReaderActivityOrigin
{
    ManualScroll,
    ManualWheel,
    ManualPointer,
    ManualTouch,
    KeyboardScroll,
    OverlayStep,
    AutoScroll,
    ChapterJump,
    Zoom,
    ControlsReset,
    LayoutRestore,
    DrawerOpened,
    WindowDeactivated,
    Loading,
}

public sealed class ReaderActivityEventArgs(ReaderActivityOrigin origin) : EventArgs
{
    public ReaderActivityOrigin Origin { get; } = origin;
}

/// <summary>One typed activity stream shared by viewport and Reader features.</summary>
public sealed class ReaderActivityHub
{
    public event EventHandler<ReaderActivityEventArgs>? ActivityOccurred;

    public ReaderActivityOrigin? LastOrigin { get; private set; }

    public void Report(ReaderActivityOrigin origin)
    {
        LastOrigin = origin;
        ActivityOccurred?.Invoke(this, new ReaderActivityEventArgs(origin));
    }
}
