using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.Oracle;

internal static class Clock
{
    private static readonly long Origin = Stopwatch.GetTimestamp();

    /// <summary>Seconds since the mod loaded, unaffected by game speed and pause.</summary>
    public static double Now => Stopwatch.GetElapsedTime(Origin).TotalSeconds;
}

/// <summary>A repeating interval measured in real time.</summary>
internal struct Every
{
    private double _due;

    public bool Elapsed(double seconds)
    {
        double now = Clock.Now;
        if (now < _due)
            return false;

        _due = now + seconds;
        return true;
    }

    public void Force() => _due = 0.0;
}

/// <summary>Real time between calls, clamped so a stall never becomes one huge step.</summary>
internal struct FrameTimer
{
    private long _last;

    public float Step(float max)
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _last == 0 ? 0f : Math.Min((float)Stopwatch.GetElapsedTime(_last, now).TotalSeconds, max);
        _last = now;
        return dt;
    }
}

internal static class Ease
{
    /// <summary>How much of a gap to close this step to approach a target at a steady rate, whatever the frame rate.</summary>
    public static float Follow(float rate, float dt) => 1f - MathF.Exp(-rate * dt);

    public static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public static float In(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t;
    }

    public static float Out(float t) => 1f - MathF.Pow(1f - Math.Clamp(t, 0f, 1f), 3f);
}
