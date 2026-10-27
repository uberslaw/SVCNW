namespace ServiceNowDesk.WorkEffort;

public static class WorkEffortTeam
{
    public static IReadOnlyList<WorkEffortPerson> Normalize(IEnumerable<WorkEffortPerson>? team)
    {
        var map = new Dictionary<string, WorkEffortPerson>(StringComparer.OrdinalIgnoreCase);
        if (team is null)
            return [];
        foreach (var person in team)
        {
            if (person is null)
                continue;
            var id = (person.SysId ?? "").Trim();
            if (id.Length == 0 || map.ContainsKey(id))
                continue;
            var name = (person.Name ?? "").Trim();
            var user = (person.UserName ?? "").Trim();
            map[id] = new WorkEffortPerson(id, name, user);
        }

        return map.Values.ToArray();
    }

    public static string Key(IEnumerable<WorkEffortPerson>? team)
    {
        var ids = Normalize(team)
            .Select(person => person.SysId)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        return string.Join("|", ids);
    }

    /// <summary>
    /// The ticked team plus the signed-in user. A person already on the team stays one row.
    /// Match sys_id when both sides have one, otherwise user_name, otherwise display name.
    /// A blank session is not a person.
    /// </summary>
    public static IReadOnlyList<WorkEffortPerson> IncludeSignedIn(
        IEnumerable<WorkEffortPerson>? team,
        WorkEffortPerson? signedIn)
    {
        var people = Normalize(team);
        var person = SessionPerson(signedIn);
        if (person is null)
            return people;

        foreach (var existing in people)
        {
            if (SamePerson(existing, person))
                return people;
        }

        var combined = new WorkEffortPerson[people.Count + 1];
        for (var index = 0; index < people.Count; index++)
            combined[index] = people[index];
        combined[^1] = person;
        return combined;
    }

    private static WorkEffortPerson? SessionPerson(WorkEffortPerson? signedIn)
    {
        if (signedIn is null)
            return null;
        var id = (signedIn.SysId ?? "").Trim();
        var name = (signedIn.Name ?? "").Trim();
        var user = (signedIn.UserName ?? "").Trim();
        if (id.Length == 0 && name.Length == 0 && user.Length == 0)
            return null;
        return new WorkEffortPerson(id, name, user);
    }

    private static bool SamePerson(WorkEffortPerson teamMember, WorkEffortPerson signedIn)
    {
        var teamId = (teamMember.SysId ?? "").Trim();
        var signedId = (signedIn.SysId ?? "").Trim();
        if (teamId.Length > 0 && signedId.Length > 0)
            return teamId.Equals(signedId, StringComparison.OrdinalIgnoreCase);

        var teamUser = (teamMember.UserName ?? "").Trim();
        var signedUser = (signedIn.UserName ?? "").Trim();
        if (teamUser.Length > 0 && signedUser.Length > 0)
            return teamUser.Equals(signedUser, StringComparison.OrdinalIgnoreCase);

        var teamName = teamMember.DisplayName.Trim();
        var signedName = signedIn.DisplayName.Trim();
        return teamName.Length > 0
            && signedName.Length > 0
            && teamName.Equals(signedName, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Counts one table page at a time. Record ids already counted stop growing at the safety cap.
/// Credited ticket lines are kept for drill-down after the page is folded.
/// </summary>
public sealed class WorkEffortAttempt
{
    public const int Width = 9;

    private readonly WorkEffortPerson[] _people;
    private readonly WorkEffortWindow _window;
    private readonly WorkEffortUpdateMode _mode;
    private readonly int _cap;
    private readonly int[] _counts;
    private readonly HashSet<string> _seen;
    private readonly HashSet<LifeKey> _life = [];
    private readonly List<PendingUpdate> _updates = [];
    private readonly Dictionary<string, TouchLabel> _labels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string>? _places;
    private readonly List<WorkEffortCredit> _credits = [];
    private readonly WorkEffortScoreNotes? _notes;
    private bool _finished;

    public WorkEffortAttempt(
        IReadOnlyList<WorkEffortPerson> people,
        WorkEffortWindow window,
        int safetyCap,
        WorkEffortUpdateMode mode = WorkEffortUpdateMode.Daily,
        WorkEffortScoreNotes? notes = null)
    {
        ArgumentNullException.ThrowIfNull(people);
        _people = people as WorkEffortPerson[] ?? people.ToArray();
        _window = window;
        _mode = mode;
        _notes = notes;
        _cap = safetyCap < 1 ? 1 : safetyCap;
        _counts = new int[_people.Length * Width];
        _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (notes is not null)
            _places = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public int Received { get; private set; }

    public bool Truncated { get; private set; }

    public bool WantsMore => Received < _cap && !Truncated;

    public int Room => Math.Max(0, _cap - Received);

    /// <summary>
    /// Accepts one ServiceNow row. Returns false when the cap was already full
    /// and the caller should stop reading. The row that fills the cap is kept.
    /// </summary>
    public bool TakeRow(WorkEffortTouch? touch)
    {
        if (Received >= _cap)
        {
            Truncated = true;
            return false;
        }

        Received++;
        if (touch is not null)
            Score(touch);
        return true;
    }

    public void FoldInto(int[] totals)
    {
        ArgumentNullException.ThrowIfNull(totals);
        Finish();
        if (totals.Length != _counts.Length)
            throw new ArgumentException("Count width does not match the team.", nameof(totals));
        for (var index = 0; index < _counts.Length; index++)
            totals[index] += _counts[index];
    }

    public IReadOnlyList<WorkEffortRow> ToRows()
    {
        Finish();
        return ToRows(_people, _counts);
    }

    public IReadOnlyList<WorkEffortCredit> Credits()
    {
        Finish();
        return EnrichWorkTypes(_credits)
            .OrderBy(line => line.PersonName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.When)
            .ThenBy(line => line.DisplayNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.MetricLabel, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Stamp every credit line with the full Opened / Resolved / update-count mix for that ticket,
    /// so Work Type can show combinations like "Opened, 3 Updates".
    /// </summary>
    private static IEnumerable<WorkEffortCredit> EnrichWorkTypes(IEnumerable<WorkEffortCredit> lines)
    {
        var list = lines as IList<WorkEffortCredit> ?? lines.ToArray();
        if (list.Count == 0)
            yield break;

        var mix = new Dictionary<string, TicketMix>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in list)
        {
            var key = MixKey(line);
            if (!mix.TryGetValue(key, out var entry))
            {
                entry = new TicketMix();
                mix[key] = entry;
            }

            if (line.Metric == WorkEffortMetric.Opened)
                entry.Opened = true;
            if (line.Metric is WorkEffortMetric.Resolved or WorkEffortMetric.Closed)
                entry.Resolved = true;
            if (line.Metric == WorkEffortMetric.Updated && line.UpdateCount > entry.UpdateCount)
                entry.UpdateCount = line.UpdateCount;
        }

        foreach (var line in list)
        {
            var entry = mix[MixKey(line)];
            yield return line with
            {
                TicketOpened = entry.Opened,
                TicketResolved = entry.Resolved,
                UpdateCount = entry.UpdateCount > 0 ? entry.UpdateCount : line.UpdateCount
            };
        }
    }

    private static string MixKey(WorkEffortCredit line) =>
        line.PersonSysId + "\n" + (int)line.Kind + "\n" + line.RecordSysId;

    private sealed class TicketMix
    {
        public bool Opened;
        public bool Resolved;
        public int UpdateCount;
    }

    public static IReadOnlyList<WorkEffortRow> ToRows(IReadOnlyList<WorkEffortPerson> people, int[] counts)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(counts);
        var rows = new List<WorkEffortRow>(people.Count);
        for (var index = 0; index < people.Count; index++)
        {
            var slot = index * Width;
            var incOpened = counts[slot];
            var incResolved = counts[slot + 1];
            var incUpdated = counts[slot + 2];
            var ritmOpened = counts[slot + 3];
            var ritmResolved = counts[slot + 4];
            var ritmUpdated = counts[slot + 5];
            var imsOpened = counts[slot + 6];
            var imsResolved = counts[slot + 7];
            var imsUpdated = counts[slot + 8];
            var weighted = (incOpened + incResolved) * WorkEffortScore.IncidentWeight
                + (ritmOpened + ritmResolved) * WorkEffortScore.RequestedItemWeight
                + (imsOpened + imsResolved) * WorkEffortScore.InteractionWeight
                + (incUpdated + ritmUpdated + imsUpdated) * WorkEffortScore.UpdateWeight;
            rows.Add(new WorkEffortRow
            {
                PersonSysId = people[index].SysId,
                Name = people[index].DisplayName,
                IncOpened = incOpened,
                IncResolved = incResolved,
                IncUpdated = incUpdated,
                RitmOpened = ritmOpened,
                RitmResolved = ritmResolved,
                RitmUpdated = ritmUpdated,
                ImsOpened = imsOpened,
                ImsResolved = imsResolved,
                ImsUpdated = imsUpdated,
                Weighted = weighted
            });
        }

        return rows
            .OrderByDescending(row => row.Weighted)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void Score(WorkEffortTouch touch)
    {
        var id = (touch.SysId ?? "").Trim();
        if (id.Length == 0)
            return;
        if (!_seen.Add((int)touch.Kind + "\n" + id))
        {
            NoteDropped("", touch.Kind, id, touch.OpenedAt is DateTime openedAt ? WorkEffortWindow.LocalDay(openedAt) : null, WorkEffortCalcLog.DuplicateMerged);
            return;
        }

        RememberLabel(touch.Kind, id, touch.Number, touch.Title);
        RememberPlace(touch.Kind, id, touch.Location);
        if (_notes is not null)
        {
            NoteIfOutside(touch, touch.OpenedAt);
            NoteIfOutside(touch, touch.ResolvedAt);
            NoteIfOutside(touch, touch.ClosedAt);
        }

        var sawOpened = false;
        var sawResolved = false;
        var sawClosed = false;
        for (var index = 0; index < _people.Length; index++)
        {
            var person = _people[index];
            var opened = InWindow(touch.OpenedAt) && Same(person.SysId, touch.OpenedBySysId);
            var resolved = InWindow(touch.ResolvedAt) && Same(person.SysId, touch.ResolvedBySysId);
            var closed = InWindow(touch.ClosedAt) && Same(person.SysId, touch.ClosedBySysId);
            if (opened)
                sawOpened = true;
            if (resolved)
                sawResolved = true;
            if (closed)
                sawClosed = true;
            if (opened)
                _life.Add(new LifeKey(index, touch.Kind, id, DayNumber(touch.OpenedAt!.Value)));
            if (resolved)
                _life.Add(new LifeKey(index, touch.Kind, id, DayNumber(touch.ResolvedAt!.Value)));
            if (closed)
                _life.Add(new LifeKey(index, touch.Kind, id, DayNumber(touch.ClosedAt!.Value)));
            var completed = resolved || closed;
            var slot = index * Width;
            switch (touch.Kind)
            {
                case WorkEffortKind.RequestedItem:
                    if (opened)
                    {
                        _counts[slot + 3]++;
                        AddLifeCredit(index, touch.Kind, id, WorkEffortMetric.Opened, touch.OpenedAt!.Value);
                    }

                    if (completed)
                    {
                        _counts[slot + 4]++;
                        if (resolved)
                            AddLifeCredit(index, touch.Kind, id, WorkEffortMetric.Resolved, touch.ResolvedAt!.Value);
                        else
                            AddLifeCredit(index, touch.Kind, id, WorkEffortMetric.Closed, touch.ClosedAt!.Value);
                    }

                    break;
                case WorkEffortKind.Interaction:
                    if (opened)
                    {
                        _counts[slot + 6]++;
                        AddLifeCredit(index, touch.Kind, id, WorkEffortMetric.Opened, touch.OpenedAt!.Value);
                    }

                    if (completed)
                    {
                        _counts[slot + 7]++;
                        if (resolved)
                            AddLifeCredit(index, touch.Kind, id, WorkEffortMetric.Resolved, touch.ResolvedAt!.Value);
                        else
                            AddLifeCredit(index, touch.Kind, id, WorkEffortMetric.Closed, touch.ClosedAt!.Value);
                    }

                    break;
                default:
                    if (opened)
                    {
                        _counts[slot]++;
                        AddLifeCredit(index, touch.Kind, id, WorkEffortMetric.Opened, touch.OpenedAt!.Value);
                    }

                    if (completed)
                    {
                        _counts[slot + 1]++;
                        if (resolved)
                            AddLifeCredit(index, touch.Kind, id, WorkEffortMetric.Resolved, touch.ResolvedAt!.Value);
                        else
                            AddLifeCredit(index, touch.Kind, id, WorkEffortMetric.Closed, touch.ClosedAt!.Value);
                    }

                    break;
            }

            if (resolved && closed)
            {
                NoteDropped(
                    person.DisplayName,
                    touch.Kind,
                    id,
                    WorkEffortWindow.LocalDay(touch.ClosedAt!.Value),
                    WorkEffortCalcLog.CloseAlreadyResolved);
            }
        }

        if (_notes is not null)
        {
            if (InWindow(touch.OpenedAt) && !string.IsNullOrWhiteSpace(touch.OpenedBySysId) && !sawOpened)
            {
                NoteDropped(
                    touch.OpenedBySysId.Trim(),
                    touch.Kind,
                    id,
                    WorkEffortWindow.LocalDay(touch.OpenedAt!.Value),
                    WorkEffortCalcLog.NotSelected);
            }

            if (InWindow(touch.ResolvedAt) && !string.IsNullOrWhiteSpace(touch.ResolvedBySysId) && !sawResolved)
            {
                NoteDropped(
                    touch.ResolvedBySysId.Trim(),
                    touch.Kind,
                    id,
                    WorkEffortWindow.LocalDay(touch.ResolvedAt!.Value),
                    WorkEffortCalcLog.NotSelected);
            }

            if (InWindow(touch.ClosedAt) && !string.IsNullOrWhiteSpace(touch.ClosedBySysId) && !sawClosed)
            {
                NoteDropped(
                    touch.ClosedBySysId.Trim(),
                    touch.Kind,
                    id,
                    WorkEffortWindow.LocalDay(touch.ClosedAt!.Value),
                    WorkEffortCalcLog.NotSelected);
            }
        }

        ConsiderUpdate(touch.Kind, id, touch.UpdatedBy, touch.UpdatedAt);
        if (touch.Updates is null)
            return;
        foreach (var update in touch.Updates)
            ConsiderUpdate(touch.Kind, id, update.By, update.At);
    }

    private void ConsiderUpdate(WorkEffortKind kind, string recordId, string? by, DateTime? at)
    {
        if (at is not DateTime moment || string.IsNullOrWhiteSpace(by))
            return;
        if (!InWindow(moment))
        {
            NoteDropped(by.Trim(), kind, recordId, WorkEffortWindow.LocalDay(moment), WorkEffortCalcLog.OutsideWindow);
            return;
        }

        var stamp = WorkEffortWindow.LocalStamp(moment);
        var day = DayNumber(stamp);
        var matched = false;
        for (var index = 0; index < _people.Length; index++)
        {
            var person = _people[index];
            if (!Same(person.UserName, by) && !Same(person.SysId, by))
                continue;
            matched = true;
            if (_life.Contains(new LifeKey(index, kind, recordId, day)))
            {
                NoteDropped(
                    person.DisplayName,
                    kind,
                    recordId,
                    WorkEffortWindow.LocalDay(stamp),
                    WorkEffortCalcLog.UpdateSuppressed);
                continue;
            }

            _updates.Add(new PendingUpdate(index, kind, recordId, stamp));
        }

        if (!matched)
            NoteDropped(by.Trim(), kind, recordId, WorkEffortWindow.LocalDay(stamp), WorkEffortCalcLog.NotSelected);
    }

    private void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        if (_updates.Count == 0)
            return;

        var grouped = new Dictionary<GroupKey, UpdateGroup>();
        foreach (var update in _updates)
        {
            var key = new GroupKey(update.Person, update.Kind, update.RecordId);
            if (!grouped.TryGetValue(key, out var group))
            {
                group = new UpdateGroup();
                grouped[key] = group;
            }

            if (!group.Moments.Add(update.Stamp.Ticks))
            {
                NoteDropped(
                    _people[update.Person].DisplayName,
                    update.Kind,
                    update.RecordId,
                    WorkEffortWindow.LocalDay(update.Stamp),
                    WorkEffortCalcLog.DuplicateMerged);
                continue;
            }

            group.Stamps.Add(update.Stamp);
        }

        foreach (var pair in grouped)
        {
            // Raw update moments for Work Type "X Updates", independent of Daily vs Multiple scoring.
            var updateCount = pair.Value.Moments.Count;
            if (_mode == WorkEffortUpdateMode.Multiple)
            {
                foreach (var stamp in pair.Value.Stamps.OrderBy(value => value))
                {
                    AddUpdateCredit(pair.Key.Person, pair.Key.Kind, pair.Key.RecordId, stamp, day: "", updateCount);
                    NoteKept(pair.Key.Person, pair.Key.Kind, pair.Key.RecordId, "updated", stamp, WorkEffortScore.UpdateWeight, WorkEffortCalcLog.EachSave);
                }

                AddUpdate(pair.Key.Person, pair.Key.Kind, updateCount);
            }
            else
            {
                var byDay = new Dictionary<int, DateTime>();
                foreach (var stamp in pair.Value.Stamps.OrderBy(value => value))
                {
                    var day = DayNumber(stamp);
                    if (!byDay.ContainsKey(day))
                        byDay[day] = stamp;
                }

                var chosen = new HashSet<long>();
                foreach (var stamp in byDay.Values.OrderBy(value => value))
                {
                    chosen.Add(stamp.Ticks);
                    var day = WorkEffortWindow.LocalDay(stamp).ToString("yyyy-MM-dd");
                    AddUpdateCredit(pair.Key.Person, pair.Key.Kind, pair.Key.RecordId, stamp, day, updateCount);
                    NoteKept(pair.Key.Person, pair.Key.Kind, pair.Key.RecordId, "updated", stamp, WorkEffortScore.UpdateWeight, WorkEffortCalcLog.OneDailyUpdate);
                }

                foreach (var stamp in pair.Value.Stamps)
                {
                    if (chosen.Contains(stamp.Ticks))
                        continue;
                    NoteDropped(
                        _people[pair.Key.Person].DisplayName,
                        pair.Key.Kind,
                        pair.Key.RecordId,
                        WorkEffortWindow.LocalDay(stamp),
                        WorkEffortCalcLog.DailyAlready);
                }

                AddUpdate(pair.Key.Person, pair.Key.Kind, byDay.Count);
            }
        }

        _updates.Clear();
        _life.Clear();
    }

    private void AddUpdate(int person, WorkEffortKind kind, int credit)
    {
        if (credit <= 0)
            return;
        var slot = person * Width;
        var offset = kind switch
        {
            WorkEffortKind.RequestedItem => 5,
            WorkEffortKind.Interaction => 8,
            _ => 2
        };
        _counts[slot + offset] += credit;
    }

    private void AddLifeCredit(int person, WorkEffortKind kind, string recordId, WorkEffortMetric metric, DateTime when)
    {
        var label = LabelOf(kind, recordId);
        var who = _people[person];
        var whenLocal = WorkEffortWindow.LocalStamp(when);
        _credits.Add(new WorkEffortCredit(
            who.SysId,
            who.DisplayName,
            recordId,
            label.Number,
            label.Title,
            kind,
            metric,
            whenLocal));
        if (_notes is null)
            return;
        var amount = LifeAmount(kind);
        var (action, rule) = metric switch
        {
            WorkEffortMetric.Opened => ("opened", WorkEffortCalcLog.OpenWeight),
            WorkEffortMetric.Closed => ("resolved", WorkEffortCalcLog.CloseAsResolve),
            _ => ("resolved", WorkEffortCalcLog.ResolveWeight)
        };
        NoteKept(person, kind, recordId, action, whenLocal, amount, rule);
    }

    private void AddUpdateCredit(int person, WorkEffortKind kind, string recordId, DateTime when, string day, int updateCount)
    {
        var label = LabelOf(kind, recordId);
        var who = _people[person];
        _credits.Add(new WorkEffortCredit(
            who.SysId,
            who.DisplayName,
            recordId,
            label.Number,
            label.Title,
            kind,
            WorkEffortMetric.Updated,
            when,
            day,
            updateCount));
    }

    private void RememberLabel(WorkEffortKind kind, string recordId, string? number, string? title)
    {
        var key = (int)kind + "\n" + recordId;
        if (_labels.TryGetValue(key, out var existing))
        {
            _labels[key] = new TouchLabel(
                string.IsNullOrWhiteSpace(existing.Number) ? (number ?? "").Trim() : existing.Number,
                string.IsNullOrWhiteSpace(existing.Title) ? (title ?? "").Trim() : existing.Title);
            return;
        }

        _labels[key] = new TouchLabel((number ?? "").Trim(), (title ?? "").Trim());
    }

    private TouchLabel LabelOf(WorkEffortKind kind, string recordId)
    {
        var key = (int)kind + "\n" + recordId;
        return _labels.TryGetValue(key, out var label) ? label : new TouchLabel("", "");
    }

    private void RememberPlace(WorkEffortKind kind, string recordId, string? location)
    {
        if (_places is null)
            return;
        var key = (int)kind + "\n" + recordId;
        var place = (location ?? "").Trim();
        if (_places.TryGetValue(key, out var existing) && existing.Length > 0)
            return;
        _places[key] = place;
    }

    private string PlaceOf(WorkEffortKind kind, string recordId)
    {
        if (_places is null)
            return "";
        var key = (int)kind + "\n" + recordId;
        return _places.TryGetValue(key, out var place) && place.Length > 0 ? place : "(blank)";
    }

    private void NoteIfOutside(WorkEffortTouch touch, DateTime? moment)
    {
        if (moment is not DateTime value || InWindow(value))
            return;
        NoteDropped("", touch.Kind, touch.SysId, WorkEffortWindow.LocalDay(value), WorkEffortCalcLog.OutsideWindow);
    }

    private void NoteKept(int person, WorkEffortKind kind, string recordId, string action, DateTime when, decimal amount, string rule)
    {
        if (_notes is null)
            return;
        var who = _people[person];
        var label = LabelOf(kind, recordId);
        _notes.Kept.Add(new WorkEffortKeptCredit(
            who.SysId,
            who.DisplayName,
            WorkEffortCalcLog.TicketLabel(label.Number, recordId),
            recordId,
            WorkEffortCalcLog.TableName(kind),
            action,
            WorkEffortWindow.LocalDay(when),
            amount,
            PlaceOf(kind, recordId),
            rule));
    }

    private void NoteDropped(string personName, WorkEffortKind kind, string recordId, DateOnly? day, string reason)
    {
        if (_notes is null)
            return;
        var label = LabelOf(kind, recordId);
        _notes.Dropped.Add(new WorkEffortScoreDrop(
            personName,
            WorkEffortCalcLog.TicketLabel(label.Number, recordId),
            recordId,
            WorkEffortCalcLog.TableName(kind),
            PlaceOf(kind, recordId),
            day,
            reason));
    }

    private static decimal LifeAmount(WorkEffortKind kind) => kind switch
    {
        WorkEffortKind.RequestedItem => WorkEffortScore.RequestedItemWeight,
        WorkEffortKind.Interaction => WorkEffortScore.InteractionWeight,
        _ => WorkEffortScore.IncidentWeight
    };

    private static int DayNumber(DateTime moment)
    {
        var day = WorkEffortWindow.LocalDay(moment);
        return day.Year * 10000 + day.Month * 100 + day.Day;
    }

    private bool InWindow(DateTime? moment) =>
        moment is DateTime value && _window.Contains(value);

    private static bool Same(string? left, string? right)
    {
        var a = (left ?? "").Trim();
        var b = (right ?? "").Trim();
        return a.Length > 0 && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct LifeKey(int Person, WorkEffortKind Kind, string RecordId, int Day);

    private readonly record struct PendingUpdate(int Person, WorkEffortKind Kind, string RecordId, DateTime Stamp);

    private readonly record struct GroupKey(int Person, WorkEffortKind Kind, string RecordId);

    private readonly record struct TouchLabel(string Number, string Title);

    private sealed class UpdateGroup
    {
        public HashSet<long> Moments { get; } = [];

        public List<DateTime> Stamps { get; } = [];
    }
}
