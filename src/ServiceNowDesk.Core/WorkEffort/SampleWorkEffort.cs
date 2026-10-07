namespace ServiceNowDesk.WorkEffort;

/// <summary>
/// Fixed practice rows for offline mode. Times are offsets from the local clock passed in,
/// so Today and a longer window stay deterministic with no network.
/// </summary>
public static class SampleWorkEffort
{
    public static WorkEffortPerson SignedIn { get; } = new("sample-user", "Alex Rivera", "alex.rivera");

    public static WorkEffortPerson Jordan { get; } = new("user-jordan", "Jordan Lee", "jordan.lee");

    public static WorkEffortPerson Riley { get; } = new("user-riley", "Riley Chen", "riley.chen");

    public static WorkEffortPerson Sam { get; } = new("user-sam", "Sam Patel", "sam.patel");

    public static IReadOnlyList<WorkEffortMembership> Memberships() =>
    [
        new("group-cs", SignedIn),
        new("group-cs", Jordan),
        new("group-cs", Riley),
        new("group-floor", SignedIn),
        new("group-floor", Jordan),
        new("group-floor", Riley),
        new("group-net", Sam)
    ];

    public static IReadOnlyList<WorkEffortTouch> Touches(DateTime localNow)
    {
        var now = WorkEffortWindow.Clock(localNow);
        var tenDays = now.AddDays(-10);
        var fortyDays = now.AddDays(-40);
        return
        [
            new("inc-today", WorkEffortKind.Incident, "sample-user", now, "sample-user", now, null, null, "alex.rivera", now),
            new("ritm-update", WorkEffortKind.RequestedItem, null, null, null, null, null, null, "alex.rivera", now),
            new("ims-close", WorkEffortKind.Interaction, null, null, null, null, "sample-user", now, "alex.rivera", now),
            new("inc-ten", WorkEffortKind.Incident, "sample-user", tenDays, null, null, null, null, "alex.rivera", tenDays),
            new("inc-forty", WorkEffortKind.Incident, null, null, null, null, null, null, "alex.rivera", fortyDays),
            new("inc-jordan", WorkEffortKind.Incident, null, null, null, null, null, null, "jordan.lee", now),
            new("inc-sam", WorkEffortKind.Incident, "user-sam", now, "user-sam", now, null, null, "sam.patel", now)
        ];
    }

    public static WorkEffortReport Report(WorkEffortScale scale, DateTime localNow)
    {
        var window = WorkEffortWindow.For(scale, localNow);
        var people = WorkEffortRoster.Collect(Memberships(), SignedIn);
        var rows = WorkEffortScore.Build(people, Touches(localNow), window);
        return new WorkEffortReport(rows, WorkEffortWindow.CountsLabel(scale), "");
    }
}
