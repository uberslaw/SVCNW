using System.Diagnostics;
using System.Globalization;

namespace ServiceNowDesk.WorkEffort;

/// <summary>
/// Minute estimate for a Work Effort run. While a rate is not known yet the text
/// says 1 minute. After that, minutes are the remaining rows divided by the measured
/// rows per second, rounded up.
/// </summary>
public static class WorkEffortEstimate
{
    public const int BarMaximum = 300;
    public const int TableCount = 3;
    public const double FormingSeconds = 0.5;

    public static string Text(int minutes)
    {
        var shown = minutes < 1 ? 1 : minutes;
        return "Generating report. check back in: " + shown.ToString(CultureInfo.InvariantCulture) + " minutes";
    }

    public static int Minutes(double elapsedSeconds, int rowsSeen, double rowsRemaining)
    {
        if (elapsedSeconds < FormingSeconds || rowsSeen < 1 || double.IsNaN(rowsRemaining) || rowsRemaining <= 0)
            return 1;
        var secondsLeft = rowsRemaining / (rowsSeen / elapsedSeconds);
        if (double.IsNaN(secondsLeft) || secondsLeft <= 0)
            return 1;
        var minutes = (int)Math.Ceiling(secondsLeft / 60.0);
        return minutes < 1 ? 1 : minutes;
    }

    /// <summary>
    /// Rows still to read: what is left on the open table, plus a full table for each
    /// table not started. Finished tables supply the average. Before any table finishes,
    /// the current total (or the rows already seen) stands in for the ones not started.
    /// </summary>
    public static double RowsRemaining(bool tableOpen, int? currentTotal, int currentRows, int tablesAfter, double averageFinished)
    {
        double leftThis = 0;
        if (tableOpen)
        {
            if (currentTotal is int total)
                leftThis = Math.Max(0, total - currentRows);
            else if (currentRows <= 0)
                leftThis = averageFinished > 0 ? averageFinished : WorkEffortQuery.PageSize;
            else
                leftThis = currentRows;
        }

        var perTable = averageFinished > 0
            ? averageFinished
            : currentTotal ?? (currentRows > 0 ? currentRows : (double)WorkEffortQuery.PageSize);
        if (perTable < 1)
            perTable = 1;
        return leftThis + perTable * Math.Max(0, tablesAfter);
    }

    public static int Bar(int tablesDone, int tableCount, bool tableOpen, int currentRows, int? currentTotal)
    {
        if (tableCount < 1)
            return 0;
        var slice = BarMaximum / tableCount;
        var value = Math.Max(0, tablesDone) * slice;
        if (tableOpen)
        {
            double fraction;
            if (currentTotal is int total && total > 0)
                fraction = Math.Clamp((double)currentRows / total, 0, 0.99);
            else if (currentRows > 0)
                fraction = Math.Min(0.9, currentRows / (double)(currentRows + WorkEffortQuery.PageSize));
            else
                fraction = 0.02;
            value += (int)(fraction * slice);
        }

        if (value < 0)
            return 0;
        if (value > BarMaximum)
            return BarMaximum;
        return value;
    }
}

public sealed class WorkEffortPace
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _committedRows;
    private int _tableRows;
    private int? _tableTotal;
    private int _tablesDone;
    private bool _tableOpen;

    public void BeginTable()
    {
        _tableRows = 0;
        _tableTotal = null;
        _tableOpen = true;
    }

    public void AddPage(int rows, int? total)
    {
        if (rows > 0)
            _tableRows += rows;
        if (total is int value)
            _tableTotal = value;
    }

    public void CompleteTable()
    {
        _committedRows += _tableRows;
        _tablesDone++;
        _tableRows = 0;
        _tableTotal = null;
        _tableOpen = false;
    }

    public void CancelTable()
    {
        _tableRows = 0;
        _tableTotal = null;
        _tableOpen = false;
    }

    public WorkEffortProgress Snapshot()
    {
        var seen = _committedRows + _tableRows;
        var average = _tablesDone > 0 ? (double)_committedRows / _tablesDone : 0;
        var after = Math.Max(0, WorkEffortEstimate.TableCount - _tablesDone - (_tableOpen ? 1 : 0));
        var remaining = WorkEffortEstimate.RowsRemaining(_tableOpen, _tableTotal, _tableRows, after, average);
        var minutes = WorkEffortEstimate.Minutes(_watch.Elapsed.TotalSeconds, seen, remaining);
        var bar = WorkEffortEstimate.Bar(_tablesDone, WorkEffortEstimate.TableCount, _tableOpen, _tableRows, _tableTotal);
        return new WorkEffortProgress(bar, WorkEffortEstimate.BarMaximum, WorkEffortEstimate.Text(minutes));
    }
}

internal static class WorkEffortQuiet
{
    public static void Leave(ThreadPriority previous)
    {
        try
        {
            if (Thread.CurrentThread.Priority != previous)
                Thread.CurrentThread.Priority = previous;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or ThreadStateException or InvalidOperationException)
        {
        }
    }
}

public readonly struct WorkEffortQuietScope : IDisposable
{
    private readonly ThreadPriority _previous;
    private readonly bool _restore;

    public WorkEffortQuietScope()
    {
        _previous = ThreadPriority.Normal;
        _restore = false;
        try
        {
            _previous = Thread.CurrentThread.Priority;
            if (_previous != ThreadPriority.BelowNormal)
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                _restore = true;
            }
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or ThreadStateException or InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (!_restore)
            return;
        WorkEffortQuiet.Leave(_previous);
    }
}
