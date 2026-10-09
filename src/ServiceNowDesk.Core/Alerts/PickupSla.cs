using System.Globalization;

namespace ServiceNowDesk.Alerts;

/// <summary>
/// Two-hour "not being picked up" SLA for unassigned incidents in the group queue.
/// Progress is elapsed time since <see cref="WatchedRecord.Opened"/> (<c>opened_at</c>)
/// divided by two hours. The desk shows the worst (highest percent) ticket that still
/// matches the office-scoped unassigned group queue.
/// </summary>
public static class PickupSla
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(2);

    /// <summary>Markers and jiggle bands land on every tenth of the window.</summary>
    public const int TickPercent = 10;

    public const int TickCount = 10;

    public static double Percent(DateTime opened, DateTime now)
    {
        var elapsed = now - opened;
        if (elapsed <= TimeSpan.Zero)
            return 0;
        return elapsed.TotalMilliseconds / Window.TotalMilliseconds * 100d;
    }

    public static double Percent(string? openedText, DateTime now) =>
        TryOpened(openedText, out var opened) ? Percent(opened, now) : 0;

    public static double Percent(WatchedRecord record, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(record);
        return TryOpened(record, out var opened) ? Percent(opened, now) : 0;
    }

    /// <summary>
    /// Band index for threshold tracking: 0 while under 10%, 1 at 10–20%, …, 10 at 100–110%, and so on.
    /// </summary>
    public static int Band(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent) || percent < TickPercent)
            return 0;
        return (int)Math.Floor(percent / TickPercent);
    }

    /// <summary>Fill width for the widget bar, clamped to 0–100% of the track.</summary>
    public static double BarFillPercent(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent) || percent <= 0)
            return 0;
        return percent >= 100 ? 100 : percent;
    }

    public static IReadOnlyList<int> TickMarks()
    {
        var marks = new int[TickCount];
        for (var i = 0; i < TickCount; i++)
            marks[i] = (i + 1) * TickPercent;
        return marks;
    }

    /// <summary>
    /// Bands newly entered when progress moves from <paramref name="previousBand"/> to
    /// <paramref name="currentBand"/>. Crossing 10% yields band 1; crossing 100% yields band 10.
    /// </summary>
    public static IReadOnlyList<int> CrossedBands(int previousBand, int currentBand)
    {
        if (currentBand <= previousBand)
            return [];
        var start = Math.Max(1, previousBand + 1);
        var count = currentBand - start + 1;
        if (count <= 0)
            return [];
        var crossed = new int[count];
        for (var i = 0; i < count; i++)
            crossed[i] = start + i;
        return crossed;
    }

    public static bool TryOpened(WatchedRecord record, out DateTime opened)
    {
        ArgumentNullException.ThrowIfNull(record);
        return TryOpened(record.Opened, out opened);
    }

    public static bool TryOpened(string? openedText, out DateTime opened) =>
        AlertClassifier.TryParseInstant(openedText, out opened);

    /// <summary>
    /// Unassigned, still open, with a parseable opened time, and inside the office list when
    /// offices are supplied (same rule as Unassigned / My Team office-scoped queues).
    /// </summary>
    public static bool IsInScope(WatchedRecord record, IReadOnlyList<string>? officeLocations)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!AlertClassifier.IsStillOpen(record))
            return false;
        if (!string.IsNullOrWhiteSpace(record.AssignedToSysId))
            return false;
        if (!TryOpened(record, out _))
            return false;
        if (officeLocations is not null && !OfficeQueue.Matches(record.Location, officeLocations))
            return false;
        return true;
    }

    public static PickupSlaSnapshot? SelectWorst(
        IEnumerable<WatchedRecord> records,
        DateTime now,
        IReadOnlyList<string>? officeLocations = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        PickupSlaSnapshot? worst = null;
        var total = 0;
        foreach (var record in records)
        {
            if (!IsInScope(record, officeLocations))
                continue;
            total++;
            if (!TryOpened(record, out var opened))
                continue;
            var percent = Percent(opened, now);
            var candidate = new PickupSlaSnapshot(
                record.SysId,
                record.Number,
                record.Title,
                record.Section,
                record.Group,
                record.Location,
                opened,
                percent,
                Band(percent),
                0);
            if (worst is null || IsWorse(candidate, worst))
                worst = candidate;
        }

        return worst is null ? null : worst with { ScopedCount = total };
    }

    public static string FormatLabel(PickupSlaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var percentText = Math.Round(snapshot.Percent, MidpointRounding.AwayFromZero)
            .ToString("0", CultureInfo.InvariantCulture);
        if (snapshot.ScopedCount > 1)
            return snapshot.Number + " · " + percentText + "% · " + snapshot.ScopedCount.ToString(CultureInfo.InvariantCulture) + " unassigned";
        return snapshot.Number + " · " + percentText + "% pickup SLA";
    }

    public static string FormatToolTip(PickupSlaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var percentText = snapshot.Percent.ToString("0.#", CultureInfo.InvariantCulture);
        var opened = snapshot.OpenedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return "Not picked up SLA (2 hours from opened). Worst unassigned in the office group queue: "
            + snapshot.Number
            + " at "
            + percentText
            + "% (opened "
            + opened
            + ").";
    }

    private static bool IsWorse(PickupSlaSnapshot candidate, PickupSlaSnapshot current)
    {
        if (candidate.Percent > current.Percent)
            return true;
        if (candidate.Percent < current.Percent)
            return false;
        return string.Compare(candidate.Number, current.Number, StringComparison.OrdinalIgnoreCase) < 0;
    }
}

public sealed record PickupSlaSnapshot(
    string SysId,
    string Number,
    string Title,
    Models.DeskSection Section,
    string Group,
    string Location,
    DateTime OpenedAt,
    double Percent,
    int Band,
    int ScopedCount);

/// <summary>
/// Remembers the highest pickup-SLA band already shown so each new 10% crossing can jiggle once.
/// The first observation is a baseline and does not jiggle.
/// </summary>
public sealed class PickupSlaWatch
{
    private int _band = -1;

    public int CurrentBand => _band;

    public PickupSlaDecision Observe(PickupSlaSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            _band = -1;
            return PickupSlaDecision.NotDue;
        }

        var band = snapshot.Band;
        if (_band < 0)
        {
            _band = band;
            return PickupSlaDecision.NotDue;
        }

        if (band <= _band)
        {
            _band = band;
            return PickupSlaDecision.NotDue;
        }

        var crossed = PickupSla.CrossedBands(_band, band);
        _band = band;
        return crossed.Count == 0 ? PickupSlaDecision.NotDue : new PickupSlaDecision(crossed);
    }

    public void Reset() => _band = -1;
}

public sealed class PickupSlaDecision
{
    public static PickupSlaDecision NotDue { get; } = new([]);

    public PickupSlaDecision(IReadOnlyList<int> crossedBands)
    {
        CrossedBands = crossedBands ?? [];
    }

    public IReadOnlyList<int> CrossedBands { get; }

    public bool Due => CrossedBands.Count > 0;
}
