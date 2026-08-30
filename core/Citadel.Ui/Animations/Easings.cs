namespace Citadel.Ui.Animations;

/// <summary>
/// The current consumers need exact linear interpolation and the sidebar's
/// ease-out. More curves stay out until a real control needs one; importing
/// lib_ui's easing catalogue here would be feature creep rather than reuse.
/// </summary>
public static class Easings
{
    public static double Linear(double progress) => Math.Clamp(progress, 0, 1);

    public static double EaseOutCubic(double progress)
    {
        var remaining = 1 - Math.Clamp(progress, 0, 1);
        return 1 - (remaining * remaining * remaining);
    }
}
