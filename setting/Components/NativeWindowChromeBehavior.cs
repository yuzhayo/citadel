using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Citadel.Setting.Components;

/// <summary>
/// Applies DWM dark mode and caption/border/text color to a window handle.
/// Shared component so both Shell and future standalone windows (e.g. ReaderWindow)
/// can call the same logic without taking a dependency on Citadel.Shell.
///
/// Unsupported DWM attributes fail soft; the standard frame remains usable.
/// </summary>
public static class NativeWindowChromeBehavior
{
    private const int UseImmersiveDarkMode = 20;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    public static void Apply(Window window, uint backgroundArgb, uint foregroundArgb)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;

        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int));

        var background = ToColorRef(backgroundArgb);
        var foreground = ToColorRef(foregroundArgb);
        _ = DwmSetWindowAttribute(handle, CaptionColor, ref background, sizeof(int));
        _ = DwmSetWindowAttribute(handle, BorderColor, ref background, sizeof(int));
        _ = DwmSetWindowAttribute(handle, TextColor, ref foreground, sizeof(int));
    }

    private static int ToColorRef(uint argb)
    {
        var red = (int)((argb >> 16) & 0xFF);
        var green = (int)((argb >> 8) & 0xFF);
        var blue = (int)(argb & 0xFF);
        return red | (green << 8) | (blue << 16);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref int value,
        int valueSize);
}
