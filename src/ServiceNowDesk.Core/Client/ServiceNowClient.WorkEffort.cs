using System.Text.Json;
using ServiceNowDesk.Mapping;
using ServiceNowDesk.Models;
using ServiceNowDesk.Query;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Client;

public sealed partial class ServiceNowClient
{
    public Task<WorkEffortReport> GetWorkEffortAsync(
        WorkEffortScale scale,
        DateTime localNow,
        IReadOnlyList<WorkEffortPerson> team,
        CancellationToken cancellationToken) =>
        GetWorkEffortAsync(scale, localNow, team, progress: null, cancellationToken);

    public Task<WorkEffortReport> GetWorkEffortAsync(
        WorkEffortScale scale,
        DateTime localNow,
        IReadOnlyList<WorkEffortPerson> team,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken) =>
        LoadWorkEffortAsync(scale, localNow, team, WorkEffortQuery.SafetyCap, progress, cancellationToken);

    internal Task<WorkEffortReport> GetWorkEffortAsync(
        WorkEffortScale scale,
        DateTime localNow,
        int safetyCap,
        CancellationToken cancellationToken,
        IProgress<WorkEffortProgress>? progress = null,
        IReadOnlyList<WorkEffortPerson>? team = null) =>
        LoadWorkEffortAsync(scale, localNow, team, safetyCap, progress, cancellationToken);

    private async Task<WorkEffortReport> LoadWorkEffortAsync(
        WorkEffortScale scale,
        DateTime localNow,
        IReadOnlyList<WorkEffortPerson>? team,
        int safetyCap,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        var people = WorkEffortTeam.Normalize(team);
        if (people.Count == 0)
            return WorkEffortReport.NoTeam();

        var window = WorkEffortWindow.For(scale, localNow);
        var cap = Math.Max(1, safetyCap);
        var problems = new List<string>();
        var resolved = await ResolveTeamLoginsAsync(people, cancellationToken).ConfigureAwait(false);
        people = resolved.People;
        if (resolved.Problem is not null)
            problems.Add(resolved.Problem);
        if (!WorkEffortQuery.HasScope(people))
            return WorkEffortReport.NoTeam();

        var pace = new WorkEffortPace();
        var totals = new int[people.Count * WorkEffortAttempt.Width];
        var truncated = false;
        progress?.Report(pace.Snapshot());

        var incident = await LoadWorkEffortTableAsync(WorkEffortTablePlan.IncidentPlans, people, window, cap, totals, pace, progress, cancellationToken).ConfigureAwait(false);
        truncated |= incident.Truncated;
        if (incident.Problem is not null)
            problems.Add(incident.Problem);

        var items = await LoadWorkEffortTableAsync(WorkEffortTablePlan.RequestedItemAttempts, people, window, cap, totals, pace, progress, cancellationToken).ConfigureAwait(false);
        truncated |= items.Truncated;
        if (items.Problem is not null)
            problems.Add(items.Problem);

        var interactions = await LoadWorkEffortTableAsync(WorkEffortTablePlan.InteractionAttempts, people, window, cap, totals, pace, progress, cancellationToken).ConfigureAwait(false);
        truncated |= interactions.Truncated;
        if (interactions.Problem is not null)
            problems.Add(interactions.Problem);

        var rows = WorkEffortAttempt.ToRows(people, totals);
        return new WorkEffortReport(rows, WorkEffortQuery.Status(scale, truncated, problems), "");
    }

    /// <summary>
    /// One narrow user lookup for the ticked team when a sign-in name is missing.
    /// Opens and resolves match on sys_id. Updates match on the sign-in name.
    /// </summary>
    private async Task<(IReadOnlyList<WorkEffortPerson> People, string? Problem)> ResolveTeamLoginsAsync(
        IReadOnlyList<WorkEffortPerson> people,
        CancellationToken cancellationToken)
    {
        if (people.All(person => person.UserName.Length > 0))
            return (people, null);

        var ids = new List<string>();
        foreach (var person in people)
        {
            if (person.UserName.Length > 0)
                continue;
            try
            {
                var token = EncodedQuery.SafeToken(person.SysId, "user id");
                if (!ids.Any(existing => existing.Equals(token, StringComparison.OrdinalIgnoreCase)))
                    ids.Add(token);
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (ids.Count == 0)
            return (people, null);

        var found = new Dictionary<string, (string Name, string User)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var index = 0; index < ids.Count; index += WorkEffortQuery.ChunkSize)
            {
                var chunk = ids.Skip(index).Take(WorkEffortQuery.ChunkSize).ToArray();
                var result = await GetListAsync(
                    "sys_user",
                    "sys_id,name,user_name",
                    "sys_idIN" + string.Join(",", chunk),
                    chunk.Length,
                    0,
                    cancellationToken).ConfigureAwait(false);
                using (result)
                using (new WorkEffortQuietScope())
                {
                    foreach (var row in RequireArray(result.Document).EnumerateArray())
                    {
                        var id = SnowField.Read(row, "sys_id").Value.Trim();
                        if (id.Length == 0)
                            id = SnowField.Read(row, "sys_id").Display.Trim();
                        if (id.Length == 0)
                            continue;
                        var name = SnowField.Read(row, "name");
                        var login = SnowField.Read(row, "user_name");
                        var display = name.Display.Trim();
                        if (display.Length == 0)
                            display = name.Value.Trim();
                        var user = login.Value.Trim();
                        if (user.Length == 0)
                            user = login.Display.Trim();
                        found[id] = (display, user);
                    }
                }
            }
        }
        catch (ServiceNowException)
        {
            return (people, "Sign-in names could not be loaded, so some updates may be missing.");
        }

        var merged = new List<WorkEffortPerson>(people.Count);
        foreach (var person in people)
        {
            if (!found.TryGetValue(person.SysId, out var known))
            {
                merged.Add(person);
                continue;
            }

            var name = person.Name.Length > 0 && !person.Name.Equals(person.SysId, StringComparison.OrdinalIgnoreCase)
                ? person.Name
                : known.Name.Length > 0 ? known.Name : person.Name;
            var user = person.UserName.Length > 0 ? person.UserName : known.User;
            merged.Add(new WorkEffortPerson(person.SysId, name, user));
        }

        return (merged, null);
    }

    private async Task<(bool Truncated, string? Problem)> LoadWorkEffortTableAsync(
        IReadOnlyList<WorkEffortTablePlan> plans,
        IReadOnlyList<WorkEffortPerson> people,
        WorkEffortWindow window,
        int safetyCap,
        int[] totals,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        ServiceNowException? rejected = null;
        for (var index = 0; index < plans.Count; index++)
        {
            var attempt = new WorkEffortAttempt(people, window, safetyCap);
            pace.BeginTable();
            try
            {
                var truncated = await LoadWorkEffortPlanAsync(plans[index], people, window, attempt, pace, progress, cancellationToken).ConfigureAwait(false);
                attempt.FoldInto(totals);
                pace.CompleteTable();
                progress?.Report(pace.Snapshot());
                return (truncated || attempt.Truncated, null);
            }
            catch (ServiceNowException ex) when (ex.StatusCode == 400 && index < plans.Count - 1)
            {
                pace.CancelTable();
                rejected = ex;
            }
            catch (ServiceNowException ex)
            {
                pace.CancelTable();
                rejected = ex;
                break;
            }
        }

        pace.CompleteTable();
        progress?.Report(pace.Snapshot());
        var label = plans.Count == 0 ? "Records" : TableLabel(plans[0].Kind);
        var detail = rejected is null ? "" : " " + rejected.Message;
        return (false, label + " could not be counted." + detail);
    }

    private async Task<bool> LoadWorkEffortPlanAsync(
        WorkEffortTablePlan plan,
        IReadOnlyList<WorkEffortPerson> people,
        WorkEffortWindow window,
        WorkEffortAttempt attempt,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        var truncated = false;
        foreach (var chunk in WorkEffortQuery.Chunks(people))
        {
            if (!attempt.WantsMore)
            {
                truncated = true;
                break;
            }

            var clause = WorkEffortQuery.Clause(plan, chunk, window);
            if (WorkEffortQuery.IsUnscoped(clause))
                continue;

            var hit = await PageWorkEffortAsync(plan, clause, attempt, pace, progress, cancellationToken).ConfigureAwait(false);
            truncated |= hit;
            if (attempt.Truncated)
                truncated = true;
        }

        return truncated;
    }

    /// <summary>
    /// One page at a time, one request at a time. Counts are folded into the attempt
    /// and the JSON document is dropped before the next page is asked for.
    /// display_value=all stays on because opened_by has to be the sys_id and the
    /// date has to be the local display time. The field list is only those columns.
    /// </summary>
    private async Task<bool> PageWorkEffortAsync(
        WorkEffortTablePlan plan,
        string query,
        WorkEffortAttempt attempt,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        var received = 0;
        int? total = null;
        var truncated = false;
        while (attempt.WantsMore)
        {
            var limit = Math.Min(WorkEffortQuery.PageSize, Math.Max(1, attempt.Room));
            var result = await GetListAsync(plan.Table, plan.Fields, query, limit, offset, cancellationToken).ConfigureAwait(false);
            var stop = false;
            using (result)
            using (new WorkEffortQuietScope())
            {
                var rows = RequireArray(result.Document);
                var count = rows.GetArrayLength();
                var kept = 0;
                foreach (var row in rows.EnumerateArray())
                {
                    var touch = ReadTouch(row, plan.Kind);
                    if (!attempt.TakeRow(touch))
                    {
                        truncated = true;
                        stop = true;
                        break;
                    }

                    kept++;
                }

                received += count;
                if (result.TotalCount is int reported)
                    total = reported;
                pace.AddPage(kept, total);
                progress?.Report(pace.Snapshot());

                var hasNext = !string.IsNullOrWhiteSpace(result.NextLink);
                var linkOffset = TryReadOffset(result.NextLink);
                var more = total is int expected && received < expected;
                if (stop || count == 0)
                {
                    truncated |= more || hasNext;
                    break;
                }

                if (total is int done && received >= done)
                    break;

                if (!more && !hasNext && count < limit)
                    break;

                var step = offset + count;
                var nextOffset = step;
                if (linkOffset > offset && (count >= limit || linkOffset <= step))
                    nextOffset = linkOffset;

                if (!attempt.WantsMore)
                {
                    truncated |= more || hasNext;
                    break;
                }

                if (nextOffset <= offset)
                {
                    truncated |= more || hasNext || count >= limit;
                    break;
                }

                offset = nextOffset;
            }
        }

        if (!attempt.WantsMore && total is int expectedTotal && received < expectedTotal)
            truncated = true;
        return truncated;
    }

    private static WorkEffortTouch? ReadTouch(JsonElement row, WorkEffortKind kind)
    {
        var sysId = SnowField.Read(row, "sys_id").Value.Trim();
        if (sysId.Length == 0)
            sysId = SnowField.Read(row, "sys_id").Display.Trim();
        if (sysId.Length == 0)
            return null;
        var openedBy = SnowField.Read(row, "opened_by").Value.Trim();
        if (openedBy.Length == 0)
            openedBy = SnowField.Read(row, "opened_for").Value.Trim();
        var updated = SnowField.Read(row, "sys_updated_by");
        var updatedBy = updated.Value.Trim();
        if (updatedBy.Length == 0)
            updatedBy = updated.Display.Trim();
        return new WorkEffortTouch(
            sysId,
            kind,
            NullIfEmpty(openedBy),
            ReadMoment(row, "opened_at"),
            NullIfEmpty(SnowField.Read(row, "resolved_by").Value),
            ReadMoment(row, "resolved_at"),
            NullIfEmpty(SnowField.Read(row, "closed_by").Value),
            ReadMoment(row, "closed_at"),
            NullIfEmpty(updatedBy),
            ReadMoment(row, "sys_updated_on"));
    }

    private static DateTime? ReadMoment(JsonElement row, string name)
    {
        var field = SnowField.Read(row, name);
        if (field.Display.Length > 0 && WorkEffortClock.TryParse(field.Display, out var display))
            return display;
        if (field.Value.Length > 0 && WorkEffortClock.TryParse(field.Value, out var value))
            return value;
        return null;
    }

    private static string? NullIfEmpty(string? value)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string TableLabel(WorkEffortKind kind) => kind switch
    {
        WorkEffortKind.RequestedItem => "Request items",
        WorkEffortKind.Interaction => "Walk-ups",
        _ => "Incidents"
    };
}
