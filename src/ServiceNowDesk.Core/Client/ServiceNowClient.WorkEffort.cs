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
        var batch = new WorkEffortBatch();
        var truncated = false;
        progress?.Report(pace.Snapshot());

        var incident = await LoadWorkEffortTableAsync(WorkEffortTablePlan.IncidentPlans, people, window, cap, batch, pace, progress, cancellationToken).ConfigureAwait(false);
        truncated |= incident.Truncated;
        if (incident.Problem is not null)
            problems.Add(incident.Problem);

        var items = await LoadWorkEffortTableAsync(WorkEffortTablePlan.RequestedItemAttempts, people, window, cap, batch, pace, progress, cancellationToken).ConfigureAwait(false);
        truncated |= items.Truncated;
        if (items.Problem is not null)
            problems.Add(items.Problem);

        var interactions = await LoadWorkEffortTableAsync(WorkEffortTablePlan.InteractionAttempts, people, window, cap, batch, pace, progress, cancellationToken).ConfigureAwait(false);
        truncated |= interactions.Truncated;
        if (interactions.Problem is not null)
            problems.Add(interactions.Problem);

        var history = await LoadWorkEffortHistoryAsync(people, window, cap, batch, pace, progress, cancellationToken).ConfigureAwait(false);
        truncated |= history.Truncated;
        if (history.Problem is not null)
            problems.Add(history.Problem);

        var touches = batch.Touches();
        var ledger = new WorkEffortLedger(people, touches, window);
        var rows = WorkEffortScore.Build(people, touches, window, WorkEffortUpdateMode.Daily);
        return new WorkEffortReport(rows, WorkEffortQuery.Status(scale, truncated, problems), "", ledger);
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
        WorkEffortBatch batch,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        ServiceNowException? rejected = null;
        for (var index = 0; index < plans.Count; index++)
        {
            var budget = new WorkEffortBudget(safetyCap);
            pace.BeginTable();
            try
            {
                var truncated = await LoadWorkEffortPlanAsync(plans[index], people, window, batch, budget, pace, progress, cancellationToken).ConfigureAwait(false);
                pace.CompleteTable();
                progress?.Report(pace.Snapshot());
                return (truncated || budget.Truncated, null);
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
        WorkEffortBatch batch,
        WorkEffortBudget budget,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        var truncated = false;
        foreach (var chunk in WorkEffortQuery.Chunks(people))
        {
            if (!budget.WantsMore)
            {
                truncated = true;
                break;
            }

            var clause = WorkEffortQuery.Clause(plan, chunk, window);
            if (WorkEffortQuery.IsUnscoped(clause))
                continue;

            var hit = await PageWorkEffortAsync(
                plan.Table,
                plan.Fields,
                clause,
                budget,
                row =>
                {
                    var touch = ReadTouch(row, plan.Kind);
                    if (touch is not null)
                        batch.Add(touch);
                },
                pace,
                progress,
                cancellationToken).ConfigureAwait(false);
            truncated |= hit;
            if (budget.Truncated)
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
        string table,
        string fields,
        string query,
        WorkEffortBudget budget,
        Action<JsonElement> accept,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        var received = 0;
        int? total = null;
        var truncated = false;
        while (budget.WantsMore)
        {
            var limit = Math.Min(WorkEffortQuery.PageSize, Math.Max(1, budget.Room));
            var result = await GetListAsync(table, fields, query, limit, offset, cancellationToken).ConfigureAwait(false);
            var stop = false;
            using (result)
            using (new WorkEffortQuietScope())
            {
                var rows = RequireArray(result.Document);
                var count = rows.GetArrayLength();
                var kept = 0;
                foreach (var row in rows.EnumerateArray())
                {
                    if (!budget.Take())
                    {
                        truncated = true;
                        stop = true;
                        break;
                    }

                    accept(row);
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

                if (!budget.WantsMore)
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

        if (!budget.WantsMore && total is int expectedTotal && received < expectedTotal)
            truncated = true;
        return truncated;
    }

    /// <summary>
    /// Journal rows (work notes and comments) plus audit rows when the instance allows them.
    /// The header update is only the latest save, so earlier days live here. A journal row and
    /// the header row at the same local second are one moment when the score runs.
    /// </summary>
    private async Task<(bool Truncated, string? Problem)> LoadWorkEffortHistoryAsync(
        IReadOnlyList<WorkEffortPerson> people,
        WorkEffortWindow window,
        int safetyCap,
        WorkEffortBatch batch,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        var truncated = false;
        string? problem = null;
        pace.BeginTable();
        try
        {
            truncated |= await LoadJournalAsync(people, window, safetyCap, batch, pace, progress, cancellationToken).ConfigureAwait(false);
            pace.CompleteTable();
            progress?.Report(pace.Snapshot());
        }
        catch (ServiceNowException)
        {
            pace.CancelTable();
            pace.CompleteTable();
            progress?.Report(pace.Snapshot());
            problem = WorkEffortQuery.HistoryNotice;
        }

        pace.BeginTable();
        try
        {
            truncated |= await LoadAuditAsync(people, window, safetyCap, batch, pace, progress, cancellationToken).ConfigureAwait(false);
            pace.CompleteTable();
            progress?.Report(pace.Snapshot());
        }
        catch (ServiceNowException)
        {
            pace.CancelTable();
            pace.CompleteTable();
            progress?.Report(pace.Snapshot());
        }

        return (truncated, problem);
    }

    private async Task<bool> LoadJournalAsync(
        IReadOnlyList<WorkEffortPerson> people,
        WorkEffortWindow window,
        int safetyCap,
        WorkEffortBatch batch,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        var budget = new WorkEffortBudget(safetyCap);
        var pending = new List<JournalHit>();
        var truncated = false;
        foreach (var chunk in WorkEffortQuery.Chunks(people))
        {
            if (!budget.WantsMore)
            {
                truncated = true;
                break;
            }

            var clause = WorkEffortQuery.JournalClause(chunk, window);
            if (WorkEffortQuery.IsUnscoped(clause))
                continue;

            truncated |= await PageWorkEffortAsync(
                WorkEffortQuery.JournalTable,
                WorkEffortQuery.JournalFields,
                clause,
                budget,
                row =>
                {
                    if (!TryReadJournal(row, window, out var hit))
                        return;
                    if (hit.Kind is WorkEffortKind known)
                        batch.AddUpdate(known, hit.RecordId, hit.By, hit.At);
                    else
                        pending.Add(hit);
                },
                pace,
                progress,
                cancellationToken).ConfigureAwait(false);
            if (budget.Truncated)
                truncated = true;
        }

        if (pending.Count > 0)
            await PlaceJournalAsync(pending, batch, cancellationToken).ConfigureAwait(false);
        return truncated || budget.Truncated;
    }

    private async Task PlaceJournalAsync(
        List<JournalHit> pending,
        WorkEffortBatch batch,
        CancellationToken cancellationToken)
    {
        var unknown = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in pending)
        {
            if (batch.KindOf(hit.RecordId) is not null)
                continue;
            if (seen.Add(hit.RecordId))
                unknown.Add(hit.RecordId);
        }

        var found = new Dictionary<string, WorkEffortKind>(StringComparer.OrdinalIgnoreCase);
        if (unknown.Count > 0)
        {
            var remaining = unknown;
            foreach (var table in new[] { "incident", "sc_req_item", "interaction" })
            {
                if (remaining.Count == 0)
                    break;
                var kind = table switch
                {
                    "sc_req_item" => WorkEffortKind.RequestedItem,
                    "interaction" => WorkEffortKind.Interaction,
                    _ => WorkEffortKind.Incident
                };
                var hits = await LookupRecordIdsAsync(table, remaining, cancellationToken).ConfigureAwait(false);
                var next = new List<string>();
                foreach (var id in remaining)
                {
                    if (hits.Contains(id))
                        found[id] = kind;
                    else
                        next.Add(id);
                }

                remaining = next;
            }
        }

        foreach (var hit in pending)
        {
            var kind = batch.KindOf(hit.RecordId);
            if (kind is null && found.TryGetValue(hit.RecordId, out var placed))
                kind = placed;
            if (kind is not WorkEffortKind known)
                continue;
            batch.AddUpdate(known, hit.RecordId, hit.By, hit.At);
        }
    }

    private async Task<HashSet<string>> LookupRecordIdsAsync(
        string table,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < ids.Count; index += WorkEffortQuery.ChunkSize)
        {
            var chunk = ids.Skip(index).Take(WorkEffortQuery.ChunkSize).ToArray();
            var clause = WorkEffortQuery.IdClause(chunk);
            if (WorkEffortQuery.IsUnscoped(clause))
                continue;
            try
            {
                var result = await GetListAsync(table, "sys_id", clause, chunk.Length, 0, cancellationToken).ConfigureAwait(false);
                using (result)
                using (new WorkEffortQuietScope())
                {
                    foreach (var row in RequireArray(result.Document).EnumerateArray())
                    {
                        var id = SnowField.Read(row, "sys_id").Value.Trim();
                        if (id.Length == 0)
                            id = SnowField.Read(row, "sys_id").Display.Trim();
                        if (id.Length > 0)
                            found.Add(id);
                    }
                }
            }
            catch (ServiceNowException ex) when (ex.StatusCode is 400 or 403 or 404)
            {
            }
        }

        return found;
    }

    private async Task<bool> LoadAuditAsync(
        IReadOnlyList<WorkEffortPerson> people,
        WorkEffortWindow window,
        int safetyCap,
        WorkEffortBatch batch,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        var budget = new WorkEffortBudget(safetyCap);
        var truncated = false;
        foreach (var chunk in WorkEffortQuery.Chunks(people))
        {
            if (!budget.WantsMore)
            {
                truncated = true;
                break;
            }

            var clause = WorkEffortQuery.AuditClause(chunk, window);
            if (WorkEffortQuery.IsUnscoped(clause))
                continue;

            truncated |= await PageWorkEffortAsync(
                WorkEffortQuery.AuditTable,
                WorkEffortQuery.AuditFields,
                clause,
                budget,
                row =>
                {
                    if (!TryReadAudit(row, window, out var kind, out var id, out var by, out var at))
                        return;
                    batch.AddUpdate(kind, id, by, at);
                },
                pace,
                progress,
                cancellationToken).ConfigureAwait(false);
            if (budget.Truncated)
                truncated = true;
        }

        return truncated || budget.Truncated;
    }

    private static bool TryReadJournal(JsonElement row, WorkEffortWindow window, out JournalHit hit)
    {
        hit = default;
        var element = SnowField.Read(row, "element").Value.Trim();
        if (element.Length == 0)
            element = SnowField.Read(row, "element").Display.Trim();
        if (!IsUpdateElement(element))
            return false;
        var id = SnowField.Read(row, "element_id").Value.Trim();
        if (id.Length == 0)
            id = SnowField.Read(row, "element_id").Display.Trim();
        var by = ReadAuthor(row, "sys_created_by");
        var at = ReadMoment(row, "sys_created_on");
        if (id.Length == 0 || by.Length == 0 || at is not DateTime moment || !window.Contains(moment))
            return false;
        var name = SnowField.Read(row, "name").Value.Trim();
        if (name.Length == 0)
            name = SnowField.Read(row, "name").Display.Trim();
        hit = new JournalHit(id, by, moment, KindFromTable(name));
        return true;
    }

    private static bool TryReadAudit(
        JsonElement row,
        WorkEffortWindow window,
        out WorkEffortKind kind,
        out string id,
        out string by,
        out DateTime at)
    {
        kind = default;
        id = "";
        by = "";
        at = default;
        var table = SnowField.Read(row, "tablename").Value.Trim();
        if (table.Length == 0)
            table = SnowField.Read(row, "tablename").Display.Trim();
        if (KindFromTable(table) is not WorkEffortKind known)
            return false;
        id = SnowField.Read(row, "documentkey").Value.Trim();
        if (id.Length == 0)
            id = SnowField.Read(row, "documentkey").Display.Trim();
        by = ReadAuthor(row, "user");
        var moment = ReadMoment(row, "sys_created_on");
        if (id.Length == 0 || by.Length == 0 || moment is not DateTime stamp || !window.Contains(stamp))
            return false;
        kind = known;
        at = stamp;
        return true;
    }

    private static bool IsUpdateElement(string element) =>
        element.Equals("comments", StringComparison.OrdinalIgnoreCase)
        || element.Equals("additional_comments", StringComparison.OrdinalIgnoreCase)
        || element.Equals("work_notes", StringComparison.OrdinalIgnoreCase);

    private static WorkEffortKind? KindFromTable(string? table)
    {
        var name = (table ?? "").Trim();
        if (name.Equals("incident", StringComparison.OrdinalIgnoreCase))
            return WorkEffortKind.Incident;
        if (name.Equals("sc_req_item", StringComparison.OrdinalIgnoreCase))
            return WorkEffortKind.RequestedItem;
        if (name.Equals("interaction", StringComparison.OrdinalIgnoreCase))
            return WorkEffortKind.Interaction;
        return null;
    }

    private static string ReadAuthor(JsonElement row, string name)
    {
        var field = SnowField.Read(row, name);
        var author = field.Value.Trim();
        if (author.Length == 0)
            author = field.Display.Trim();
        return author;
    }

    private readonly record struct JournalHit(string RecordId, string By, DateTime At, WorkEffortKind? Kind);

    private sealed class WorkEffortBudget
    {
        public WorkEffortBudget(int cap) => Cap = cap < 1 ? 1 : cap;

        public int Cap { get; }

        public int Received { get; private set; }

        public bool Truncated { get; private set; }

        public bool WantsMore => Received < Cap && !Truncated;

        public int Room => Math.Max(0, Cap - Received);

        public bool Take()
        {
            if (Received >= Cap)
            {
                Truncated = true;
                return false;
            }

            Received++;
            return true;
        }
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
        var number = SnowField.Read(row, "number");
        var title = SnowField.Read(row, "short_description");
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
            ReadMoment(row, "sys_updated_on"),
            Number: NullIfEmpty(number.Display.Length > 0 ? number.Display : number.Value),
            Title: NullIfEmpty(title.Display.Length > 0 ? title.Display : title.Value));
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
