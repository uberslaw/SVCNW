using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.QueueDay;

public static class QueueDayCheck
{
    public static async Task<QueueDayReport> RunAsync(
        IQueueDaySource source,
        IQueueDayStore store,
        IReadOnlyList<WorkEffortPerson> team,
        DateTime localNow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(store);
        var people = WorkEffortTeam.Normalize(team);
        if (people.Count == 0)
            return QueueDayReport.DefineTeam();

        var day = DateOnly.FromDateTime(localNow);
        var fresh = new List<(WorkEffortPerson Person, IReadOnlyList<QueueDayTicket> Tickets)>();
        foreach (var person in people.OrderBy(person => person.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = await source.ListOpenAssignedAsync(person.SysId, cancellationToken).ConfigureAwait(false);
            fresh.Add((person, KeepOpenAssigned(person.SysId, rows)));
        }

        var existing = store.Find(day);
        if (existing is null || existing.LocalDate != day)
        {
            var baseline = new QueueDayBaseline(
                day,
                localNow,
                fresh.Select(row => new QueueDayPersonBaseline(
                    row.Person.SysId,
                    row.Person.DisplayName,
                    row.Tickets)).ToArray());
            store.Save(baseline);
            var saved = store.Find(day);
            if (saved is null || saved.LocalDate != day || saved.TakenAt != baseline.TakenAt)
            {
                throw new InvalidOperationException(
                    "Queue check wrote today's baseline but could not read it back.");
            }

            return Report(saved, localNow, created: true, LinesFromBaseline(saved));
        }

        var compared = await CompareAsync(source, existing, fresh, cancellationToken).ConfigureAwait(false);
        return Report(existing, localNow, created: false, compared);
    }

    private static async Task<IReadOnlyList<QueueDayPersonResult>> CompareAsync(
        IQueueDaySource source,
        QueueDayBaseline baseline,
        IReadOnlyList<(WorkEffortPerson Person, IReadOnlyList<QueueDayTicket> Tickets)> fresh,
        CancellationToken cancellationToken)
    {
        var baselinePeople = baseline.People.ToDictionary(person => person.PersonId, StringComparer.OrdinalIgnoreCase);
        var missing = new List<(WorkEffortPerson Person, QueueDayTicket Ticket)>();
        var staged = new List<(WorkEffortPerson Person, List<QueueDayLine> Lines)>();
        foreach (var (person, tickets) in fresh)
        {
            var before = baselinePeople.TryGetValue(person.SysId, out var stored)
                ? stored.Tickets
                : [];
            var beforeById = Index(before);
            var nowIds = new HashSet<string>(tickets.Select(ticket => ticket.SysId.Trim()), StringComparer.OrdinalIgnoreCase);
            var lines = new List<QueueDayLine>();
            foreach (var ticket in tickets)
            {
                if (!beforeById.TryGetValue(ticket.SysId.Trim(), out var prior))
                {
                    lines.Add(Line(person, ticket, QueueDayKind.New, QueueDayText.New));
                    continue;
                }

                lines.Add(CompareStill(person, prior, ticket, baseline.TakenAt));
            }

            foreach (var prior in before)
            {
                if (prior.SysId.Trim().Length == 0 || nowIds.Contains(prior.SysId.Trim()))
                    continue;
                missing.Add((person, prior));
            }

            staged.Add((person, lines));
        }

        var found = new Dictionary<string, QueueDayTicket>(StringComparer.OrdinalIgnoreCase);
        if (missing.Count > 0)
        {
            var refs = missing
                .Select(row => new QueueDayTicketRef(QueueDayQuery.NormalizeTable(row.Ticket.Table), row.Ticket.SysId.Trim()))
                .ToArray();
            var lookedUp = await source.LookupAsync(refs, cancellationToken).ConfigureAwait(false);
            foreach (var ticket in lookedUp ?? [])
            {
                if (ticket is null || string.IsNullOrWhiteSpace(ticket.SysId))
                    continue;
                found[Key(ticket.Table, ticket.SysId)] = ticket;
            }
        }

        var results = new List<QueueDayPersonResult>();
        foreach (var (person, lines) in staged)
        {
            foreach (var (owner, prior) in missing.Where(row => Same(row.Person.SysId, person.SysId)))
            {
                found.TryGetValue(Key(prior.Table, prior.SysId), out var hit);
                lines.Add(CompareMissing(owner, prior, hit));
            }

            results.Add(new QueueDayPersonResult(
                person.SysId,
                person.DisplayName,
                lines.OrderBy(line => Rank(line.Kind)).ThenBy(line => line.Number, StringComparer.OrdinalIgnoreCase).ToArray()));
        }

        return results;
    }

    private static IReadOnlyList<QueueDayPersonResult> LinesFromBaseline(QueueDayBaseline baseline) =>
        baseline.People.Select(person => new QueueDayPersonResult(
            person.PersonId,
            person.PersonName,
            person.Tickets
                .Select(ticket => Line(person.PersonId, person.PersonName, ticket, QueueDayKind.Unchanged, QueueDayText.Unchanged))
                .OrderBy(line => line.Number, StringComparer.OrdinalIgnoreCase)
                .ToArray())).ToArray();

    private static QueueDayReport Report(
        QueueDayBaseline baseline,
        DateTime checkedAt,
        bool created,
        IReadOnlyList<QueueDayPersonResult> people) =>
        new(
            baseline.TakenAt,
            checkedAt,
            created,
            people,
            created ? QueueDayText.BaselineSaved : QueueDayText.Compared,
            "");

    private static QueueDayLine CompareStill(
        WorkEffortPerson person,
        QueueDayTicket before,
        QueueDayTicket fresh,
        DateTime baselineAt)
    {
        var facts = new List<string>();
        if (GroupChanged(before, fresh))
            facts.Add(QueueDayText.GroupChanged(GroupLabel(before), GroupLabel(fresh)));
        var update = UpdateFact(fresh, baselineAt);
        if (update is not null)
            facts.Add(update);
        if (facts.Count == 0)
            return Line(person, fresh, QueueDayKind.Unchanged, QueueDayText.Unchanged);
        return Line(person, fresh, QueueDayKind.Changed, QueueDayText.Join(facts));
    }

    private static QueueDayLine CompareMissing(WorkEffortPerson person, QueueDayTicket before, QueueDayTicket? found)
    {
        if (found is null)
            return Line(person, before, QueueDayKind.Missing, QueueDayText.LookupMissed);

        var facts = new List<string>();
        AddClosure(facts, found);
        if (!Same(found.AssigneeId, person.SysId))
        {
            facts.Add(string.IsNullOrWhiteSpace(found.AssigneeId)
                ? QueueDayText.Unassigned
                : QueueDayText.AssignedTo(Prefer(found.AssigneeName, found.AssigneeId)));
        }

        if (GroupChanged(before, found))
            facts.Add(QueueDayText.GroupChanged(GroupLabel(before), GroupLabel(found)));
        var shown = string.IsNullOrWhiteSpace(found.Number) ? before : found;
        var description = string.IsNullOrWhiteSpace(shown.ShortDescription) ? before.ShortDescription : shown.ShortDescription;
        var number = string.IsNullOrWhiteSpace(shown.Number) ? before.Number : shown.Number;
        var ticket = shown with { Number = number ?? "", ShortDescription = description ?? "" };
        if (facts.Count == 0)
            facts.Add(QueueDayText.NoLonger);
        return Line(person, ticket, QueueDayKind.Missing, QueueDayText.Join(facts));
    }

    private static string? UpdateFact(QueueDayTicket fresh, DateTime baselineAt)
    {
        if (fresh.UpdatedAt is not DateTime at || at <= baselineAt)
            return null;
        var by = fresh.UpdatedBy?.Trim() ?? "";
        if (by.Length == 0)
            return QueueDayText.Updated;
        if (SameUser(by, fresh.AssigneeUserName))
            return QueueDayText.UpdatedByAssignee;
        if (SameUser(by, fresh.CallerUserName))
            return QueueDayText.UpdatedByCaller;
        return QueueDayText.UpdatedBy(by);
    }

    private static void AddClosure(List<string> facts, QueueDayTicket ticket)
    {
        var value = ticket.StateValue?.Trim() ?? "";
        var label = string.IsNullOrWhiteSpace(ticket.StateDisplay) ? value : ticket.StateDisplay.Trim();
        var shown = label.Length == 0 ? value : label;
        var section = QueueDayQuery.Section(ticket.Table);
        if (Has(label, "cancel") || Has(value, "cancel") || (section == DeskSection.Incidents && value == "8"))
            facts.Add(QueueDayText.Cancelled(shown));
        if (Has(label, "resolv") || Has(value, "resolv") || (section == DeskSection.Incidents && value == "6"))
            facts.Add(QueueDayText.Resolved(shown));
        if (Has(label, "closed") || Has(value, "closed") || ClosedCode(section, value))
            facts.Add(QueueDayText.Closed(shown));
    }

    private static bool ClosedCode(DeskSection section, string value) => section switch
    {
        DeskSection.Incidents => value == "7",
        DeskSection.RequestedItems => value is "3" or "4" or "7",
        DeskSection.WalkUps => value is "closed_complete" or "closed_abandoned",
        _ => false
    };

    private static bool GroupChanged(QueueDayTicket before, QueueDayTicket after)
    {
        var oldId = before.AssignmentGroupId?.Trim() ?? "";
        var newId = after.AssignmentGroupId?.Trim() ?? "";
        if (oldId.Length > 0 && newId.Length > 0)
            return !oldId.Equals(newId, StringComparison.OrdinalIgnoreCase);
        return !string.Equals(GroupLabel(before), GroupLabel(after), StringComparison.OrdinalIgnoreCase);
    }

    private static string GroupLabel(QueueDayTicket ticket)
    {
        var name = ticket.AssignmentGroupName?.Trim() ?? "";
        if (name.Length > 0)
            return name;
        var id = ticket.AssignmentGroupId?.Trim() ?? "";
        return id.Length == 0 ? "none" : id;
    }

    private static IReadOnlyList<QueueDayTicket> KeepOpenAssigned(string personId, IReadOnlyList<QueueDayTicket>? tickets)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<QueueDayTicket>();
        foreach (var ticket in tickets ?? [])
        {
            if (ticket is null || string.IsNullOrWhiteSpace(ticket.SysId) || !seen.Add(ticket.SysId.Trim()))
                continue;
            if (!Same(ticket.AssigneeId, personId))
                continue;
            if (!AlertClassifier.IsStillOpen(QueueDayQuery.Section(ticket.Table), ticket.StateValue, ticket.StateDisplay))
                continue;
            kept.Add(ticket);
        }

        return kept;
    }

    private static Dictionary<string, QueueDayTicket> Index(IReadOnlyList<QueueDayTicket> tickets)
    {
        var map = new Dictionary<string, QueueDayTicket>(StringComparer.OrdinalIgnoreCase);
        foreach (var ticket in tickets)
        {
            var id = ticket.SysId?.Trim() ?? "";
            if (id.Length > 0 && !map.ContainsKey(id))
                map[id] = ticket;
        }

        return map;
    }

    private static QueueDayLine Line(WorkEffortPerson person, QueueDayTicket ticket, QueueDayKind kind, string breakdown) =>
        Line(person.SysId, person.DisplayName, ticket, kind, breakdown);

    private static QueueDayLine Line(string personId, string personName, QueueDayTicket ticket, QueueDayKind kind, string breakdown) =>
        new(
            personId,
            personName,
            ticket.Number?.Trim() ?? "",
            ticket.ShortDescription?.Trim() ?? "",
            QueueDayQuery.NormalizeTable(ticket.Table),
            ticket.SysId?.Trim() ?? "",
            kind,
            breakdown ?? "");

    private static string Key(string? table, string? sysId) =>
        QueueDayQuery.NormalizeTable(table) + "|" + (sysId ?? "").Trim();

    private static int Rank(QueueDayKind kind) => kind switch
    {
        QueueDayKind.Changed => 0,
        QueueDayKind.New => 1,
        QueueDayKind.Missing => 2,
        _ => 3
    };

    private static string Prefer(string? name, string? fallback)
    {
        var text = name?.Trim() ?? "";
        return text.Length > 0 ? text : (fallback ?? "").Trim();
    }

    private static bool Has(string? text, string fragment) =>
        !string.IsNullOrWhiteSpace(text) && text.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string? left, string? right) =>
        string.Equals((left ?? "").Trim(), (right ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameUser(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && Same(left, right);
}
