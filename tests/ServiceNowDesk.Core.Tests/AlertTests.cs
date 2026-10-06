using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class AlertTests
{
    [Fact]
    public void CountIncreaseArmsAcknowledgementUntilTheCountRisesAgain()
    {
        var watch = new AlertWatchState();
        var first = watch.Observe(Counts(assigned: 2, group: 1));

        Assert.Contains(AlertKind.AssignedToMe, first.Increased);
        Assert.Contains(AlertKind.WatchedGroup, first.Increased);
        Assert.True(watch.IsUnacknowledged(AlertKind.AssignedToMe));
        Assert.True(watch.IsUnacknowledged(AlertKind.WatchedGroup));
        Assert.True(watch.AnyUnacknowledged);

        watch.Acknowledge();
        Assert.False(watch.IsUnacknowledged(AlertKind.AssignedToMe));
        Assert.False(watch.IsUnacknowledged(AlertKind.WatchedGroup));
        Assert.Equal(2, watch.AcknowledgedCount(AlertKind.AssignedToMe));
        Assert.Equal(1, watch.AcknowledgedCount(AlertKind.WatchedGroup));

        var equal = watch.Observe(Counts(assigned: 2, group: 1));
        Assert.Empty(equal.Increased);
        Assert.False(watch.AnyUnacknowledged);

        var increased = watch.Observe(Counts(assigned: 3, group: 1));
        Assert.Equal(AlertKind.AssignedToMe, Assert.Single(increased.Increased));
        Assert.True(watch.IsUnacknowledged(AlertKind.AssignedToMe));
        Assert.False(watch.IsUnacknowledged(AlertKind.WatchedGroup));
    }

    [Fact]
    public void DecreaseDoesNotRearmOrCountAsANewAlert()
    {
        var watch = new AlertWatchState();
        watch.Observe(Counts(assigned: 5, group: 4));
        watch.Acknowledge();

        var decreased = watch.Observe(Counts(assigned: 3, group: 4));
        Assert.Empty(decreased.Increased);
        Assert.False(watch.IsUnacknowledged(AlertKind.AssignedToMe));
        Assert.False(watch.IsUnacknowledged(AlertKind.WatchedGroup));
        Assert.Equal(5, watch.AcknowledgedCount(AlertKind.AssignedToMe));
        Assert.Equal(3, watch.CurrentCount(AlertKind.AssignedToMe));

        var stillDown = watch.Observe(Counts(assigned: 2, group: 2));
        Assert.Empty(stillDown.Increased);
        Assert.False(watch.AnyUnacknowledged);
    }

    [Fact]
    public void WatchedGroupQueryQuotesValuesAndSanitizesCarets()
    {
        var query = AlertQueryBuilder.WatchedGroup(
            "Aus DT^active=false",
            ["Gold Coast", "Brisbane^ORpriority=1"]);

        Assert.NotNull(query);
        Assert.Contains("assignment_group.name=\"Aus DT active=false\"", query);
        Assert.Contains("\"Gold Coast\"", query);
        Assert.Contains("\"Brisbane ORpriority=1\"", query);
        Assert.Contains("active=true", query);
        Assert.DoesNotContain("^active=false", query);
        Assert.DoesNotContain("^ORpriority=1", query);
        Assert.Equal("assigned_to=sample-user^active=true^ORDERBYDESCsys_updated_on", AlertQueryBuilder.AssignedToMe("sample-user"));
        Assert.Throws<InvalidOperationException>(() => AlertQueryBuilder.AssignedToMe("user^active=false"));
    }

    [Fact]
    public void BlankGroupOrEmptyLocationsProduceNoGroupQuery()
    {
        Assert.Null(AlertQueryBuilder.WatchedGroup("", ["Brisbane"]));
        Assert.Null(AlertQueryBuilder.WatchedGroup("   ", ["Brisbane"]));
        Assert.Null(AlertQueryBuilder.WatchedGroup("Aus DT - Client Services", []));
        Assert.Null(AlertQueryBuilder.WatchedGroup("Aus DT - Client Services", [" ", "\t"]));
        Assert.Null(AlertQueryBuilder.WatchedGroup("^^^", ["^^^"]));
    }

    [Fact]
    public async Task PracticeAlertsMatchAlexAndTheBrisbaneGroupQueue()
    {
        using var client = new SampleServiceNowClient();
        var user = await client.GetCurrentUserAsync(CancellationToken.None);
        Assert.Equal("Alex Rivera", user.Name);

        var snapshot = await client.GetOpenAlertsAsync(
            new AlertSearch(user.SysId, "Aus DT - Client Services", NotificationPreferences.DefaultLocations),
            CancellationToken.None);

        Assert.Contains(snapshot.Bucket(AlertKind.AssignedToMe).Rows, row => row.Number == "INC0010001" && row.Section == DeskSection.Incidents);
        Assert.Contains(snapshot.Bucket(AlertKind.AssignedToMe).Rows, row => row.Number == "INC0010002");
        Assert.Contains(snapshot.Bucket(AlertKind.AssignedToMe).Rows, row => row.Number == "INC0010006");
        Assert.Contains(snapshot.Bucket(AlertKind.AssignedToMe).Rows, row => row.Number == "REQ0010001" && row.Section == DeskSection.Requests);
        Assert.Contains(snapshot.Bucket(AlertKind.AssignedToMe).Rows, row => row.Number == "RITM0010001" && row.Section == DeskSection.RequestedItems);
        Assert.DoesNotContain(snapshot.Bucket(AlertKind.AssignedToMe).Rows, row => row.Number == "INC0010003");
        Assert.DoesNotContain(snapshot.Bucket(AlertKind.AssignedToMe).Rows, row => row.Number == "RITM0010004");
        Assert.DoesNotContain(snapshot.Bucket(AlertKind.AssignedToMe).Rows, row => row.Section == DeskSection.Knowledge);
        Assert.Equal(5, snapshot.Count(AlertKind.AssignedToMe));

        var group = snapshot.Bucket(AlertKind.WatchedGroup).Rows;
        var brisbane = Assert.Single(group);
        Assert.Equal("INC0010007", brisbane.Number);
        Assert.Equal("Brisbane", brisbane.Location);
        Assert.Equal("Aus DT - Client Services", brisbane.Group);
        Assert.DoesNotContain(group, row => row.Number == "INC0010008");
        Assert.DoesNotContain(group, row => row.Number == "INC0010009");

        var ignoredCase = await client.GetOpenAlertsAsync(
            new AlertSearch(user.SysId, "aus dt - client services", ["brisbane"]),
            CancellationToken.None);
        Assert.Equal("INC0010007", Assert.Single(ignoredCase.Bucket(AlertKind.WatchedGroup).Rows).Number);

        var injected = await client.GetOpenAlertsAsync(
            new AlertSearch(user.SysId, "Aus DT - Client Services", ["Brisbane^ORactive=false"]),
            CancellationToken.None);
        Assert.Empty(injected.Bucket(AlertKind.WatchedGroup).Rows);

        var blank = await client.GetOpenAlertsAsync(new AlertSearch(user.SysId, "", ["Brisbane"]), CancellationToken.None);
        Assert.Empty(blank.Bucket(AlertKind.WatchedGroup).Rows);
        var noCities = await client.GetOpenAlertsAsync(
            new AlertSearch(user.SysId, "Aus DT - Client Services", []),
            CancellationToken.None);
        Assert.Empty(noCities.Bucket(AlertKind.WatchedGroup).Rows);
    }

    [Fact]
    public async Task LiveClientSkipsTheGroupQueryUntilAGroupAndCityAreSet()
    {
        var blankHandler = new StubHandler((_, _) => Api.Json("""{"result":[]}"""));
        using var blankClient = ServiceNowClient.Create(Api.BasicSession(), blankHandler);
        await blankClient.GetOpenAlertsAsync(new AlertSearch("sample-user", "  ", ["Brisbane"]), CancellationToken.None);

        Assert.Equal(3, blankHandler.Calls.Count);
        Assert.All(blankHandler.Calls, call => Assert.DoesNotContain("assignment_group", QueryOf(call.PathAndQuery)));
        Assert.Contains(blankHandler.Calls, call => call.PathAndQuery.Contains("/incident", StringComparison.Ordinal));
        Assert.Contains(blankHandler.Calls, call => call.PathAndQuery.Contains("/sc_request", StringComparison.Ordinal));
        Assert.Contains(blankHandler.Calls, call => call.PathAndQuery.Contains("/sc_req_item", StringComparison.Ordinal));
        Assert.All(blankHandler.Calls, call => Assert.Contains("assigned_to=sample-user", QueryOf(call.PathAndQuery)));

        var handler = new StubHandler((_, _) => Api.Json("""{"result":[]}"""));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        await client.GetOpenAlertsAsync(
            new AlertSearch("sample-user", "Aus DT^active=false", ["Gold Coast", "Brisbane"]),
            CancellationToken.None);

        Assert.Equal(4, handler.Calls.Count);
        var groupQuery = handler.Calls
            .Select(call => QueryOf(call.PathAndQuery))
            .Single(query => query.Contains("assignment_group.name", StringComparison.Ordinal));
        Assert.Contains("assignment_group.name=\"Aus DT active=false\"", groupQuery);
        Assert.Contains("\"Gold Coast\"", groupQuery);
        Assert.Contains("\"Brisbane\"", groupQuery);
        Assert.DoesNotContain("^active=false", groupQuery);
        Assert.Contains("active=true", groupQuery);
    }

    [Fact]
    public void NotificationDefaultsAndConnectionSecretsStayIntact()
    {
        var defaults = NotificationPreferences.From(new DeskSettings());
        Assert.Equal("00:01:00", defaults.JiggleFrequency);
        Assert.Equal(2, defaults.JiggleDurationSeconds);
        Assert.Equal(60, defaults.PollSeconds);
        Assert.True(defaults.MaximizeWhenJiggling);
        Assert.False(defaults.PlaySoundWhenJiggling);
        Assert.True(defaults.PlaySoundOnAlertMetric);
        Assert.Equal("Aus DT - Client Services", defaults.WatchedGroupName);
        Assert.Equal(["Brisbane", "Maroochydore", "Gold Coast", "Townsville", "Cairns"], defaults.OfficeLocations);

        var connection = new ConnectionViewModel();
        connection.Load(new DeskSettings
        {
            InstanceUrl = "https://example.service-now.com",
            AuthMode = ServiceNowAuthMode.OAuthPassword,
            Username = "alex",
            Password = "secret",
            ClientId = "cid",
            ClientSecret = "csecret",
            SessionCookie = "cookie",
            UserToken = "token"
        });
        connection.RememberNotifications(new NotificationPreferences
        {
            WatchedGroupName = "Other",
            OfficeLocations = ["Cairns"]
        });

        var saved = connection.BuildSettings();
        Assert.Equal("https://example.service-now.com", saved.InstanceUrl);
        Assert.Equal(ServiceNowAuthMode.OAuthPassword, saved.AuthMode);
        Assert.Equal("secret", saved.Password);
        Assert.Equal("cid", saved.ClientId);
        Assert.Equal("csecret", saved.ClientSecret);
        Assert.Equal("cookie", saved.SessionCookie);
        Assert.Equal("token", saved.UserToken);
        Assert.Equal("Other", saved.WatchedGroupName);
        Assert.Equal("Cairns", Assert.Single(saved.OfficeLocations!));

        var notifications = new NotificationWorkspaceViewModel();
        notifications.Load(saved);
        notifications.FrequencyText = "";
        notifications.DurationText = "0";
        notifications.PollSecondsText = "5";
        notifications.GroupNameText = "Desk queue";
        notifications.SaveSettingsCommand.Execute(null);

        Assert.Equal("00:01:00", notifications.FrequencyText);
        Assert.Equal("2", notifications.DurationText);
        Assert.Equal("60", notifications.PollSecondsText);
        Assert.Equal("Desk queue", notifications.Committed.WatchedGroupName);
        Assert.Contains("previous value", notifications.SettingsMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WidgetSummaryUpdatesWhenANotificationIsAddedAndTheListToggleKeepsThem()
    {
        var notifications = new NotificationWorkspaceViewModel();
        var watch = new AlertWatchState();

        Assert.Equal("No notifications", notifications.WidgetSummary);
        Assert.Equal("", notifications.NewestTitle);
        Assert.False(notifications.IsWidgetOpen);
        Assert.False(notifications.HasWidgetItems);
        Assert.Empty(notifications.WidgetItems);
        Assert.Equal("Assigned to me 0", notifications.Circles.Single(circle => circle.Kind == AlertKind.AssignedToMe).StatusLabel);
        Assert.Equal("Group queue 0", notifications.Circles.Single(circle => circle.Kind == AlertKind.WatchedGroup).StatusLabel);

        notifications.Apply(BannerSnapshot(
            BannerRow("inc9", "VPN is down", "2026-09-01 09:00:00", AlertKind.AssignedToMe)), watch);

        Assert.Equal("1 unread", notifications.WidgetSummary);
        Assert.Equal("VPN is down", notifications.NewestTitle);
        Assert.Equal("Assigned to me 1", notifications.Circles.Single(circle => circle.Kind == AlertKind.AssignedToMe).StatusLabel);
        Assert.Equal("Group queue 0", notifications.Circles.Single(circle => circle.Kind == AlertKind.WatchedGroup).StatusLabel);
        Assert.False(notifications.IsWidgetOpen);
        Assert.Equal("VPN is down", Assert.Single(notifications.WidgetItems).Title);
        Assert.Equal("VPN is down", Assert.Single(notifications.Sections.Single(section => section.Kind == AlertKind.AssignedToMe).Rows).Title);

        notifications.ToggleWidgetCommand.Execute(null);
        Assert.True(notifications.IsWidgetOpen);
        Assert.Equal("VPN is down", Assert.Single(notifications.WidgetItems).Title);
        Assert.Equal("1 unread", notifications.WidgetSummary);

        notifications.ToggleWidgetCommand.Execute(null);
        Assert.False(notifications.IsWidgetOpen);
        Assert.Equal("VPN is down", Assert.Single(notifications.WidgetItems).Title);
        Assert.Equal("VPN is down", Assert.Single(notifications.Sections.Single(section => section.Kind == AlertKind.AssignedToMe).Rows).Title);

        notifications.Apply(BannerSnapshot(
            BannerRow("inc9", "VPN is down", "2026-09-01 09:00:00", AlertKind.AssignedToMe),
            BannerRow("inc2", "Badge printer is down", "2026-10-02 11:25:00", AlertKind.WatchedGroup)), watch);

        Assert.False(notifications.IsWidgetOpen);
        Assert.Equal("2 unread", notifications.WidgetSummary);
        Assert.Equal("Badge printer is down", notifications.NewestTitle);
        Assert.Collection(
            notifications.WidgetItems,
            item => Assert.Equal("Badge printer is down", item.Title),
            item => Assert.Equal("VPN is down", item.Title));
        Assert.Collection(
            notifications.Sections.SelectMany(section => section.Rows),
            item => Assert.Equal("VPN is down", item.Title),
            item => Assert.Equal("Badge printer is down", item.Title));

        notifications.ToggleWidgetCommand.Execute(null);
        notifications.Apply(BannerSnapshot(
            BannerRow("inc9", "VPN is down", "2026-09-01 09:00:00", AlertKind.AssignedToMe),
            BannerRow("inc2", "Badge printer is down", "2026-10-02 11:25:00", AlertKind.WatchedGroup),
            BannerRow("inc1", "Lobby door is stuck", "2026-10-03 08:15:00", AlertKind.AssignedToMe)), watch);

        Assert.True(notifications.IsWidgetOpen);
        Assert.Equal("3 unread", notifications.WidgetSummary);
        Assert.Equal("Lobby door is stuck", notifications.NewestTitle);
        Assert.Collection(
            notifications.WidgetItems,
            item => Assert.Equal("Lobby door is stuck", item.Title),
            item => Assert.Equal("Badge printer is down", item.Title),
            item => Assert.Equal("VPN is down", item.Title));

        notifications.ToggleWidgetCommand.Execute(null);
        watch.Acknowledge();
        notifications.RefreshAcknowledgement(watch);

        Assert.False(notifications.IsWidgetOpen);
        Assert.Equal("3 active", notifications.WidgetSummary);
        Assert.False(notifications.WidgetHasUnread);
        Assert.Equal("Lobby door is stuck", notifications.NewestTitle);
        Assert.Collection(
            notifications.WidgetItems,
            item => Assert.Equal("Lobby door is stuck", item.Title),
            item => Assert.Equal("Badge printer is down", item.Title),
            item => Assert.Equal("VPN is down", item.Title));
        Assert.Collection(
            notifications.Sections.SelectMany(section => section.Rows),
            item => Assert.Equal("VPN is down", item.Title),
            item => Assert.Equal("Lobby door is stuck", item.Title),
            item => Assert.Equal("Badge printer is down", item.Title));
    }

    private static AlertSnapshot BannerSnapshot(params AlertRecord[] rows)
    {
        var assigned = rows.Where(row => row.Kind == AlertKind.AssignedToMe).ToArray();
        var group = rows.Where(row => row.Kind == AlertKind.WatchedGroup).ToArray();
        return new AlertSnapshot(new Dictionary<AlertKind, AlertBucket>
        {
            [AlertKind.AssignedToMe] = new(assigned, assigned.Length),
            [AlertKind.WatchedGroup] = new(group, group.Length)
        });
    }

    private static AlertRecord BannerRow(string id, string title, string updated, AlertKind kind) => new(
        kind,
        DeskSection.Incidents,
        id,
        id.ToUpperInvariant(),
        title,
        "New",
        "Aus DT - Client Services",
        "Brisbane",
        updated);

    private static Dictionary<AlertKind, int> Counts(int assigned, int group) => new()
    {
        [AlertKind.AssignedToMe] = assigned,
        [AlertKind.WatchedGroup] = group
    };

    private static string QueryOf(string pathAndQuery)
    {
        const string marker = "sysparm_query=";
        var start = pathAndQuery.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var encoded = pathAndQuery[(start + marker.Length)..];
        var end = encoded.IndexOf('&', StringComparison.Ordinal);
        if (end >= 0)
            encoded = encoded[..end];
        return Uri.UnescapeDataString(encoded);
    }
}
