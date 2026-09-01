using System.Windows;
using WpfSize = System.Windows.Size;

namespace Citadel.Shell;

/// <summary>Pure one-time preferred-size and monitor-work-area policy.</summary>
internal static class WindowBoundsPolicy
{
    private const double DipDpi = 96d;

    internal static Rect Calculate(WpfSize preferred, WpfSize minimum, Rect workArea)
    {
        RequirePositive(preferred.Width, nameof(preferred));
        RequirePositive(preferred.Height, nameof(preferred));
        RequirePositive(minimum.Width, nameof(minimum));
        RequirePositive(minimum.Height, nameof(minimum));
        RequirePositive(workArea.Width, nameof(workArea));
        RequirePositive(workArea.Height, nameof(workArea));

        var effectiveMinimumWidth = Math.Min(minimum.Width, workArea.Width);
        var effectiveMinimumHeight = Math.Min(minimum.Height, workArea.Height);
        var width = Math.Clamp(preferred.Width, effectiveMinimumWidth, workArea.Width);
        var height = Math.Clamp(preferred.Height, effectiveMinimumHeight, workArea.Height);
        var left = workArea.Left + ((workArea.Width - width) / 2d);
        var top = workArea.Top + ((workArea.Height - height) / 2d);
        return new Rect(left, top, width, height);
    }

    internal static Rect PixelsToDips(Rect pixels, double dpiX, double dpiY)
    {
        RequirePositive(dpiX, nameof(dpiX));
        RequirePositive(dpiY, nameof(dpiY));
        return new Rect(
            pixels.Left * DipDpi / dpiX,
            pixels.Top * DipDpi / dpiY,
            pixels.Width * DipDpi / dpiX,
            pixels.Height * DipDpi / dpiY);
    }

    private static void RequirePositive(double value, string parameter)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameter);
        }
    }
}
