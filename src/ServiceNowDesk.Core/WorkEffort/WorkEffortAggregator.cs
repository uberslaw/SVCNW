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
/// Counts one table page at a time. The only retained row state is the set of
/// record ids already counted, and that set stops growing at the safety cap.
/// </summary>
public sealed class WorkEffortAttempt
{
    public const int Width = 9;

    private readonly WorkEffortPerson[] _people;
    private readonly WorkEffortWindow _window;
    private readonly int _cap;
    private readonly int[] _counts;
    private readonly HashSet<string> _seen;

    public WorkEffortAttempt(IReadOnlyList<WorkEffortPerson> people, WorkEffortWindow window, int safetyCap)
    {
        ArgumentNullException.ThrowIfNull(people);
        _people = people as WorkEffortPerson[] ?? people.ToArray();
        _window = window;
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
        if (totals.Length != _counts.Length)
            throw new ArgumentException("Count width does not match the team.", nameof(totals));
        for (var index = 0; index < _counts.Length; index++)
            totals[index] += _counts[index];
    }

    public IReadOnlyList<WorkEffortRow> ToRows() => ToRows(_people, _counts);

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
            var updated = InWindow(touch.UpdatedAt)
                && (Same(person.UserName, touch.UpdatedBy) || Same(person.SysId, touch.UpdatedBy))
                && !opened
                && !resolved
                && !closed;
            var completed = resolved || closed;
            var slot = index * Width;
            switch (touch.Kind)
            {
                case WorkEffortKind.RequestedItem:
                    if (opened) _counts[slot + 3]++;
                    if (completed) _counts[slot + 4]++;
                    if (updated) _counts[slot + 5]++;
                    break;
                case WorkEffortKind.Interaction:
                    if (opened) _counts[slot + 6]++;
                    if (completed) _counts[slot + 7]++;
                    if (updated) _counts[slot + 8]++;
                    break;
                default:
                    if (opened) _counts[slot]++;
                    if (completed) _counts[slot + 1]++;
                    if (updated) _counts[slot + 2]++;
                    break;
            }
        }
    }

    private bool InWindow(DateTime? moment) =>
        moment is DateTime value && _window.Contains(value);

    private static bool Same(string? left, string? right)
    {
        var a = (left ?? "").Trim();
        var b = (right ?? "").Trim();
        return a.Length > 0 && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }
}
