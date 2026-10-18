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
        GetWorkEffortAsync(scale, localNow, team, regionOffices: null, progress, cancellationToken);

    public Task<WorkEffortReport> GetWorkEffortAsync(
        WorkEffortScale scale,
        DateTime localNow,
        IReadOnlyList<WorkEffortPerson> team,
        IReadOnlyList<string>? regionOffices,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken,
        DateTime? rangeStart = null,
        DateTime? rangeEnd = null) =>
        LoadWorkEffortAsync(scale, localNow, team, WorkEffortQuery.SafetyCap, regionOffices, progress, cancellationToken, rangeStart, rangeEnd);

    internal Task<WorkEffortReport> GetWorkEffortAsync(
        WorkEffortScale scale,
        DateTime localNow,
        int safetyCap,
        CancellationToken cancellationToken,
        IProgress<WorkEffortProgress>? progress = null,
        IReadOnlyList<WorkEffortPerson>? team = null,
        IReadOnlyList<string>? regionOffices = null,
        DateTime? rangeStart = null,
        DateTime? rangeEnd = null) =>
        LoadWorkEffortAsync(scale, localNow, team, safetyCap, regionOffices, progress, cancellationToken, rangeStart, rangeEnd);

    private async Task<WorkEffortReport> LoadWorkEffortAsync(
        WorkEffortScale scale,
        DateTime localNow,
        IReadOnlyList<WorkEffortPerson>? team,
        int safetyCap,
        IReadOnlyList<string>? regionOffices,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken,
        DateTime? rangeStart = null,
        DateTime? rangeEnd = null)
    {
        var people = WorkEffortTeam.Normalize(team);
        if (people.Count == 0)
            return WorkEffortReport.NoTeam();

        var places = TeamListScope.Places(regionOffices);
        if (places.Count == 0)
            return WorkEffortReport.NoRegion();

        var window = WorkEffortWindow.Resolve(scale, localNow, rangeStart, rangeEnd);
        var slices = WorkEffortSlices.Plan(window, people.Count);
        var cap = Math.Max(1, safetyCap);
        var facts = new WorkEffortLoadFacts(places, cap);
        var problems = new List<string>();
        var resolved = await ResolveTeamLoginsAsync(people, cancellationToken).ConfigureAwait(false);
        people = resolved.People;
        if (resolved.Problem is not null)
            problems.Add(resolved.Problem);
        if (!WorkEffortQuery.HasScope(people))
            return WorkEffortReport.NoTeam();

        // Newest local day first, then every selected person for that day, then the previous day.
        // One request at a time. A window of one local day stays one query per person.
        // Each request asks for one page. A full page is continued until ServiceNow runs out of rows.
        // When that page cannot be continued, the same slice is read again in smaller pieces.
        // The shared batch merges a record that shows up on more than one page.
        var pace = new WorkEffortPace();
        var batch = new WorkEffortBatch();
        var truncated = false;
        progress?.Report(pace.Snapshot());

        var incident = await LoadWorkEffortTableAsync(WorkEffortTablePlan.IncidentPlans, people, places, slices, cap, batch, facts, pace, progress, cancellationToken).ConfigureAwait(false);
        truncated |= incident.Truncated;
        if (incident.Problem is not null)
            problems.Add(incident.Problem);

        var items = await LoadWorkEffortTableAsync(WorkEffortTablePlan.RequestedItemAttempts, people, places, slices, cap, batch, facts, pace, progress, cancellationToken).ConfigureAwait(false);
        truncated |= items.Truncated;
        if (items.Problem is not null)
            problems.Add(items.Problem);

        var interactions = await LoadWorkEffortTableAsync(WorkEffortTablePlan.InteractionAttempts, people, places, slices, cap, batch, facts, pace, progress, cancellationToken).ConfigureAwait(false);
        truncated |= interactions.Truncated;
        if (interactions.Problem is not null)
            problems.Add(interactions.Problem);

        // History is for the tickets opened in those slices, across the whole window.
        var recordIds = batch.Touches().Select(touch => touch.SysId).ToArray();
        var history = await LoadWorkEffortHistoryAsync(recordIds, window, cap, batch, facts, pace, progress, cancellationToken).ConfigureAwait(false);
        truncated |= history.Truncated;
        if (history.Problem is not null)
            problems.Add(history.Problem);

        var touches = batch.Touches();
        var ledger = new WorkEffortLedger(people, touches, window);
        var rows = WorkEffortScore.Build(people, touches, window, WorkEffortUpdateMode.Daily);
        return new WorkEffortReport(rows, WorkEffortQuery.Status(window, truncated, problems), "", ledger, "", facts);
    }

    /// <summary>
    /// One narrow user lookup when a sign-in name is missing.
    /// Opens match on sys_id. Journal and audit authors match on the sign-in name.
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
        IReadOnlyList<string> places,
        IReadOnlyList<WorkEffortWindow> slices,
        int safetyCap,
        WorkEffortBatch batch,
        WorkEffortLoadFacts facts,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        ServiceNowException? rejected = null;
        for (var index = 0; index < plans.Count; index++)
        {
            pace.BeginTable();
            try
            {
                var truncated = await LoadWorkEffortPlanAsync(plans[index], people, places, slices, safetyCap, batch, facts, pace, progress, cancellationToken).ConfigureAwait(false);
                pace.CompleteTable();
                progress?.Report(pace.Snapshot());
                return (truncated, null);
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
        IReadOnlyList<string> places,
        IReadOnlyList<WorkEffortWindow> slices,
        int safetyCap,
        WorkEffortBatch batch,
        WorkEffortLoadFacts facts,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        var truncated = false;
        var tableName = WorkEffortCalcLog.TableName(plan.Kind);
        foreach (var slice in slices)
        {
            foreach (var person in people)
            {
                var day = DateOnly.FromDateTime(slice.Start);
                foreach (var place in places)
                {
                    var noted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var read = await ReadScopedAsync(
                        plan.Table,
                        plan.Fields,
                        slice,
                        safetyCap,
                        part => WorkEffortQuery.OpenedInOffice(plan, person, part, place),
                        row =>
                        {
                            var touch = ReadTouch(row, plan.Kind);
                            if (touch is null)
                                return;
                            // LIKE also returns a longer city (York finds New York). Keep the place rule.
                            if (!RegionOfficeMatch.LocationMatchesOffice(touch.Location, place))
                            {
                                if (noted.Add("out\n" + touch.SysId))
                                {
                                    facts.AddDrop(new WorkEffortSeenDrop(
                                        person.DisplayName,
                                        WorkEffortCalcLog.TicketLabel(touch.Number, touch.SysId),
                                        touch.SysId,
                                        tableName,
                                        string.IsNullOrWhiteSpace(touch.Location) ? "(blank)" : touch.Location.Trim(),
                                        day,
                                        place,
                                        WorkEffortCalcLog.OutsideRegion));
                                }

                                return;
                            }

                            if (batch.Contains(touch.Kind, touch.SysId) && noted.Add("dup\n" + touch.SysId))
                            {
                                facts.AddDrop(new WorkEffortSeenDrop(
                                    person.DisplayName,
                                    WorkEffortCalcLog.TicketLabel(touch.Number, touch.SysId),
                                    touch.SysId,
                                    tableName,
                                    string.IsNullOrWhiteSpace(touch.Location) ? "(blank)" : touch.Location.Trim(),
                                    day,
                                    place,
                                    WorkEffortCalcLog.DuplicateMerged));
                            }

                            batch.Add(touch);
                        },
                        person.DisplayName,
                        pace,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    foreach (var clause in read.Clauses)
                    {
                        facts.AddSlice(new WorkEffortSliceRecord(
                            person.DisplayName,
                            day,
                            place,
                            tableName,
                            clause.Role,
                            clause.RowsReturned,
                            clause.HitSafetyCap,
                            clause.ReportedTotal));
                    }

                    truncated |= read.Truncated;
                }

                progress?.Report(pace.Snapshot());
            }
        }

        return truncated;
    }

    private static string ClauseRole(string clause)
    {
        if (clause.Contains("opened_byISEMPTY", StringComparison.Ordinal)
            || (clause.Contains("opened_forIN", StringComparison.Ordinal)
                && !clause.Contains("opened_byIN", StringComparison.Ordinal)))
            return "opened for";
        return "opened by";
    }

    /// <summary>
    /// Reads every query for <paramref name="window"/>. A full page is continued. When the
    /// next page does not move forward, the window is read again in smaller pieces.
    /// <see cref="ScopedRead.Truncated"/> is true only when the smallest piece still cannot be continued.
    /// Row counts are distinct ids handed to <paramref name="accept"/> across those pieces.
    /// </summary>
    private async Task<ScopedRead> ReadScopedAsync(
        string table,
        string fields,
        WorkEffortWindow window,
        int pageSize,
        Func<WorkEffortWindow, IReadOnlyList<string>> queries,
        Action<JsonElement> accept,
        string subject,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        var clauses = queries(window);
        var tallies = new ClauseTally[clauses.Count];
        var totals = new int?[clauses.Count];
        var capped = new bool[clauses.Count];
        for (var index = 0; index < tallies.Length; index++)
            tallies[index] = new ClauseTally();

        var truncated = await ReadScopedCoreAsync(
            table,
            fields,
            window,
            pageSize,
            queries,
            accept,
            subject,
            pace,
            progress,
            cancellationToken,
            tallies,
            totals,
            capped,
            pursue: null).ConfigureAwait(false);

        var stats = new List<ClauseStats>(clauses.Count);
        for (var index = 0; index < clauses.Count; index++)
        {
            if (WorkEffortQuery.IsUnscoped(clauses[index]))
                continue;
            stats.Add(new ClauseStats(ClauseRole(clauses[index]), tallies[index].Rows, totals[index], capped[index]));
        }

        return new ScopedRead(truncated, stats);
    }

    private async Task<bool> ReadScopedCoreAsync(
        string table,
        string fields,
        WorkEffortWindow window,
        int pageSize,
        Func<WorkEffortWindow, IReadOnlyList<string>> queries,
        Action<JsonElement> accept,
        string subject,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken,
        ClauseTally[] tallies,
        int?[] totals,
        bool[] capped,
        bool[]? pursue)
    {
        var clauses = queries(window);
        var stuckAt = new bool[clauses.Count];
        var stuck = false;
        for (var index = 0; index < clauses.Count; index++)
        {
            if (pursue is not null && (index >= pursue.Length || !pursue[index]))
                continue;

            var clause = clauses[index];
            if (WorkEffortQuery.IsUnscoped(clause))
                continue;

            var tally = index < tallies.Length ? tallies[index] : null;
            var read = await PageWorkEffortAsync(
                table,
                fields,
                clause,
                pageSize,
                row =>
                {
                    tally?.Note(SysIdOf(row));
                    accept(row);
                },
                subject,
                window,
                pace,
                progress,
                cancellationToken).ConfigureAwait(false);
            if (index < totals.Length && read.ReportedTotal is int reported && totals[index] is null)
                totals[index] = reported;
            if (read.Outcome == PageOutcome.Stuck)
            {
                stuck = true;
                stuckAt[index] = true;
            }
        }

        if (!stuck)
            return false;

        var parts = WorkEffortSlices.Subdivide(window);
        if (parts.Count == 0)
        {
            for (var index = 0; index < stuckAt.Length && index < capped.Length; index++)
            {
                if (stuckAt[index])
                    capped[index] = true;
            }

            return true;
        }

        var truncated = false;
        foreach (var part in parts)
        {
            truncated |= await ReadScopedCoreAsync(
                table,
                fields,
                part,
                pageSize,
                queries,
                accept,
                subject,
                pace,
                progress,
                cancellationToken,
                tallies,
                totals,
                capped,
                stuckAt).ConfigureAwait(false);
        }

        return truncated;
    }

    /// <summary>
    /// One page at a time, one request at a time. The JSON document is dropped before the next
    /// page is asked for. display_value=all stays on because opened_by has to be the sys_id and
    /// the date has to be the local display time. A full page is not the end of the slice:
    /// the next page uses the offset link, then <c>sys_id</c> greater than the last id.
    /// Rows already seen on this query are not handed to <paramref name="accept"/> again.
    /// A non-matching LIKE row still counts as a row on the page, so later matches are requested.
    /// </summary>
    private async Task<PageRead> PageWorkEffortAsync(
        string table,
        string fields,
        string query,
        int pageSize,
        Action<JsonElement> accept,
        string subject,
        WorkEffortWindow window,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        var limit = pageSize < 1 ? 1 : pageSize;
        var offset = 0;
        var received = 0;
        int? total = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = query;
        var keyset = false;
        string? cursor = null;
        var page = 1;

        for (var guard = 0; guard < 10000; guard++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pace.Reading(WorkEffortProgress.Reading(subject, window, page));
            progress?.Report(pace.Snapshot());
            var result = await GetListAsync(table, fields, active, limit, keyset ? 0 : offset, cancellationToken).ConfigureAwait(false);
            var outcome = PageOutcome.Continue;
            var nextOffset = offset;
            using (result)
            using (new WorkEffortQuietScope())
            {
                var rows = RequireArray(result.Document);
                var count = rows.GetArrayLength();
                if (result.TotalCount is int reported)
                    total = reported;

                var identifiable = 0;
                var fresh = 0;
                string? pageLast = null;
                foreach (var row in rows.EnumerateArray())
                {
                    var id = SysIdOf(row);
                    if (id.Length == 0)
                    {
                        accept(row);
                        continue;
                    }

                    identifiable++;
                    if (pageLast is null || string.Compare(id, pageLast, StringComparison.Ordinal) > 0)
                        pageLast = id;
                    if (!seen.Add(id))
                        continue;
                    fresh++;
                    accept(row);
                }

                if (pageLast is not null)
                    cursor = pageLast;

                received += count;
                pace.AddPage(count, total);
                progress?.Report(pace.Snapshot());

                var hasNext = !string.IsNullOrWhiteSpace(result.NextLink);
                var more = total is int expected && received < expected;
                var doneByTotal = total is int finished && received >= finished;
                var full = count >= limit;

                if (count == 0 || (identifiable > 0 && fresh == 0) || (identifiable == 0 && count > 0 && (full || more || hasNext)))
                {
                    if (count == 0 && !more && !hasNext)
                        outcome = PageOutcome.Done;
                    else if (fresh == 0 && doneByTotal && !hasNext)
                        outcome = PageOutcome.Done;
                    else if (!keyset && cursor is not null && TryKeyset(query, cursor, out var keyed))
                    {
                        keyset = true;
                        active = keyed;
                        nextOffset = 0;
                    }
                    else if (count == 0 && !more)
                        outcome = PageOutcome.Done;
                    else
                        outcome = PageOutcome.Stuck;
                }
                else if (!full && !more && !hasNext)
                {
                    outcome = PageOutcome.Done;
                }
                else if (!full && doneByTotal && !hasNext)
                {
                    outcome = PageOutcome.Done;
                }
                else if (keyset)
                {
                    if (cursor is null || !TryKeyset(query, cursor, out var keyed))
                        outcome = PageOutcome.Stuck;
                    else
                    {
                        active = keyed;
                        nextOffset = 0;
                    }
                }
                else
                {
                    var step = offset + count;
                    var linkOffset = TryReadOffset(result.NextLink);
                    nextOffset = step;
                    if (linkOffset > offset && (full || linkOffset <= step))
                        nextOffset = linkOffset;
                    if (nextOffset <= offset)
                    {
                        if (cursor is not null && TryKeyset(query, cursor, out var keyed))
                        {
                            keyset = true;
                            active = keyed;
                            nextOffset = 0;
                        }
                        else
                            outcome = PageOutcome.Stuck;
                    }
                }
            }

            if (outcome == PageOutcome.Done)
                return new PageRead(PageOutcome.Done, total);
            if (outcome == PageOutcome.Stuck)
                return new PageRead(PageOutcome.Stuck, total);
            offset = nextOffset;
            page++;
        }

        return new PageRead(PageOutcome.Stuck, total);
    }

    private enum PageOutcome
    {
        Continue,
        Done,
        Stuck
    }

    private static bool TryKeyset(string query, string cursor, out string keyed)
    {
        keyed = query;
        try
        {
            keyed = WorkEffortQuery.AfterSysId(query, cursor);
            return !string.Equals(keyed, query, StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string SysIdOf(JsonElement row)
    {
        var id = SnowField.Read(row, "sys_id").Value.Trim();
        if (id.Length == 0)
            id = SnowField.Read(row, "sys_id").Display.Trim();
        return id;
    }

    /// <summary>
    /// Journal rows (work notes and comments) plus audit rows when the instance allows them.
    /// The header update is only the latest save, so earlier days live here. A journal row and
    /// the header row at the same local second are one moment when the score runs.
    /// </summary>
    private async Task<(bool Truncated, string? Problem)> LoadWorkEffortHistoryAsync(
        IReadOnlyList<string> recordIds,
        WorkEffortWindow window,
        int safetyCap,
        WorkEffortBatch batch,
        WorkEffortLoadFacts facts,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        var truncated = false;
        string? problem = null;
        pace.BeginTable();
        try
        {
            truncated |= await LoadJournalAsync(recordIds, window, safetyCap, batch, facts, pace, progress, cancellationToken).ConfigureAwait(false);
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
            truncated |= await LoadAuditAsync(recordIds, window, safetyCap, batch, facts, pace, progress, cancellationToken).ConfigureAwait(false);
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
        IReadOnlyList<string> recordIds,
        WorkEffortWindow window,
        int safetyCap,
        WorkEffortBatch batch,
        WorkEffortLoadFacts facts,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pending = new List<JournalHit>();
        var truncated = false;
        for (var index = 0; index < recordIds.Count; index += WorkEffortQuery.ChunkSize)
        {
            var chunk = recordIds.Skip(index).Take(WorkEffortQuery.ChunkSize).ToArray();
            var read = await ReadScopedAsync(
                WorkEffortQuery.JournalTable,
                WorkEffortQuery.JournalFields,
                window,
                safetyCap,
                part => [WorkEffortQuery.JournalForRecords(chunk, part)],
                row =>
                {
                    if (!TryReadJournal(row, window, out var hit))
                        return;
                    if (hit.Kind is WorkEffortKind known)
                        batch.AddUpdate(known, hit.RecordId, hit.By, hit.At);
                    else
                        pending.Add(hit);
                },
                "update notes",
                pace,
                progress,
                cancellationToken).ConfigureAwait(false);
            truncated |= read.Truncated;
            RecordHistory(facts, "journal", read);
        }

        if (pending.Count > 0)
            await PlaceJournalAsync(pending, batch, cancellationToken).ConfigureAwait(false);
        return truncated;
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
        IReadOnlyList<string> recordIds,
        WorkEffortWindow window,
        int safetyCap,
        WorkEffortBatch batch,
        WorkEffortLoadFacts facts,
        WorkEffortPace pace,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        var truncated = false;
        for (var index = 0; index < recordIds.Count; index += WorkEffortQuery.ChunkSize)
        {
            var chunk = recordIds.Skip(index).Take(WorkEffortQuery.ChunkSize).ToArray();
            var read = await ReadScopedAsync(
                WorkEffortQuery.AuditTable,
                WorkEffortQuery.AuditFields,
                window,
                safetyCap,
                part => [WorkEffortQuery.AuditForRecords(chunk, part)],
                row =>
                {
                    if (!TryReadAudit(row, window, out var kind, out var id, out var by, out var at))
                        return;
                    batch.AddUpdate(kind, id, by, at);
                },
                "field history",
                pace,
                progress,
                cancellationToken).ConfigureAwait(false);
            truncated |= read.Truncated;
            RecordHistory(facts, "audit", read);
        }

        return truncated;
    }

    private static void RecordHistory(WorkEffortLoadFacts facts, string source, ScopedRead read)
    {
        if (read.Clauses.Count == 0)
            return;

        var rows = 0;
        int? reported = null;
        var capped = false;
        foreach (var clause in read.Clauses)
        {
            rows += clause.RowsReturned;
            capped |= clause.HitSafetyCap;
            reported ??= clause.ReportedTotal;
        }

        facts.AddHistory(new WorkEffortHistoryRecord(source, rows, capped, reported));
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

    private readonly record struct PageRead(PageOutcome Outcome, int? ReportedTotal);

    private readonly record struct ClauseStats(string Role, int RowsReturned, int? ReportedTotal, bool HitSafetyCap);

    private readonly record struct ScopedRead(bool Truncated, IReadOnlyList<ClauseStats> Clauses);

    /// <summary>Distinct rows handed to the reader for one opener or history clause, including later pages.</summary>
    private sealed class ClauseTally
    {
        private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
        private int _anonymous;

        public int Rows => _ids.Count + _anonymous;

        public void Note(string id)
        {
            if (id.Length == 0)
                _anonymous++;
            else
                _ids.Add(id);
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
        var location = SnowField.Read(row, "location");
        var locationName = location.Display.Trim();
        if (locationName.Length == 0)
            locationName = location.Value.Trim();
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
            Title: NullIfEmpty(title.Display.Length > 0 ? title.Display : title.Value),
            Location: NullIfEmpty(locationName));
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
