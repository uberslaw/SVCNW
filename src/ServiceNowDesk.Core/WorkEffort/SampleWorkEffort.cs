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
        var earlierToday = now.Date.AddHours(9);
        var midToday = now.Date.AddHours(11);
        var twoDaysAgo = now.Date.AddDays(-2).AddHours(10);
        var tenDays = now.AddDays(-10);
        var dayAfterOpen = tenDays.Date.AddDays(1).AddHours(9);
        var fortyDays = now.AddDays(-40);
        return
        [
            new("inc-today", WorkEffortKind.Incident, "sample-user", now, "sample-user", now, null, null, "alex.rivera", now,
                Number: "INC0010001", Title: "VPN drops on campus Wi-Fi"),
            // Alex updated one request item on two local days, and twice on the later day.
            // The header moment is repeated in the history so it is counted once.
            new("ritm-update", WorkEffortKind.RequestedItem, null, null, null, null, null, null, "alex.rivera", now,
                new WorkEffortUpdate[]
                {
                    new("alex.rivera", twoDaysAgo),
                    new("alex.rivera", earlierToday),
                    new("alex.rivera", now)
                },
                Number: "RITM0010002", Title: "Laptop docking station"),
            new("ims-close", WorkEffortKind.Interaction, null, null, null, null, "sample-user", now, "alex.rivera", now,
                Number: "IMS0010003", Title: "Badge reader walk-up"),
            // Opened ten days ago. That day's header update is not a second credit. The next day is.
            new("inc-ten", WorkEffortKind.Incident, "sample-user", tenDays, null, null, null, null, "alex.rivera", tenDays,
                new WorkEffortUpdate[]
                {
                    new("alex.rivera", dayAfterOpen)
                },
                Number: "INC0010004", Title: "Printer jam in east wing"),
            new("inc-forty", WorkEffortKind.Incident, null, null, null, null, null, null, "alex.rivera", fortyDays,
                Number: "INC0010005", Title: "Mailbox full notice"),
            // Jordan saved one incident several times on the same local day.
            new("inc-jordan", WorkEffortKind.Incident, null, null, null, null, null, null, "jordan.lee", now,
                new WorkEffortUpdate[]
                {
                    new("jordan.lee", earlierToday),
                    new("jordan.lee", midToday)
                },
                Number: "INC0010006", Title: "Monitor flicker after login"),
            new("inc-sam", WorkEffortKind.Incident, "user-sam", now, "user-sam", now, null, null, "sam.patel", now,
                Number: "INC0010007", Title: "Switch uplink alarm")
        ];
    }

    public static WorkEffortReport Report(WorkEffortScale scale, DateTime localNow)
    {
        var window = WorkEffortWindow.For(scale, localNow);
        var people = WorkEffortRoster.Collect(Memberships(), SignedIn);
        var touches = Touches(localNow);
        var rows = WorkEffortScore.Build(people, touches, window, WorkEffortUpdateMode.Daily);
        return new WorkEffortReport(rows, WorkEffortWindow.CountsLabel(scale), "", new WorkEffortLedger(people, touches, window));
    }

    public static IReadOnlyList<WorkEffortPerson> WithLogins(IReadOnlyList<WorkEffortPerson>? team)
    {
        var normalized = WorkEffortTeam.Normalize(team);
        var known = new Dictionary<string, WorkEffortPerson>(StringComparer.OrdinalIgnoreCase)
        {
            [SignedIn.SysId] = SignedIn,
            [Jordan.SysId] = Jordan,
            [Riley.SysId] = Riley,
            [Sam.SysId] = Sam
        };
        var people = new List<WorkEffortPerson>(normalized.Count);
        foreach (var person in normalized)
        {
            if (!known.TryGetValue(person.SysId, out var match))
            {
                people.Add(person);
                continue;
            }

            var name = person.Name.Length > 0 && !person.Name.Equals(person.SysId, StringComparison.OrdinalIgnoreCase)
                ? person.Name
                : match.Name;
            var user = person.UserName.Length > 0 ? person.UserName : match.UserName;
            people.Add(new WorkEffortPerson(person.SysId, name, user));
        }

        return people;
    }
}
