using System;

namespace Clippboard.Core;

public static class MotionMath
{
    public static double Settle(double progress, bool bounce)
    {
        var t = Math.Clamp(progress, 0, 1);
        if (t is 0 or 1) return t;
        double Curve(double x) => bounce
            ? 1 - Math.Exp(-8 * x) * (Math.Cos(13 * x) + 8.0 / 13 * Math.Sin(13 * x))
            : 1 - Math.Exp(-9 * x) * (1 + 9 * x);
        return Curve(t) / Curve(1);
    }

    public static double RopeHeight(double x, double width, double sag)
    {
        var t = Math.Clamp(x / Math.Max(1, width), 0, 1);
        return 20 + 4 * sag * t * (1 - t);
    }
    public static double Sway(double progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        if (t is 0 or 1) return t;
        // Continuous damped oscillator: no velocity resets at hand-authored keyframes.
        static double Curve(double x) => 1 - Math.Exp(-4.8 * x) * (Math.Cos(13 * x) + 4.8 / 13 * Math.Sin(13 * x));
        return Curve(t) / Curve(1);
    }

    public static (int First, int Last) VisibleRange(int count, double offset, double viewport, double pitch)
    {
        if (count <= 0) return (0, -1);
        pitch = Math.Max(1, pitch);
        var first = Math.Clamp((int)Math.Floor(Math.Max(0, offset) / pitch) - 1, 0, count - 1);
        var last = Math.Clamp((int)Math.Ceiling((Math.Max(0, offset) + Math.Max(1, viewport)) / pitch) + 1, first, count - 1);
        return (first, last);
    }
}
