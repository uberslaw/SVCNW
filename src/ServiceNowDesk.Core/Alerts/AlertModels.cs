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
        AlertKind.ReturnedWithNotes => "Returned with notes",
        AlertKind.Unattended => "Unattended tickets",
        _ => kind.ToString()
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
    string AssignedToSysId = "");

public sealed record AlertBucket(IReadOnlyList<AlertRecord> Rows, int TotalCount, string Status = "")
{
    public static AlertBucket Empty { get; } = new([], 0);

    public static AlertBucket Failed(string status) => new([], 0, status ?? "");
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
