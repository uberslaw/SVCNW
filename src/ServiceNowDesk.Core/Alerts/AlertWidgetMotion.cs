namespace ServiceNowDesk.Alerts;

/// <summary>
/// Rest shows the thin indicator. The bar is open while the pointer is over it,
/// and while a scheduled jiggle is holding it open.
/// </summary>
public sealed class AlertWidgetMotion
{
    public bool PointerOver { get; private set; }

    public bool TimerDrop { get; private set; }

    public bool BarOpen => PointerOver || TimerDrop;

    public bool IndicatorAtRest => !BarOpen;

    public void SetPointerOver(bool over) => PointerOver = over;

    public void SetTimerDrop(bool dropped) => TimerDrop = dropped;
}

/// <summary>
/// Arms the jiggle interval once. A later poll that reports the same interval does not restart the wait.
/// </summary>
public sealed class AlertJiggleSchedule
{
    public TimeSpan Interval { get; private set; } = TimeSpan.FromMinutes(1);

    public bool IsRunning { get; private set; }

    public int ArmCount { get; private set; }

    /// <summary>
    /// Returns true when the timer must be started or retargeted.
    /// Returns false when it is already running at this interval, so the elapsed wait is kept.
    /// </summary>
    public bool Arm(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            interval = TimeSpan.FromMinutes(1);
        if (IsRunning && Interval == interval)
            return false;

        Interval = interval;
        IsRunning = true;
        ArmCount++;
        return true;
    }

    public void Stop() => IsRunning = false;
}
