using System.Windows.Input;
using Module.Mangareader.ReaderCore;

namespace Module.Mangareader;

public enum ReaderEscapeAction
{
    ExitFullscreen,
    CloseDrawer,
    CloseReader,
}

public enum ReaderKeyAction
{
    None,
    ToggleFullscreen,
    DimLighter,
    DimDarker,
    ResetDim,
    ResetZoom,
    ReportKeyboardScroll,
}

/// <summary>Pure input decisions shared by the WPF router and regression tests.</summary>
public static class ReaderInputPolicy
{
    public static ReaderEscapeAction ResolveEscape(IReaderStateView state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.IsFullscreen) return ReaderEscapeAction.ExitFullscreen;
        return state.IsDrawerOpen
            ? ReaderEscapeAction.CloseDrawer
            : ReaderEscapeAction.CloseReader;
    }

    public static ReaderOverlayZone ResolveOverlayZone(double x, double viewportWidth)
    {
        if (!double.IsFinite(x) || !double.IsFinite(viewportWidth) || viewportWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewportWidth));

        var third = viewportWidth / 3;
        if (x < third) return ReaderOverlayZone.Previous;
        return x < third * 2 ? ReaderOverlayZone.Menu : ReaderOverlayZone.Next;
    }

    public static ReaderKeyAction ResolveKey(Key key, ModifierKeys modifiers)
    {
        if (key == Key.F11) return ReaderKeyAction.ToggleFullscreen;

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            if (key == Key.Up) return ReaderKeyAction.DimLighter;
            if (key == Key.Down) return ReaderKeyAction.DimDarker;
            if (key is Key.D0 or Key.NumPad0) return ReaderKeyAction.ResetDim;
            return ReaderKeyAction.None;
        }

        if ((modifiers & ModifierKeys.Control) != 0
            && key is Key.D0 or Key.NumPad0)
        {
            return ReaderKeyAction.ResetZoom;
        }

        return key is Key.Up or Key.Down or Key.Left or Key.Right
            or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Space
            ? ReaderKeyAction.ReportKeyboardScroll
            : ReaderKeyAction.None;
    }

    public static bool ExceedsDragThreshold(
        double deltaX,
        double deltaY,
        double horizontalThreshold,
        double verticalThreshold) =>
        Math.Abs(deltaX) > horizontalThreshold || Math.Abs(deltaY) > verticalThreshold;
}
