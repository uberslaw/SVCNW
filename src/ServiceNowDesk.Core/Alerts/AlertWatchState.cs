namespace ServiceNowDesk.Alerts;

public sealed class AlertPollDecision
{
    public static AlertPollDecision None { get; } = new([]);

    public AlertPollDecision(IReadOnlyList<AlertKind> increased)
    {
        Increased = increased;
    }

    public IReadOnlyList<AlertKind> Increased { get; }

    public bool HasIncrease => Increased.Count > 0;
}

/// <summary>
/// Compares the latest open counts with the counts the user last acknowledged.
/// A class is unacknowledged when its current count is above that baseline.
/// Only an increase above the baseline arms a new alert. A decrease does not.
/// </summary>
public sealed class AlertWatchState
{
    private readonly Dictionary<AlertKind, int> _acknowledged = [];
    private readonly Dictionary<AlertKind, int> _current = [];

    public int CurrentCount(AlertKind kind) => _current.GetValueOrDefault(kind);

    public int AcknowledgedCount(AlertKind kind) => _acknowledged.GetValueOrDefault(kind);

    public bool IsUnacknowledged(AlertKind kind) => CurrentCount(kind) > AcknowledgedCount(kind);

    public bool AnyUnacknowledged => AlertCatalog.All.Any(IsUnacknowledged);

    public AlertPollDecision Observe(IReadOnlyDictionary<AlertKind, int> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var increased = new List<AlertKind>();
        foreach (var kind in AlertCatalog.All)
        {
            var next = Math.Max(0, counts.GetValueOrDefault(kind));
            var previous = CurrentCount(kind);
            _current[kind] = next;
            if (next > previous && next > AcknowledgedCount(kind))
                increased.Add(kind);
        }

        return increased.Count == 0 ? AlertPollDecision.None : new AlertPollDecision(increased);
    }

    public void Acknowledge()
    {
        foreach (var kind in AlertCatalog.All)
            _acknowledged[kind] = CurrentCount(kind);
    }

    public void Reset()
    {
        _acknowledged.Clear();
        _current.Clear();
    }
}
