using ServiceNowDesk.Models;

namespace ServiceNowDesk.Client;

public static class DefaultChoices
{
    public static IReadOnlyList<Choice> IncidentStates { get; } =
    [
        new("1", "New"),
        new("2", "In Progress"),
        new("3", "On Hold"),
        new("6", "Resolved"),
        new("7", "Closed"),
        new("8", "Canceled")
    ];

    public static IReadOnlyList<Choice> Impacts { get; } =
    [
        new("1", "1 - High"),
        new("2", "2 - Medium"),
        new("3", "3 - Low")
    ];

    public static IReadOnlyList<Choice> Urgencies { get; } = Impacts;

    public static IReadOnlyList<Choice> Priorities { get; } =
    [
        new("1", "1 - Critical"),
        new("2", "2 - High"),
        new("3", "3 - Moderate"),
        new("4", "4 - Low"),
        new("5", "5 - Planning")
    ];

    public static IReadOnlyList<Choice> CloseCodes { get; } =
    [
        new("Solved (Permanently)", "Solved (Permanently)"),
        new("Solved (Work Around)", "Solved (Work Around)"),
        new("Solved Remotely (Permanently)", "Solved Remotely (Permanently)"),
        new("Solved Remotely (Work Around)", "Solved Remotely (Work Around)"),
        new("Not Solved (Not Reproducible)", "Not Solved (Not Reproducible)"),
        new("Not Solved (Too Costly)", "Not Solved (Too Costly)"),
        new("Closed/Resolved by Caller", "Closed/Resolved by Caller")
    ];

    public static IReadOnlyList<Choice> ContactTypes { get; } =
    [
        new("phone", "Phone"),
        new("email", "Email"),
        new("walk-in", "Walk-in"),
        new("self-service", "Self-service"),
        new("virtual_agent", "Virtual Agent")
    ];

    public static IReadOnlyList<Choice> Categories { get; } =
    [
        new("inquiry", "Inquiry / Help"),
        new("software", "Software"),
        new("hardware", "Hardware"),
        new("network", "Network"),
        new("database", "Database")
    ];

    public static IReadOnlyList<Choice> HoldReasons { get; } =
    [
        new("awaiting_caller", "Awaiting Caller"),
        new("awaiting_change", "Awaiting Change"),
        new("awaiting_problem", "Awaiting Problem"),
        new("awaiting_vendor", "Awaiting Vendor")
    ];

    public static IReadOnlyList<Choice> RequestStates { get; } =
    [
        new("requested", "Requested"),
        new("in_process", "In Process"),
        new("closed_complete", "Closed Complete"),
        new("closed_incomplete", "Closed Incomplete"),
        new("closed_cancelled", "Closed Cancelled"),
        new("closed_rejected", "Closed Rejected"),
        new("closed_skipped", "Closed Skipped")
    ];

    public static IReadOnlyList<Choice> RequestOutcomes { get; } =
    [
        new("closed_complete", "Closed Complete"),
        new("closed_incomplete", "Closed Incomplete"),
        new("closed_cancelled", "Closed Cancelled"),
        new("closed_rejected", "Closed Rejected")
    ];

    public static IReadOnlyList<Choice> ItemStates { get; } =
    [
        new("1", "Open"),
        new("2", "Work in Progress"),
        new("-5", "Pending"),
        new("3", "Closed Complete"),
        new("4", "Closed Incomplete"),
        new("7", "Closed Skipped")
    ];

    public static IReadOnlyList<Choice> ItemOutcomes { get; } =
    [
        new("3", "Closed Complete"),
        new("4", "Closed Incomplete"),
        new("7", "Closed Skipped")
    ];

    public const string WalkUpType = "walkup";

    public static IReadOnlyList<Choice> InteractionStates { get; } =
    [
        new("new", "New"),
        new("work_in_progress", "Work in Progress"),
        new("on_hold", "On Hold"),
        new("wrap_up", "Wrap Up"),
        new("closed_complete", "Closed Complete"),
        new("closed_abandoned", "Closed Abandoned")
    ];

    public static IReadOnlyList<Choice> InteractionTypes { get; } =
    [
        new(WalkUpType, "Walk-up"),
        new("phone", "Phone"),
        new("chat", "Chat"),
        new("messaging", "Messaging")
    ];

    public static IReadOnlyList<Choice> InteractionOutcomes { get; } =
    [
        new("closed_complete", "Closed Complete"),
        new("closed_abandoned", "Closed Abandoned")
    ];

    public static IReadOnlyList<Choice> For(string table, string element) => (table, element) switch
    {
        ("incident", "state") => IncidentStates,
        ("incident", "impact") => Impacts,
        ("incident", "urgency") => Urgencies,
        ("incident", "priority") => Priorities,
        ("incident", "close_code") => CloseCodes,
        ("incident", "contact_type") => ContactTypes,
        ("incident", "category") => Categories,
        ("incident", "hold_reason") => HoldReasons,
        ("sc_request", "request_state") => RequestStates,
        ("sc_request", "priority") => Priorities,
        ("sc_req_item", "state") => ItemStates,
        ("sc_req_item", "priority") => Priorities,
        ("interaction", "state") => InteractionStates,
        ("interaction", "type") => InteractionTypes,
        _ => []
    };
}

public static class PresetCatalog
{
    public static IReadOnlyList<PresetOption> Incidents { get; } =
    [
        new(AssignmentScope.Mine, ActivityFilter.Open, "My open"),
        new(AssignmentScope.MyGroups, ActivityFilter.Open, "My groups"),
        new(AssignmentScope.Unassigned, ActivityFilter.Open, "Unassigned"),
        new(AssignmentScope.Any, ActivityFilter.Open, "All open"),
        new(AssignmentScope.Any, ActivityFilter.Closed, "Closed"),
        new(AssignmentScope.Mine, ActivityFilter.Any, "All of mine")
    ];

    public static IReadOnlyList<PresetOption> Requests { get; } =
    [
        new(AssignmentScope.Any, ActivityFilter.Open, "Open"),
        new(AssignmentScope.Any, ActivityFilter.Open, "Requested for me", "requested_for=javascript:gs.getUserID()"),
        new(AssignmentScope.Any, ActivityFilter.Open, "Opened by me", "opened_by=javascript:gs.getUserID()"),
        new(AssignmentScope.Any, ActivityFilter.Closed, "Closed"),
        new(AssignmentScope.Any, ActivityFilter.Any, "Recent")
    ];

    public static IReadOnlyList<PresetOption> RequestedItems { get; } = Incidents;

    public static IReadOnlyList<PresetOption> WalkUps { get; } = Incidents;
}

public static class ServiceNowLinks
{
    public static string Record(Uri instance, string table, string sysId)
    {
        var authority = instance.GetLeftPart(UriPartial.Authority);
        return $"{authority}/nav_to.do?uri={Uri.EscapeDataString(table + ".do?sys_id=" + sysId)}";
    }

    public static string Knowledge(Uri instance, string sysId)
    {
        var authority = instance.GetLeftPart(UriPartial.Authority);
        return authority + "/kb_view.do?sys_kb_id=" + Uri.EscapeDataString(sysId);
    }
}

public static class StateTone
{
    public static string ForIncident(string? state) => state switch
    {
        "1" => "new",
        "2" => "progress",
        "3" => "hold",
        "6" => "resolved",
        "7" or "8" => "closed",
        _ => "open"
    };

    public static string ForRequest(string? state) => state switch
    {
        "requested" => "new",
        "in_process" => "progress",
        "closed_complete" => "resolved",
        "closed_incomplete" or "closed_cancelled" or "closed_rejected" or "closed_skipped" => "closed",
        _ => "open"
    };

    public static string ForItem(string? state) => state switch
    {
        "1" or "-5" => "new",
        "2" => "progress",
        "3" => "resolved",
        "4" or "7" => "closed",
        _ => "open"
    };

    public static string ForKnowledge(string? state) =>
        string.Equals(state, "published", StringComparison.OrdinalIgnoreCase) ? "resolved" : "open";

    public static string ForInteraction(string? state) => state switch
    {
        "new" => "new",
        "work_in_progress" => "progress",
        "on_hold" => "hold",
        "wrap_up" => "progress",
        "closed_complete" => "resolved",
        "closed_abandoned" => "closed",
        _ => "open"
    };
}
