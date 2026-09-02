using System.Windows;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace Module.Mangareader;

public readonly record struct ReaderPixelRect(int Left, int Top, int Right, int Bottom);

[StructLayout(LayoutKind.Sequential)]
public struct ReaderNativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct ReaderMonitorInfoEx
{
    public int Size;
    public ReaderNativeRect Monitor;
    public ReaderNativeRect WorkArea;
    public uint Flags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;
}

public static class ReaderFullscreenGeometry
{
    public static Rect ToDipRect(ReaderPixelRect pixels, Matrix transformFromDevice)
    {
        if (pixels.Right <= pixels.Left || pixels.Bottom <= pixels.Top)
            throw new ArgumentOutOfRangeException(nameof(pixels));

        var topLeft = transformFromDevice.Transform(new Point(pixels.Left, pixels.Top));
        var bottomRight = transformFromDevice.Transform(new Point(pixels.Right, pixels.Bottom));
        return new Rect(
            topLeft.X,
            topLeft.Y,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);
    }
}
