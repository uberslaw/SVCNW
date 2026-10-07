using System.Globalization;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Alerts;

/// <summary>
/// Alert classes shown on the desk bar, the desktop strip, and the notifications dashboard.
/// </summary>
public enum AlertKind
{
    AssignedToMe,
    WatchedGroup,
    SlaBreaching,
    OnHoldPastFollowUp,
    UpdatedByCaller,
    ReturnedWithNotes,
    Unattended
}

public readonly record struct AlertSwatch(string Name, string Hex, string ResourceKey);

public static class AlertCatalog
{
    public static IReadOnlyList<AlertKind> All { get; } =
    [
        AlertKind.AssignedToMe,
        AlertKind.WatchedGroup,
        AlertKind.SlaBreaching,
        AlertKind.OnHoldPastFollowUp,
        AlertKind.UpdatedByCaller,
        AlertKind.ReturnedWithNotes,
        AlertKind.Unattended
    ];

    public static string Title(AlertKind kind) => kind switch
    {
        AlertKind.AssignedToMe => "Assigned to me",
        AlertKind.WatchedGroup => "Group queue",
        AlertKind.SlaBreaching => "SLA breaching",
        AlertKind.OnHoldPastFollowUp => "On hold past follow-up",
        AlertKind.UpdatedByCaller => "Updated by caller",
        AlertKind.ReturnedWithNotes => "Returned by DT",
        AlertKind.Unattended => "Unattended tickets",
        _ => kind.ToString()
    };

    /// <summary>
    /// Hover text for a notification chip. Each sentence follows the query and the classifier.
    /// </summary>
    public static string Description(AlertKind kind) => kind switch
    {
        AlertKind.AssignedToMe =>
            "Open incidents, requests, and request items assigned to you. On hold counts, and resolved, closed, and cancelled do not.",
        AlertKind.WatchedGroup =>
            "Open incidents in the watched group whose location is one of the office cities under Settings. On hold counts, and resolved, closed, and cancelled do not.",
        AlertKind.SlaBreaching =>
            "An open incident, request item, or walk-up that has breached, or is in progress past its planned end, and is assigned to you or unassigned in one of your groups or the watched group.",
        AlertKind.OnHoldPastFollowUp =>
            "An incident, request item, or walk-up assigned to you that is on hold and whose follow-up time has passed. Resolved, closed, and cancelled do not count.",
        AlertKind.UpdatedByCaller =>
            "The person who last updated an open incident, request item, or walk-up is the caller. It counts when the ticket is yours, in the watched group, or unassigned with no group, and the location matches a configured office when offices are set.",
        AlertKind.ReturnedWithNotes =>
            "Open incidents, request items, and walk-ups assigned to you, in one of your groups, or in the watched group at a configured office. The newest comment or work note is from someone other than the caller and the assignee.",
        AlertKind.Unattended =>
            "Open incidents, request items, and walk-ups assigned to you, including on hold, that were last updated at least 24 hours ago. Any update resets the clock, and a missing update time does not count.",
        _ => Title(kind)
    };

    public static AlertSwatch Swatch(AlertKind kind) => kind switch
    {
        AlertKind.WatchedGroup => new("Amber", "#9A6408", "AmberBrush"),
        AlertKind.SlaBreaching => new("Crimson", "#721612", "SlaBrush"),
        AlertKind.OnHoldPastFollowUp => new("Blue", "#14386C", "FollowUpBrush"),
        AlertKind.UpdatedByCaller => new("Violet", "#5B21B6", "CallerUpdateBrush"),
        AlertKind.ReturnedWithNotes => new("Cyan", "#1A90C0", "ReturnedBrush"),
        AlertKind.Unattended => new("Slate", "#2A333C", "UnattendedBrush"),
        _ => new("Green", "#0A635C", "AccentBrush")
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
    string Updated,
    string Assignee = "",
    string AssignedToSysId = "",
    string AssignedOn = "");

public sealed record AlertBucket(IReadOnlyList<AlertRecord> Rows, int TotalCount, string Status = "")
{
    public static AlertBucket Empty { get; } = new([], 0);

    public static AlertBucket Failed(string status) => new([], 0, status ?? "");
}

/// <summary>
/// A failed refresh that comes back empty must not wipe a queue that already has rows.
/// A successful empty result (no status) still replaces the previous list.
/// </summary>
public static class AlertCountKeep
{
    public static AlertSnapshot Apply(AlertSnapshot previous, AlertSnapshot next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        var buckets = new Dictionary<AlertKind, AlertBucket>();
        foreach (var kind in AlertCatalog.All)
        {
            var before = previous.Bucket(kind);
            var after = next.Bucket(kind);
            buckets[kind] = before.TotalCount > 0 && after.TotalCount == 0 && !string.IsNullOrWhiteSpace(after.Status)
                ? new AlertBucket(before.Rows, before.TotalCount, after.Status)
                : after;
        }

        return new AlertSnapshot(buckets);
    }
}

/// <summary>
/// One watched incident, request item, or walk-up, with the fields the extra categories classify on.
/// </summary>
public sealed record WatchedRecord
{
    public required DeskSection Section { get; init; }
    public required string SysId { get; init; }
    public required string Number { get; init; }
    public required string Title { get; init; }
    public string State { get; init; } = "";
    public string StateValue { get; init; } = "";
    public string Group { get; init; } = "";
    public string Location { get; init; } = "";
    public string Updated { get; init; } = "";
    public string UpdatedBy { get; init; } = "";
    public string CallerUserName { get; init; } = "";
    public string AssigneeUserName { get; init; } = "";
    public string AssigneeDisplay { get; init; } = "";
    public string AssignedToSysId { get; init; } = "";
    public string AssignmentGroupSysId { get; init; } = "";
    public DateTime? UpdatedAt { get; init; }
    public string PriorityValue { get; init; } = "";
    public string PriorityLabel { get; init; } = "";
    public string Opened { get; init; } = "";
    public DateTime? FollowUp { get; init; }
    public bool SlaHasBreached { get; init; }
    public string SlaStage { get; init; } = "";
    public DateTime? SlaPlannedEnd { get; init; }
    public string LatestJournalAuthor { get; init; } = "";
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

public sealed record AlertSearch(
    string UserSysId,
    string? GroupName,
    IReadOnlyList<string> Locations,
    IReadOnlyList<string>? TeamMemberIds = null);

public sealed class AlertReport
{
    public AlertReport(AlertSnapshot personal, LeadBoard leads, DailyWorkBoard? daily = null)
    {
        ArgumentNullException.ThrowIfNull(personal);
        ArgumentNullException.ThrowIfNull(leads);
        Personal = personal;
        Leads = leads;
        Daily = daily ?? DailyWorkBoard.Empty;
    }

    public AlertSnapshot Personal { get; }

    public LeadBoard Leads { get; }

    public DailyWorkBoard Daily { get; }
}

public sealed class AlertAttention : EventArgs
{
    public bool PlaySound { get; init; }

    public IReadOnlyList<AlertKind> Increased { get; init; } = [];
}
