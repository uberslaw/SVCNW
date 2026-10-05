using System.Globalization;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Alerts;

/// <summary>
/// Alert classes the minimized widget can show. Add a member and a catalog entry
/// when a later class (SLA, unassigned age) needs a circle of its own.
/// </summary>
public enum AlertKind
{
    AssignedToMe,
    WatchedGroup
}

public static class AlertCatalog
{
    public static IReadOnlyList<AlertKind> All { get; } =
    [
        AlertKind.AssignedToMe,
        AlertKind.WatchedGroup
    ];

    public static string Title(AlertKind kind) => kind switch
    {
        AlertKind.AssignedToMe => "Assigned to me",
        AlertKind.WatchedGroup => "Group queue",
        _ => kind.ToString()
    };

    public static string AutomationName(AlertKind kind, int count) =>
        Title(kind) + ", " + count.ToString(CultureInfo.InvariantCulture);
}

public sealed record AlertRecord(
    AlertKind Kind,
    DeskSection Section,
    string SysId,
    string Number,
    string Title,
    string State,
    string Group,
    string Location,
    string Updated);

public sealed record AlertBucket(IReadOnlyList<AlertRecord> Rows, int TotalCount)
{
    public static AlertBucket Empty { get; } = new([], 0);
}

public sealed class AlertSnapshot
{
    private readonly Dictionary<AlertKind, AlertBucket> _buckets;

    public AlertSnapshot(IReadOnlyDictionary<AlertKind, AlertBucket> buckets)
    {
        _buckets = new Dictionary<AlertKind, AlertBucket>();
        foreach (var kind in AlertCatalog.All)
            _buckets[kind] = buckets.TryGetValue(kind, out var bucket) ? bucket : AlertBucket.Empty;
    }

    public AlertBucket Bucket(AlertKind kind) =>
        _buckets.TryGetValue(kind, out var bucket) ? bucket : AlertBucket.Empty;

    public int Count(AlertKind kind) => Bucket(kind).TotalCount;

    public IReadOnlyDictionary<AlertKind, int> Counts
    {
        get
        {
            var counts = new Dictionary<AlertKind, int>();
            foreach (var kind in AlertCatalog.All)
                counts[kind] = Count(kind);
            return counts;
        }
    }
}

public sealed record AlertSearch(string UserSysId, string? GroupName, IReadOnlyList<string> Locations);

public sealed class AlertAttention : EventArgs
{
    public bool PlaySound { get; init; }
}
