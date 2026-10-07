namespace ServiceNowDesk.WorkEffort;

public static class WorkEffortEngine
{
    public static Task<WorkEffortReport> Start(
        IReadOnlyList<WorkEffortPerson> team,
        WorkEffortWindow window,
        Func<int, IEnumerable<WorkEffortTouch>> tables,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken,
        int safetyCap = WorkEffortQuery.SafetyCap)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var people = WorkEffortTeam.Normalize(team);
        var cap = safetyCap < 1 ? 1 : safetyCap;
        return Task.Run(() => Run(people, window, tables, progress, cancellationToken, cap), cancellationToken);
    }

    private static WorkEffortReport Run(
        IReadOnlyList<WorkEffortPerson> people,
        WorkEffortWindow window,
        Func<int, IEnumerable<WorkEffortTouch>> tables,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken,
        int safetyCap)
    {
        if (people.Count == 0)
            return WorkEffortReport.NoTeam();

        using var quiet = new WorkEffortQuietScope();
        var pace = new WorkEffortPace();
        var totals = new int[people.Count * WorkEffortAttempt.Width];
        var truncated = false;
        progress?.Report(pace.Snapshot());
        for (var table = 0; table < WorkEffortEstimate.TableCount; table++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pace.BeginTable();
            var attempt = new WorkEffortAttempt(people, window, safetyCap);
            var noted = 0;
            foreach (var touch in tables(table))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!attempt.TakeRow(touch))
                {
                    truncated = true;
                    break;
                }

                if (attempt.Received - noted >= 256)
                {
                    pace.AddPage(attempt.Received - noted, null);
                    noted = attempt.Received;
                    progress?.Report(pace.Snapshot());
                }
            }

            var tail = attempt.Received - noted;
            if (tail > 0)
                pace.AddPage(tail, null);
            truncated |= attempt.Truncated;
            attempt.FoldInto(totals);
            pace.CompleteTable();
            progress?.Report(pace.Snapshot());
        }

        var rows = WorkEffortAttempt.ToRows(people, totals);
        return new WorkEffortReport(rows, WorkEffortQuery.Status(window.Scale, truncated, null), "");
    }
}
