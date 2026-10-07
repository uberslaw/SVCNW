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
}

/// <summary>
/// Counts one table page at a time. Record ids already counted stop growing at the safety cap.
/// Update moments are kept only until the page is folded, then dropped.
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
    private bool _finished;

    public WorkEffortAttempt(
        IReadOnlyList<WorkEffortPerson> people,
        WorkEffortWindow window,
        int safetyCap,
        WorkEffortUpdateMode mode = WorkEffortUpdateMode.Daily)
    {
        ArgumentNullException.ThrowIfNull(people);
        _people = people as WorkEffortPerson[] ?? people.ToArray();
        _window = window;
        _mode = mode;
        _cap = safetyCap < 1 ? 1 : safetyCap;
        _counts = new int[_people.Length * Width];
        _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            return;

        for (var index = 0; index < _people.Length; index++)
        {
            var person = _people[index];
            var opened = InWindow(touch.OpenedAt) && Same(person.SysId, touch.OpenedBySysId);
            var resolved = InWindow(touch.ResolvedAt) && Same(person.SysId, touch.ResolvedBySysId);
            var closed = InWindow(touch.ClosedAt) && Same(person.SysId, touch.ClosedBySysId);
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
                    if (opened) _counts[slot + 3]++;
                    if (completed) _counts[slot + 4]++;
                    break;
                case WorkEffortKind.Interaction:
                    if (opened) _counts[slot + 6]++;
                    if (completed) _counts[slot + 7]++;
                    break;
                default:
                    if (opened) _counts[slot]++;
                    if (completed) _counts[slot + 1]++;
                    break;
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
            return;
        var stamp = WorkEffortWindow.LocalStamp(moment);
        var day = DayNumber(stamp);
        for (var index = 0; index < _people.Length; index++)
        {
            var person = _people[index];
            if (!Same(person.UserName, by) && !Same(person.SysId, by))
                continue;
            if (_life.Contains(new LifeKey(index, kind, recordId, day)))
                continue;
            _updates.Add(new PendingUpdate(index, kind, recordId, stamp));
        }
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
                continue;
            group.Days.Add(DayNumber(update.Stamp));
        }

        foreach (var pair in grouped)
        {
            var credit = _mode == WorkEffortUpdateMode.Multiple ? pair.Value.Moments.Count : pair.Value.Days.Count;
            AddUpdate(pair.Key.Person, pair.Key.Kind, credit);
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

    private sealed class UpdateGroup
    {
        public HashSet<long> Moments { get; } = [];

        public HashSet<int> Days { get; } = [];
    }
}
