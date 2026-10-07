using System.Text.Json;
using ServiceNowDesk.Mapping;
using ServiceNowDesk.Models;
using ServiceNowDesk.Query;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Client;

public sealed partial class ServiceNowClient
{
    public Task<WorkEffortReport> GetWorkEffortAsync(WorkEffortScale scale, DateTime localNow, CancellationToken cancellationToken) =>
        GetWorkEffortAsync(scale, localNow, WorkEffortQuery.SafetyCap, cancellationToken);

    internal async Task<WorkEffortReport> GetWorkEffortAsync(
        WorkEffortScale scale,
        DateTime localNow,
        int safetyCap,
        CancellationToken cancellationToken)
    {
        var window = WorkEffortWindow.For(scale, localNow);
        var cap = Math.Max(1, safetyCap);
        var user = await GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        var signedIn = new WorkEffortPerson(
            user.SysId,
            string.IsNullOrWhiteSpace(user.Name) ? user.UserName : user.Name,
            user.UserName);
        var groups = await LoadWorkEffortGroupsAsync(signedIn.SysId, cancellationToken).ConfigureAwait(false);
        if (groups.Ids.Count == 0)
            return WorkEffortReport.NoGroups();

        var problems = new List<string>();
        if (groups.Truncated)
            problems.Add("Some of your groups were left out because the group list hit its cap.");

        var members = await LoadWorkEffortMembersAsync(groups.Ids, cancellationToken).ConfigureAwait(false);
        if (members.Truncated)
            problems.Add("Some people in your groups were left out because the member list hit its cap.");

        var memberships = new List<WorkEffortMembership>();
        foreach (var groupId in groups.Ids)
            memberships.Add(new WorkEffortMembership(groupId, signedIn));
        memberships.AddRange(members.Memberships);
        var people = WorkEffortRoster.Collect(memberships, signedIn);
        if (people.Count == 0)
            return WorkEffortReport.NoGroups();

        var touches = new List<WorkEffortTouch>();
        var truncated = false;
        var incident = await LoadWorkEffortTableAsync([WorkEffortTablePlan.Incident], people, window, cap, cancellationToken).ConfigureAwait(false);
        touches.AddRange(incident.Touches);
        truncated |= incident.Truncated;
        if (incident.Problem is not null)
            problems.Add(incident.Problem);

        var items = await LoadWorkEffortTableAsync(WorkEffortTablePlan.RequestedItemAttempts, people, window, cap, cancellationToken).ConfigureAwait(false);
        touches.AddRange(items.Touches);
        truncated |= items.Truncated;
        if (items.Problem is not null)
            problems.Add(items.Problem);

        var interactions = await LoadWorkEffortTableAsync(WorkEffortTablePlan.InteractionAttempts, people, window, cap, cancellationToken).ConfigureAwait(false);
        touches.AddRange(interactions.Touches);
        truncated |= interactions.Truncated;
        if (interactions.Problem is not null)
            problems.Add(interactions.Problem);

        var rows = WorkEffortScore.Build(people, touches, window);
        return new WorkEffortReport(rows, WorkEffortQuery.Status(scale, truncated, problems), "");
    }

    private async Task<(List<string> Ids, bool Truncated)> LoadWorkEffortGroupsAsync(string userSysId, CancellationToken cancellationToken)
    {
        var id = EncodedQuery.SafeToken(userSysId, "user id");
        var ids = new List<string>();
        var truncated = await PageRowsAsync(
            "sys_user_grmember",
            "group,user",
            "user=" + id + "^ORDERBYsys_id",
            200,
            null,
            row =>
            {
                var group = SnowField.Read(row, "group").Value.Trim();
                if (group.Length == 0 || ids.Any(existing => existing.Equals(group, StringComparison.OrdinalIgnoreCase)))
                    return;
                try
                {
                    ids.Add(EncodedQuery.SafeToken(group, "group id"));
                }
                catch (InvalidOperationException)
                {
                }
            },
            cancellationToken).ConfigureAwait(false);
        return (ids, truncated);
    }

    private async Task<(List<WorkEffortMembership> Memberships, bool Truncated)> LoadWorkEffortMembersAsync(
        IReadOnlyList<string> groupIds,
        CancellationToken cancellationToken)
    {
        var memberships = new List<WorkEffortMembership>();
        var truncated = false;
        const int groupChunk = 40;
        var remaining = 8000;
        for (var index = 0; index < groupIds.Count && remaining > 0; index += groupChunk)
        {
            var chunk = groupIds.Skip(index).Take(groupChunk).ToArray();
            var before = memberships.Count;
            var hit = await PageRowsAsync(
                "sys_user_grmember",
                "group,user,user.name,user.user_name",
                "groupIN" + string.Join(",", chunk) + "^ORDERBYsys_id",
                remaining,
                null,
                row =>
                {
                    var membership = ReadMember(row);
                    if (membership is WorkEffortMembership value)
                        memberships.Add(value);
                },
                cancellationToken).ConfigureAwait(false);
            remaining -= Math.Max(0, memberships.Count - before);
            if (hit)
                truncated = true;
        }

        return (memberships, truncated);
    }

    private static WorkEffortMembership? ReadMember(JsonElement row)
    {
        var group = SnowField.Read(row, "group").Value.Trim();
        var user = SnowField.Read(row, "user");
        var userId = user.Value.Trim();
        if (group.Length == 0 || userId.Length == 0)
            return null;
        try
        {
            group = EncodedQuery.SafeToken(group, "group id");
            userId = EncodedQuery.SafeToken(userId, "user id");
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var name = SnowField.Read(row, "user.name");
        var login = SnowField.Read(row, "user.user_name");
        var display = name.Display.Trim();
        if (display.Length == 0 || display.Equals(userId, StringComparison.OrdinalIgnoreCase))
            display = login.Display.Trim();
        if (display.Length == 0)
            display = user.Display.Trim();
        var userName = login.Value.Trim();
        if (userName.Length == 0)
            userName = login.Display.Trim();
        return new WorkEffortMembership(group, new WorkEffortPerson(userId, display, userName));
    }

    /// <summary>
    /// Aggregate group-by can count opens or resolves, but an update is dropped when the same
    /// person opened, closed, or resolved that record, and a close counts only when they did
    /// not also resolve it. Those rules need the user and date columns on each record, so this
    /// pages that narrow list instead of full ticket bodies.
    /// </summary>
    private async Task<(List<WorkEffortTouch> Touches, bool Truncated, string? Problem)> LoadWorkEffortTableAsync(
        IReadOnlyList<WorkEffortTablePlan> plans,
        IReadOnlyList<WorkEffortPerson> people,
        WorkEffortWindow window,
        int safetyCap,
        CancellationToken cancellationToken)
    {
        ServiceNowException? rejected = null;
        for (var index = 0; index < plans.Count; index++)
        {
            var plan = plans[index];
            try
            {
                var loaded = await LoadWorkEffortPlanAsync(plan, people, window, safetyCap, cancellationToken).ConfigureAwait(false);
                return (loaded.Touches, loaded.Truncated, null);
            }
            catch (ServiceNowException ex) when (ex.StatusCode == 400 && index < plans.Count - 1)
            {
                rejected = ex;
            }
            catch (ServiceNowException ex)
            {
                rejected = ex;
                break;
            }
        }

        var label = plans.Count == 0 ? "Records" : TableLabel(plans[0].Kind);
        var detail = rejected is null ? "" : " " + rejected.Message;
        return ([], false, label + " could not be counted." + detail);
    }

    private async Task<(List<WorkEffortTouch> Touches, bool Truncated)> LoadWorkEffortPlanAsync(
        WorkEffortTablePlan plan,
        IReadOnlyList<WorkEffortPerson> people,
        WorkEffortWindow window,
        int safetyCap,
        CancellationToken cancellationToken)
    {
        var touches = new List<WorkEffortTouch>();
        var truncated = false;
        foreach (var chunk in WorkEffortQuery.Chunks(people))
        {
            if (touches.Count >= safetyCap)
            {
                truncated = true;
                break;
            }

            var room = safetyCap - touches.Count;
            var clause = WorkEffortQuery.Clause(plan, chunk, window);
            var hit = await PageRowsAsync(
                plan.Table,
                plan.Fields,
                clause,
                room,
                null,
                row =>
                {
                    var touch = ReadTouch(row, plan.Kind);
                    if (touch is not null)
                        touches.Add(touch);
                },
                cancellationToken).ConfigureAwait(false);
            if (hit)
                truncated = true;
        }

        return (touches, truncated);
    }

    private static WorkEffortTouch? ReadTouch(JsonElement row, WorkEffortKind kind)
    {
        var sysId = SnowField.Read(row, "sys_id").Value.Trim();
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
