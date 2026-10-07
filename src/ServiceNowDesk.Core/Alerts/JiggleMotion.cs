namespace ServiceNowDesk.Alerts;

/// <summary>
/// How fast the desktop strip shakes while a jiggle is open.
/// One move is one full sine cycle, the same cycle <c>AlertWidgetWindow</c> already draws:
/// the strip travels to one side, through the centre, to the other side, and back to centre.
/// The previous animation drew six of those cycles across the default two-second jiggle,
/// which is three moves per second.
/// </summary>
public static class JiggleMotion
{
    public const double MinimumMovesPerSecond = 1;
    public const double MaximumMovesPerSecond = 10;
    public const double Step = 0.5;

    /// <summary>
    /// Six sine cycles over the default two-second jiggle.
    /// </summary>
    public const double DefaultMovesPerSecond = 3;

    public static double Snap(double movesPerSecond)
    {
        if (double.IsNaN(movesPerSecond) || double.IsInfinity(movesPerSecond))
            return DefaultMovesPerSecond;

        var steps = Math.Round(movesPerSecond / Step, MidpointRounding.AwayFromZero);
        var snapped = steps * Step;
        if (snapped < MinimumMovesPerSecond)
            return MinimumMovesPerSecond;
        if (snapped > MaximumMovesPerSecond)
            return MaximumMovesPerSecond;
        return snapped;
    }

    /// <summary>
    /// Time between moves. Two moves per second is 500 milliseconds.
    /// </summary>
    public static TimeSpan MoveInterval(double movesPerSecond)
    {
        var speed = Snap(movesPerSecond);
        return TimeSpan.FromSeconds(1d / speed);
    }

    /// <summary>
    /// How many sine cycles fit in <paramref name="duration"/> at this speed.
    /// The cycle period is <see cref="MoveInterval"/>.
    /// </summary>
    public static double SineCycles(double movesPerSecond, TimeSpan duration)
    {
        var speed = Snap(movesPerSecond);
        var seconds = duration.TotalSeconds;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
            seconds = 2;
        return speed * seconds;
    }

    public static JiggleMotionPlan Plan(double movesPerSecond, TimeSpan duration)
    {
        var speed = Snap(movesPerSecond);
        return new JiggleMotionPlan(speed, MoveInterval(speed), SineCycles(speed, duration));
    }

    /// <summary>
    /// Horizontal offset, in pixels, at <paramref name="progress"/> (0 at the start of the jiggle, 1 at the end).
    /// The previous strip used eight keyframes per cycle and an amplitude of 6.
    /// </summary>
    public static double Offset(double movesPerSecond, TimeSpan duration, double progress, double amplitude = 6)
    {
        var cycles = SineCycles(movesPerSecond, duration);
        if (double.IsNaN(progress) || double.IsInfinity(progress))
            progress = 0;
        return Math.Sin(progress * cycles * Math.PI * 2) * amplitude;
    }

    public static int KeyframeCount(double sineCycles)
    {
        if (double.IsNaN(sineCycles) || double.IsInfinity(sineCycles) || sineCycles <= 0)
            return 8;
        return Math.Max(1, (int)Math.Round(sineCycles * 8, MidpointRounding.AwayFromZero));
    }
}

public readonly record struct JiggleMotionPlan(double MovesPerSecond, TimeSpan MoveInterval, double SineCycles);
