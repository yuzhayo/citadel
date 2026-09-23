namespace Module.Mangareader;

/// <summary>
/// Pure momentum (inertia) math for wheel scrolling, giving the coast-and-settle
/// feel of touch/game scrolling instead of a fixed per-notch step. A wheel notch
/// injects a velocity impulse; each frame the velocity is integrated into the
/// offset and then decays exponentially (friction). Frame-rate independent and
/// free of WPF/timers so the glide is testable in isolation.
/// </summary>
public static class ReaderMomentumScrollPolicy
{
    /// <summary>Friction time constant in seconds. Larger = a longer, floatier coast.</summary>
    public const double FrictionSeconds = 0.25;

    /// <summary>Total-distance multiplier per notch. 1.0 coasts exactly the notch distance.</summary>
    public const double MomentumGain = 1.5;

    /// <summary>Below this speed (DIP/s) the coast is considered stopped.</summary>
    public const double StopVelocity = 5;

    /// <summary>Hard speed cap (DIP/s) so a fast flick cannot fly off.</summary>
    public const double MaxVelocity = 9000;

    /// <summary>
    /// Velocity low-pass time constant (s). The actual velocity eases toward the
    /// momentum target over this window, so a lone notch ramps in instead of kicking.
    /// Keep this small: a large value makes the page trail the input ("dragged").
    /// </summary>
    public const double ResponseSeconds = 0.012;

    /// <summary>
    /// Signed velocity impulse (DIP/s) for a notch that should coast about
    /// <paramref name="notchDistance"/> DIP (times <see cref="MomentumGain"/>).
    /// Under exponential friction the total travel of v0 is v0 * FrictionSeconds,
    /// so v0 = distance / FrictionSeconds * gain.
    /// </summary>
    public static double ImpulseForDistance(double notchDistance)
    {
        if (!double.IsFinite(notchDistance)) return 0;
        return ClampVelocity((notchDistance / FrictionSeconds) * MomentumGain);
    }

    /// <summary>Offset delta for one frame at the current velocity.</summary>
    public static double DistanceForFrame(double velocity, double elapsedSeconds)
    {
        if (!double.IsFinite(velocity)) return 0;
        var seconds = double.IsFinite(elapsedSeconds) ? Math.Max(0, elapsedSeconds) : 0;
        return velocity * seconds;
    }

    /// <summary>Velocity after one frame of friction; snaps to 0 below the stop threshold.</summary>
    public static double DecayVelocity(double velocity, double elapsedSeconds)
    {
        if (!double.IsFinite(velocity)) return 0;
        var seconds = double.IsFinite(elapsedSeconds) ? Math.Max(0, elapsedSeconds) : 0;
        var decayed = velocity * Math.Exp(-seconds / FrictionSeconds);
        return Math.Abs(decayed) < StopVelocity ? 0 : decayed;
    }

    /// <summary>
    /// Eases <paramref name="current"/> velocity toward <paramref name="target"/> with
    /// frame-rate-independent exponential smoothing (a first-order low-pass). Keeps
    /// acceleration continuous so motion has no per-notch discontinuity; the unity DC
    /// gain preserves total travel distance.
    /// </summary>
    public static double ApproachVelocity(double current, double target, double elapsedSeconds)
    {
        if (!double.IsFinite(target)) return double.IsFinite(current) ? current : 0;
        if (!double.IsFinite(current)) return target;
        var seconds = double.IsFinite(elapsedSeconds) ? Math.Max(0, elapsedSeconds) : 0;
        if (seconds <= 0) return current;
        var alpha = 1 - Math.Exp(-seconds / ResponseSeconds);
        return current + ((target - current) * alpha);
    }

    public static double ClampVelocity(double velocity) =>
        double.IsFinite(velocity) ? Math.Clamp(velocity, -MaxVelocity, MaxVelocity) : 0;

    public static bool ShouldStop(double velocity) =>
        !double.IsFinite(velocity) || Math.Abs(velocity) < StopVelocity;
}
