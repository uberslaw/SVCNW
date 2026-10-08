using System.Net;
using System.Reflection;
using System.Text;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
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
        Assert.Contains("location.name=\"Gold Coast\"", query);
        Assert.Contains("location.name=\"Brisbane ORpriority=1\"", query);
        Assert.Contains("^NQ", query);
        Assert.DoesNotContain("location.nameIN", query);
        Assert.Contains("active=true", query);
        Assert.DoesNotContain("^active=false", query);
        Assert.DoesNotContain("^ORpriority=1", query);
        Assert.Equal("assigned_to=sample-user^active=true^stateNOT IN6,7,8^ORDERBYDESCsys_updated_on", AlertQueryBuilder.AssignedToMe("sample-user"));
        Assert.Contains("stateNOT IN3,4,7", AlertQueryBuilder.AssignedToMe("sample-user", DeskSection.RequestedItems));
        Assert.Contains("request_stateNOT LIKEclosed", AlertQueryBuilder.AssignedToMe("sample-user", DeskSection.Requests));
        Assert.Contains("stateNOT LIKEclosed", AlertQueryBuilder.AssignedToMe("sample-user", DeskSection.WalkUps));
        Assert.DoesNotContain("<", AlertQueryBuilder.AssignedToMe("sample-user"));
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
        Assert.DoesNotContain(snapshot.Bucket(AlertKind.AssignedToMe).Rows, row => row.Number == "INC0010015");
        Assert.DoesNotContain(snapshot.Bucket(AlertKind.SlaBreaching).Rows, row => row.Number == "INC0010015");
        Assert.DoesNotContain(snapshot.Bucket(AlertKind.AssignedToMe).Rows, row => row.Number == "RITM0010004");
        Assert.DoesNotContain(snapshot.Bucket(AlertKind.AssignedToMe).Rows, row => row.Section == DeskSection.Knowledge);
        Assert.Equal(6, snapshot.Count(AlertKind.AssignedToMe));
        Assert.DoesNotContain(snapshot.Bucket(AlertKind.AssignedToMe).Rows, row => row.Number == "INC0010024");

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

        Assert.Contains(blankHandler.Calls, call => call.PathAndQuery.Contains("/incident", StringComparison.Ordinal));
        Assert.Contains(blankHandler.Calls, call => call.PathAndQuery.Contains("/sc_request", StringComparison.Ordinal));
        Assert.Contains(blankHandler.Calls, call => call.PathAndQuery.Contains("/sc_req_item", StringComparison.Ordinal));
        Assert.All(blankHandler.Calls, call => Assert.DoesNotContain("assignment_group", QueryOf(call.PathAndQuery)));
        Assert.DoesNotContain(blankHandler.Calls, call => call.PathAndQuery.Contains("/task_sla", StringComparison.Ordinal));
        Assert.All(blankHandler.Calls, call => AssertScoped(call.PathAndQuery));

        var handler = new StubHandler((_, _) => Api.Json("""{"result":[]}"""));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        await client.GetOpenAlertsAsync(
            new AlertSearch("sample-user", "Aus DT^active=false", ["Gold Coast", "Brisbane"]),
            CancellationToken.None);

        var groupQuery = handler.Calls
            .Select(call => QueryOf(call.PathAndQuery))
            .First(query => query.Contains("assignment_group.name", StringComparison.Ordinal));
        Assert.Contains("assignment_group.name=\"Aus DT active=false\"", groupQuery);
        Assert.Contains("location.name=\"Gold Coast\"", groupQuery);
        Assert.Contains("location.name=\"Brisbane\"", groupQuery);
        Assert.Contains("location.name=\"Brisbane Office\"", groupQuery);
        Assert.Contains("^NQ", groupQuery);
        Assert.DoesNotContain("location.nameIN", groupQuery);
        Assert.DoesNotContain("^active=false", groupQuery);
        Assert.Contains("active=true", groupQuery);
    }

    [Fact]
    public void NotificationDefaultsAndConnectionSecretsStayIntact()
    {
        var defaults = NotificationPreferences.From(new DeskSettings());
        Assert.Equal("00:01:00", defaults.JiggleFrequency);
        Assert.Equal(2, defaults.JiggleDurationSeconds);
        Assert.Equal(3d, defaults.JiggleSpeed);
        Assert.Equal(DesktopWidgetWhen.WhileOpen, defaults.ShowDesktopWidget);
        Assert.Equal(JiggleWhen.Persistent, defaults.JiggleWhen);
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

        var settingsModel = new NotificationSettingsViewModel();
        settingsModel.Load(saved);
        settingsModel.FrequencyText = "";
        settingsModel.DurationText = "0";
        settingsModel.PollSecondsText = "5";
        settingsModel.GroupNameText = "Desk queue";
        settingsModel.SaveSettingsCommand.Execute(null);

        Assert.Equal("00:01:00", settingsModel.FrequencyText);
        Assert.Equal("2", settingsModel.DurationText);
        Assert.Equal("60", settingsModel.PollSecondsText);
        Assert.Equal("Desk queue", settingsModel.Committed.WatchedGroupName);
        Assert.Contains("previous value", settingsModel.SettingsMessage, StringComparison.OrdinalIgnoreCase);

        var dashboard = new NotificationWorkspaceViewModel();
        Assert.Null(dashboard.GetType().GetProperty("FrequencyText"));
        Assert.Null(dashboard.GetType().GetProperty("DurationText"));
        Assert.Null(dashboard.GetType().GetProperty("SoundPath"));
        Assert.Null(dashboard.GetType().GetProperty("GroupNameText"));
        Assert.Null(dashboard.GetType().GetProperty("LocationsText"));
        Assert.Null(dashboard.GetType().GetProperty("SaveSettingsCommand"));
        Assert.NotNull(settingsModel.GetType().GetProperty("FrequencyText"));
        Assert.NotNull(settingsModel.GetType().GetProperty("DurationText"));
        Assert.NotNull(settingsModel.GetType().GetProperty("SoundPath"));
        Assert.NotNull(settingsModel.GetType().GetProperty("GroupNameText"));
        Assert.NotNull(settingsModel.GetType().GetProperty("LocationsText"));
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

    [Fact]
    public void SelectingAQueueFiltersTheDashboardAndARowKeepsItsTarget()
    {
        var notifications = new NotificationWorkspaceViewModel();
        var watch = new AlertWatchState();
        notifications.Apply(BannerSnapshot(
            BannerRow("inc9", "VPN is down", "2026-09-01 09:00:00", AlertKind.AssignedToMe, DeskSection.Incidents),
            BannerRow("ritm1", "New laptop", "2026-09-02 09:00:00", AlertKind.AssignedToMe, DeskSection.RequestedItems),
            BannerRow("ims1", "Lobby visitor", "2026-09-03 09:00:00", AlertKind.AssignedToMe, DeskSection.WalkUps),
            BannerRow("inc2", "Badge printer is down", "2026-10-02 11:25:00", AlertKind.WatchedGroup, DeskSection.Incidents)), watch);

        notifications.SelectQueueCommand.Execute(AlertKind.AssignedToMe);
        Assert.Equal(AlertKind.AssignedToMe, notifications.SelectedQueue);
        Assert.Collection(
            notifications.DashboardRows,
            row => AssertDashboardRow(row, "inc9", "VPN is down", "Assigned to me", DeskSection.Incidents),
            row => AssertDashboardRow(row, "ritm1", "New laptop", "Assigned to me", DeskSection.RequestedItems),
            row => AssertDashboardRow(row, "ims1", "Lobby visitor", "Assigned to me", DeskSection.WalkUps));
        Assert.DoesNotContain(notifications.DashboardRows, row => row.Kind == AlertKind.WatchedGroup);

        notifications.SelectQueueCommand.Execute(AlertKind.WatchedGroup);
        Assert.Equal(AlertKind.WatchedGroup, notifications.SelectedQueue);
        var group = Assert.Single(notifications.DashboardRows);
        AssertDashboardRow(group, "inc2", "Badge printer is down", "Group queue", DeskSection.Incidents);
        Assert.Collection(
            notifications.Sections.Single(section => section.Kind == AlertKind.AssignedToMe).Rows,
            row => Assert.Equal("inc9", row.SysId),
            row => Assert.Equal("ritm1", row.SysId),
            row => Assert.Equal("ims1", row.SysId));
    }

    private static void AssertDashboardRow(AlertRow row, string sysId, string title, string queue, DeskSection section)
    {
        Assert.Equal(sysId, row.SysId);
        Assert.Equal(title, row.Title);
        Assert.Equal("New", row.State);
        Assert.Equal("Aus DT - Client Services", row.Group);
        Assert.Equal(queue, row.QueueLabel);
        var notifications = new NotificationWorkspaceViewModel();
        var target = notifications.Resolve(row);
        Assert.NotNull(target);
        Assert.Equal(section, target.Value.Section);
        Assert.Equal(sysId, target.Value.SysId);
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

    private static AlertRecord BannerRow(string id, string title, string updated, AlertKind kind, DeskSection section = DeskSection.Incidents) => new(
        kind,
        section,
        id,
        id.ToUpperInvariant(),
        title,
        "New",
        "Aus DT - Client Services",
        "Brisbane",
        updated);

    [Fact]
    public void TenSecondFrequencyIsTenSecondsAndAPollDoesNotResetIt()
    {
        Assert.True(NotificationPreferences.TryParseFrequency("00:00:10", out var parsed));
        Assert.Equal(TimeSpan.FromSeconds(10), parsed);

        var settings = new NotificationSettingsViewModel();
        settings.FrequencyText = "00:00:10";
        settings.SaveSettingsCommand.Execute(null);
        Assert.Equal("00:00:10", settings.Committed.JiggleFrequency);
        Assert.Equal(TimeSpan.FromSeconds(10), settings.ActiveJiggleInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), settings.Committed.JiggleInterval);

        var schedule = new AlertJiggleSchedule();
        Assert.True(schedule.Arm(settings.ActiveJiggleInterval));
        Assert.Equal(TimeSpan.FromSeconds(10), schedule.Interval);
        Assert.False(schedule.Arm(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, schedule.ArmCount);
        Assert.True(schedule.IsRunning);
    }

    [Fact]
    public void JiggleFrequencyCapsAtOneHour()
    {
        Assert.Equal(TimeSpan.FromHours(1), NotificationPreferences.MaximumFrequency);
        Assert.Equal("01:00:00", NotificationPreferences.MaximumFrequencyText);
        Assert.True(NotificationPreferences.TryParseFrequency("01:00:00", out var oneHour));
        Assert.Equal(TimeSpan.FromHours(1), oneHour);
        Assert.Equal("01:00:00", NotificationPreferences.FormatFrequency(TimeSpan.FromHours(2)));

        var settings = new NotificationSettingsViewModel();
        settings.FrequencyText = "01:00:00";
        settings.SaveSettingsCommand.Execute(null);
        Assert.Equal("01:00:00", settings.Committed.JiggleFrequency);
        Assert.Equal(TimeSpan.FromHours(1), settings.ActiveJiggleInterval);
        Assert.Contains("saved", settings.SettingsMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cannot exceed", settings.SettingsMessage, StringComparison.OrdinalIgnoreCase);

        settings.FrequencyText = "01:00:01";
        settings.SaveSettingsCommand.Execute(null);
        Assert.Equal("01:00:00", settings.Committed.JiggleFrequency);
        Assert.Equal("01:00:00", settings.FrequencyText);
        Assert.Equal(TimeSpan.FromHours(1), settings.ActiveJiggleInterval);
        Assert.Contains("cannot exceed 01:00:00", settings.SettingsMessage, StringComparison.Ordinal);
        Assert.Contains("previous value", settings.SettingsMessage, StringComparison.OrdinalIgnoreCase);

        settings.FrequencyText = "02:00:00";
        settings.SaveSettingsCommand.Execute(null);
        Assert.Equal("01:00:00", settings.Committed.JiggleFrequency);
        Assert.Equal("01:00:00", settings.FrequencyText);
        Assert.Contains("cannot exceed 01:00:00", settings.SettingsMessage, StringComparison.Ordinal);

        var loaded = NotificationPreferences.From(new DeskSettings { JiggleFrequency = "03:30:00" });
        Assert.Equal("01:00:00", loaded.JiggleFrequency);
        Assert.Equal(TimeSpan.FromHours(1), loaded.JiggleInterval);

        var fromFile = DeskSettingsFile.Deserialize(
            """{"JiggleFrequency":"02:15:00"}""",
            text => text ?? "");
        Assert.Equal("01:00:00", fromFile.JiggleFrequency);
        Assert.Equal("01:00:00", NotificationPreferences.From(fromFile).JiggleFrequency);
    }

    [Fact]
    public void HoverOpensTheBarAndLeavingReturnsToTheIndicator()
    {
        var motion = new AlertWidgetMotion();
        Assert.True(motion.IndicatorAtRest);
        Assert.False(motion.BarOpen);

        motion.SetPointerOver(true);
        Assert.True(motion.BarOpen);
        Assert.False(motion.IndicatorAtRest);

        motion.SetPointerOver(false);
        Assert.True(motion.IndicatorAtRest);
        Assert.False(motion.BarOpen);

        motion.SetTimerDrop(true);
        Assert.True(motion.BarOpen);
        motion.SetPointerOver(true);
        motion.SetTimerDrop(false);
        Assert.True(motion.BarOpen);
        motion.SetPointerOver(false);
        Assert.True(motion.IndicatorAtRest);
    }

    [Fact]
    public void OnlyMinimizedSuppressesTheDesktopWidgetWhileTheMainWindowIsOpen()
    {
        Assert.False(AlertJiggleRules.AllowsDesktopWidget(DesktopWidgetWhen.OnlyMinimized, mainWindowMinimized: false));
        Assert.True(AlertJiggleRules.AllowsDesktopWidget(DesktopWidgetWhen.OnlyMinimized, mainWindowMinimized: true));
    }

    [Fact]
    public void WhileOpenAllowsTheDesktopWidgetWhenTheDeskIsOpenOrMinimized()
    {
        Assert.True(AlertJiggleRules.AllowsDesktopWidget(DesktopWidgetWhen.WhileOpen, mainWindowMinimized: false));
        Assert.True(AlertJiggleRules.AllowsDesktopWidget(DesktopWidgetWhen.WhileOpen, mainWindowMinimized: true));
    }

    [Fact]
    public void PersistentModeIsDueOnTheIntervalWhileACountIsUnacknowledged()
    {
        var watch = new AlertWatchState();
        watch.Observe(Counts(2, 1));

        var due = AlertJiggleRules.IntervalDue(JiggleWhen.Persistent, Unacknowledged(watch));
        Assert.True(due.Due);
        Assert.Equal([AlertKind.AssignedToMe, AlertKind.WatchedGroup], due.Causes);

        watch.Observe(Counts(2, 1));
        Assert.True(AlertJiggleRules.IntervalDue(JiggleWhen.Persistent, Unacknowledged(watch)).Due);

        watch.Acknowledge();
        Assert.False(AlertJiggleRules.IntervalDue(JiggleWhen.Persistent, Unacknowledged(watch)).Due);
    }

    [Fact]
    public void NewUntilAcknowledgedIsDueOnAnIncreaseAndNotAgainUntilAnother()
    {
        var watch = new AlertWatchState();
        var first = watch.Observe(Counts(1, 0));
        var due = AlertJiggleRules.IncreaseDue(JiggleWhen.NewUntilAcknowledged, first);
        Assert.True(due.Due);
        Assert.Equal(AlertKind.AssignedToMe, Assert.Single(due.Causes));
        Assert.False(AlertJiggleRules.IntervalDue(JiggleWhen.NewUntilAcknowledged, Unacknowledged(watch)).Due);

        var same = watch.Observe(Counts(1, 0));
        Assert.False(AlertJiggleRules.IncreaseDue(JiggleWhen.NewUntilAcknowledged, same).Due);

        watch.Acknowledge();
        var held = watch.Observe(Counts(1, 0));
        Assert.False(AlertJiggleRules.IncreaseDue(JiggleWhen.NewUntilAcknowledged, held).Due);
        Assert.False(AlertJiggleRules.IntervalDue(JiggleWhen.NewUntilAcknowledged, Unacknowledged(watch)).Due);

        var next = watch.Observe(Counts(2, 0));
        Assert.True(AlertJiggleRules.IncreaseDue(JiggleWhen.NewUntilAcknowledged, next).Due);
        Assert.Equal(AlertKind.AssignedToMe, Assert.Single(next.Increased));
    }

    [Fact]
    public void HighlightedKindsAreExactlyTheKindsThatCausedTheJiggle()
    {
        var watch = new AlertWatchState();
        watch.Observe(new Dictionary<AlertKind, int>
        {
            [AlertKind.AssignedToMe] = 1,
            [AlertKind.WatchedGroup] = 2,
            [AlertKind.SlaBreaching] = 3
        });

        var persistent = AlertJiggleRules.IntervalDue(JiggleWhen.Persistent, Unacknowledged(watch));
        var highlighted = AlertJiggleRules.HighlightedKinds(persistent);
        Assert.Equal(persistent.Causes, highlighted);
        Assert.Equal(
            [AlertKind.AssignedToMe, AlertKind.WatchedGroup, AlertKind.SlaBreaching],
            highlighted);

        var notifications = new NotificationWorkspaceViewModel();
        notifications.ShowJiggle(highlighted);
        Assert.Equal(highlighted, notifications.JiggleHighlight);
        Assert.All(
            notifications.Circles.Where(circle => !highlighted.Contains(circle.Kind)),
            circle => Assert.False(circle.IsJiggleCause));

        var increased = watch.Observe(new Dictionary<AlertKind, int>
        {
            [AlertKind.AssignedToMe] = 4,
            [AlertKind.WatchedGroup] = 2,
            [AlertKind.SlaBreaching] = 3
        });
        var fresh = AlertJiggleRules.IncreaseDue(JiggleWhen.NewUntilAcknowledged, increased);
        var freshHighlight = AlertJiggleRules.HighlightedKinds(fresh);
        Assert.Equal([AlertKind.AssignedToMe], freshHighlight);
        notifications.ShowJiggle(freshHighlight);
        Assert.Equal([AlertKind.AssignedToMe], notifications.JiggleHighlight);
        Assert.False(notifications.Circles.Single(circle => circle.Kind == AlertKind.WatchedGroup).IsJiggleCause);
        Assert.False(notifications.Circles.Single(circle => circle.Kind == AlertKind.SlaBreaching).IsJiggleCause);

        Assert.Empty(AlertJiggleRules.HighlightedKinds(AlertJiggleDecision.NotDue));
        var duringHover = notifications.JiggleHighlight.ToArray();
        Assert.Equal([AlertKind.AssignedToMe], duringHover);

        watch.Acknowledge();
        notifications.RefreshAcknowledgement(watch);
        Assert.Empty(notifications.JiggleHighlight);
        Assert.Empty(AlertJiggleRules.HighlightedKinds(AlertJiggleDecision.NotDue));
    }

    [Fact]
    public void JiggleChoicesPersistWithNotificationPreferences()
    {
        var defaults = NotificationPreferences.From(new DeskSettings());
        Assert.Equal(DesktopWidgetWhen.WhileOpen, defaults.ShowDesktopWidget);
        Assert.Equal(JiggleWhen.Persistent, defaults.JiggleWhen);

        var broken = NotificationPreferences.From(new DeskSettings
        {
            ShowDesktopWidget = (DesktopWidgetWhen)42,
            JiggleWhen = (JiggleWhen)42
        });
        Assert.Equal(DesktopWidgetWhen.WhileOpen, broken.ShowDesktopWidget);
        Assert.Equal(JiggleWhen.Persistent, broken.JiggleWhen);

        var chosen = new NotificationPreferences
        {
            ShowDesktopWidget = DesktopWidgetWhen.OnlyMinimized,
            JiggleWhen = JiggleWhen.NewUntilAcknowledged,
            JiggleFrequency = "00:00:10"
        };
        var practice = new ConnectionViewModel { UseSampleData = true };
        practice.RememberNotifications(chosen);
        var saved = practice.BuildSettings();
        Assert.True(saved.UseSampleData);
        Assert.Equal(DesktopWidgetWhen.OnlyMinimized, saved.ShowDesktopWidget);
        Assert.Equal(JiggleWhen.NewUntilAcknowledged, saved.JiggleWhen);
        Assert.Equal("00:00:10", saved.JiggleFrequency);

        var settings = new NotificationSettingsViewModel();
        settings.Load(saved);
        Assert.Equal(DesktopWidgetWhen.OnlyMinimized, settings.ShowDesktopWidget);
        Assert.Equal(JiggleWhen.NewUntilAcknowledged, settings.JiggleWhen);
        Assert.Equal(DesktopWidgetWhen.OnlyMinimized, settings.ActiveShowDesktopWidget);
        Assert.Equal(JiggleWhen.NewUntilAcknowledged, settings.ActiveJiggleWhen);
        Assert.Equal("00:00:10", settings.FrequencyText);

        settings.ShowDesktopWidget = DesktopWidgetWhen.WhileOpen;
        settings.JiggleWhen = JiggleWhen.Persistent;
        settings.SaveSettingsCommand.Execute(null);
        Assert.Equal(DesktopWidgetWhen.WhileOpen, settings.Committed.ShowDesktopWidget);
        Assert.Equal(JiggleWhen.Persistent, settings.Committed.JiggleWhen);
        Assert.Equal(DesktopWidgetWhen.WhileOpen, settings.ActiveShowDesktopWidget);
        Assert.Equal(JiggleWhen.Persistent, settings.ActiveJiggleWhen);
        Assert.Equal("00:00:10", settings.Committed.JiggleFrequency);
    }

    [Fact]
    public void CategoryLabelsAndColorsStayDistinct()
    {
        Assert.Equal(
            ["Assigned to me", "Group queue", "SLA breaching", "On hold past follow-up", "Updated by caller", "Returned by DT", "Unattended tickets"],
            AlertCatalog.All.Select(AlertCatalog.Title).ToArray());
        Assert.Equal(
            ["Green", "Amber", "Crimson", "Blue", "Violet", "Cyan", "Slate"],
            AlertCatalog.All.Select(kind => AlertCatalog.Swatch(kind).Name).ToArray());
        Assert.Equal(7, AlertCatalog.All.Select(kind => AlertCatalog.Swatch(kind).Hex).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void NotificationChipsExplainEachQueueEvenWhenEmpty()
    {
        var notifications = new NotificationWorkspaceViewModel();
        Assert.Equal(7, notifications.Circles.Count);
        foreach (var circle in notifications.Circles)
        {
            Assert.Equal(0, circle.Count);
            Assert.False(string.IsNullOrWhiteSpace(circle.Description));
            Assert.Equal(AlertCatalog.Description(circle.Kind), circle.Description);
            Assert.DoesNotContain("AlertKind", circle.Description, StringComparison.Ordinal);
            Assert.DoesNotContain("sc_", circle.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("task_sla", circle.Description, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(
            "Open incidents, requests, and request items assigned to you. On hold counts, and resolved, closed, and cancelled do not.",
            AlertCatalog.Description(AlertKind.AssignedToMe));
        Assert.Equal(
            "Open incidents in the watched group whose location is one of the office cities under Settings. On hold counts, and resolved, closed, and cancelled do not.",
            AlertCatalog.Description(AlertKind.WatchedGroup));
        Assert.Equal(
            "An open incident, request item, or walk-up that has breached, or is in progress past its planned end, and is assigned to you or unassigned in one of your groups or the watched group.",
            AlertCatalog.Description(AlertKind.SlaBreaching));
        Assert.Equal(
            "An incident, request item, or walk-up assigned to you that is on hold and whose follow-up time has passed. Resolved, closed, and cancelled do not count.",
            AlertCatalog.Description(AlertKind.OnHoldPastFollowUp));
        Assert.Equal(
            "The person who last updated an open incident, request item, or walk-up is the caller. It counts when the ticket is yours, in the watched group, or unassigned with no group, and the location matches a configured office when offices are set.",
            AlertCatalog.Description(AlertKind.UpdatedByCaller));
        Assert.Equal(
            "Open incidents, request items, and walk-ups assigned to you, in one of your groups, or in the watched group at a configured office. The newest comment or work note is from someone other than the caller and the assignee.",
            AlertCatalog.Description(AlertKind.ReturnedWithNotes));
        Assert.Equal(
            "Open incidents, request items, and walk-ups assigned to you, including on hold, that were last updated at least 24 hours ago. Any update resets the clock, and a missing update time does not count.",
            AlertCatalog.Description(AlertKind.Unattended));
    }

    [Fact]
    public void EachNewCategoryIsClassifiedFromASampleRecord()
    {
        var now = new DateTime(2026, 10, 6, 12, 0, 0);
        var slaScope = new SlaBreachScope("sample-user", ["group-cs"], "Client Services");
        var mine = SampleRecord() with { AssignedToSysId = "sample-user" };
        var slaBreached = mine with { SlaHasBreached = true, SlaStage = "completed" };
        var slaInProgress = mine with { SlaStage = "in_progress", SlaPlannedEnd = now.AddMinutes(-1) };
        var slaStillOpen = mine with { SlaStage = "in_progress", SlaPlannedEnd = now.AddHours(1) };
        Assert.True(AlertClassifier.IsSlaBreaching(slaBreached, now, slaScope));
        Assert.True(AlertClassifier.IsSlaBreaching(slaInProgress, now, slaScope));
        Assert.False(AlertClassifier.IsSlaBreaching(slaStillOpen, now, slaScope));
        Assert.False(AlertClassifier.IsSlaBreaching(mine with { SlaStage = "paused", SlaPlannedEnd = now.AddMinutes(-5) }, now, slaScope));

        var hold = SampleRecord() with { StateValue = "3", State = "On Hold", FollowUp = now.AddHours(-2) };
        Assert.True(AlertClassifier.IsOnHoldPastFollowUp(hold, now));
        Assert.False(AlertClassifier.IsOnHoldPastFollowUp(hold with { FollowUp = now.AddHours(2) }, now));
        Assert.False(AlertClassifier.IsOnHoldPastFollowUp(hold with { StateValue = "2", State = "In Progress" }, now));
        Assert.True(AlertClassifier.IsOnHoldPastFollowUp(hold with { Section = DeskSection.WalkUps, StateValue = "on_hold", State = "On Hold" }, now));
        Assert.False(AlertClassifier.IsOnHold(DeskSection.RequestedItems, "3", "Closed Complete"));

        var scope = new CallerUpdateScope("sample-user", "Aus DT - Client Services", ["Brisbane"]);
        var updated = SampleRecord() with
        {
            UpdatedBy = "jordan.lee",
            CallerUserName = "Jordan.Lee",
            AssignedToSysId = "sample-user",
            Location = "Brisbane"
        };
        Assert.True(AlertClassifier.IsUpdatedByCaller(updated, scope));
        Assert.False(AlertClassifier.IsUpdatedByCaller(updated with { UpdatedBy = "alex.rivera" }, scope));
        Assert.False(AlertClassifier.IsUpdatedByCaller(updated with { UpdatedBy = "" }, scope));
        Assert.False(AlertClassifier.IsUpdatedByCaller(updated with { CallerUserName = "" }, scope));

        var returned = SampleRecord() with
        {
            LatestJournalAuthor = "casey.ng",
            CallerUserName = "jordan.lee",
            AssigneeUserName = ""
        };
        Assert.True(AlertClassifier.IsReturnedWithNotes(returned));
        Assert.True(AlertClassifier.IsReturnedWithNotes(returned with { AssigneeUserName = "   " }));
        Assert.False(AlertClassifier.IsReturnedWithNotes(returned with { LatestJournalAuthor = "jordan.lee" }));
        Assert.False(AlertClassifier.IsReturnedWithNotes(returned with { LatestJournalAuthor = "alex.rivera", AssigneeUserName = "alex.rivera" }));
        Assert.False(AlertClassifier.IsReturnedWithNotes(returned with { LatestJournalAuthor = "" }));
    }

    [Fact]
    public void SlaBreachesAreRebuiltForTheAssigneeOrAnUnassignedGroupTicket()
    {
        var now = new DateTime(2026, 10, 6, 12, 0, 0);
        var scope = new SlaBreachScope("sample-user", ["group-cs"], "Aus DT - Client Services");
        var breached = SampleRecord() with { SlaHasBreached = true, SlaStage = "completed" };

        var assignedToMe = breached with { AssignedToSysId = "sample-user", AssignmentGroupSysId = "group-net", Group = "Network" };
        var unassignedInMyGroup = breached with
        {
            Number = "INC0091002",
            AssignedToSysId = "",
            AssigneeUserName = "",
            AssignmentGroupSysId = "group-cs",
            Group = "Client Services"
        };
        var unassignedInWatchedGroup = breached with
        {
            Number = "INC0091003",
            AssignedToSysId = "",
            AssigneeUserName = "",
            AssignmentGroupSysId = "group-aus",
            Group = "Aus DT - Client Services"
        };
        var colleague = breached with
        {
            Number = "INC0091004",
            AssignedToSysId = "user-jordan",
            AssigneeUserName = "jordan.lee",
            AssignmentGroupSysId = "group-cs",
            Group = "Client Services"
        };
        var resolved = breached with
        {
            Number = "INC0091005",
            AssignedToSysId = "sample-user",
            State = "Resolved",
            StateValue = "6"
        };
        var unassignedNowhere = breached with
        {
            Number = "INC0091006",
            AssignedToSysId = "",
            AssigneeUserName = "",
            AssignmentGroupSysId = "",
            Group = ""
        };

        Assert.True(AlertClassifier.IsSlaBreaching(assignedToMe, now, scope));
        Assert.True(AlertClassifier.IsSlaBreaching(unassignedInMyGroup, now, scope));
        Assert.True(AlertClassifier.IsSlaBreaching(unassignedInWatchedGroup, now, scope));
        Assert.False(AlertClassifier.IsSlaBreaching(colleague, now, scope));
        Assert.False(AlertClassifier.IsSlaBreaching(resolved, now, scope));
        Assert.False(AlertClassifier.IsStillOpen(resolved));
        Assert.False(AlertClassifier.IsSlaBreaching(unassignedNowhere, now, scope));

        var bucket = AlertClassifier.Bucket(
            AlertKind.SlaBreaching,
            [assignedToMe, unassignedInMyGroup, colleague, resolved, unassignedNowhere],
            now,
            scope);
        Assert.Equal(["INC0091001", "INC0091002"], bucket.Rows.Select(row => row.Number).ToArray());
    }

    [Fact]
    public void UpdatedByCallerUsesTheWatchedGroupAssigneeAndOffices()
    {
        var offices = new[] { "Brisbane", "Cairns" };
        var scope = new CallerUpdateScope("sample-user", "Aus DT - Client Services", offices);
        var callerUpdate = SampleRecord() with
        {
            UpdatedBy = "jordan.lee",
            CallerUserName = "jordan.lee",
            Location = "Brisbane"
        };

        var assignedToMe = callerUpdate with
        {
            AssignedToSysId = "sample-user",
            AssignmentGroupSysId = "group-net",
            Group = "Network"
        };
        Assert.True(AlertClassifier.IsUpdatedByCaller(assignedToMe, scope));

        var someoneElseInMainGroup = callerUpdate with
        {
            AssignedToSysId = "user-sam",
            AssignmentGroupSysId = "group-aus",
            Group = "Aus DT - Client Services"
        };
        Assert.True(AlertClassifier.IsUpdatedByCaller(someoneElseInMainGroup, scope));

        var unassignedMainGroup = callerUpdate with
        {
            AssignedToSysId = "",
            AssigneeUserName = "",
            AssignmentGroupSysId = "group-aus",
            Group = "Aus DT - Client Services"
        };
        var unassignedNoGroup = callerUpdate with
        {
            Number = "INC0091002",
            AssignedToSysId = "",
            AssigneeUserName = "",
            AssignmentGroupSysId = "",
            Group = ""
        };
        Assert.True(AlertClassifier.IsUpdatedByCaller(unassignedMainGroup, scope));
        Assert.True(AlertClassifier.IsUpdatedByCaller(unassignedNoGroup, scope));

        var unassignedOtherGroup = callerUpdate with
        {
            AssignedToSysId = "",
            AssigneeUserName = "",
            AssignmentGroupSysId = "group-net",
            Group = "Network"
        };
        Assert.False(AlertClassifier.IsUpdatedByCaller(unassignedOtherGroup, scope));

        var otherPersonOtherGroup = callerUpdate with
        {
            AssignedToSysId = "user-sam",
            AssignmentGroupSysId = "group-net",
            Group = "Network"
        };
        Assert.False(AlertClassifier.IsUpdatedByCaller(otherPersonOtherGroup, scope));

        var rightGroupWrongOffice = someoneElseInMainGroup with { Location = "Sydney" };
        Assert.False(AlertClassifier.IsUpdatedByCaller(rightGroupWrongOffice, scope));

        var noOffices = new CallerUpdateScope("sample-user", "Aus DT - Client Services", []);
        Assert.True(AlertClassifier.IsUpdatedByCaller(rightGroupWrongOffice, noOffices));

        var bucket = AlertClassifier.Bucket(
            AlertKind.UpdatedByCaller,
            [assignedToMe, someoneElseInMainGroup, unassignedOtherGroup, otherPersonOtherGroup, rightGroupWrongOffice],
            DateTime.Now,
            scope);
        Assert.Equal(2, bucket.Rows.Count);
        Assert.Contains(bucket.Rows, row => row.Number == assignedToMe.Number);
        Assert.DoesNotContain(bucket.Rows, row => row.Location == "Sydney");
    }

    [Fact]
    public async Task PracticeDataShowsEachNewCategoryInsideTheWatchedPopulation()
    {
        using var client = new SampleServiceNowClient();
        var user = await client.GetCurrentUserAsync(CancellationToken.None);
        var snapshot = await client.GetOpenAlertsAsync(
            new AlertSearch(user.SysId, "Aus DT - Client Services", NotificationPreferences.DefaultLocations),
            CancellationToken.None);

        var sla = snapshot.Bucket(AlertKind.SlaBreaching).Rows;
        Assert.Contains(sla, row => row.Number == "INC0010010" && row.Section == DeskSection.Incidents && row.Assignee == "Alex Rivera");
        Assert.Contains(sla, row => row.Number == "RITM0010005" && row.Section == DeskSection.RequestedItems);
        Assert.Contains(sla, row => row.Number == "IMS0010003" && row.Section == DeskSection.WalkUps);
        Assert.DoesNotContain(sla, row => row.Number == "INC0010014");
        Assert.DoesNotContain(sla, row => row.Number == "INC0010015");
        Assert.DoesNotContain(sla, row => row.Number == "INC0010016");

        Assert.Empty(snapshot.Bucket(AlertKind.OnHoldPastFollowUp).Rows);

        var unattendedRows = snapshot.Bucket(AlertKind.Unattended).Rows;
        var unattended = unattendedRows.Select(row => row.Number).ToArray();
        Assert.All(unattendedRows, row => Assert.False(string.IsNullOrWhiteSpace(row.Assignee)));
        Assert.Equal("Alex Rivera", unattendedRows.Single(row => row.Number == "INC0010001").Assignee);
        Assert.Contains("INC0010001", unattended);
        Assert.Contains("INC0010002", unattended);
        Assert.Contains("INC0010006", unattended);
        Assert.Contains("RITM0010001", unattended);
        Assert.Contains("IMS0010001", unattended);
        Assert.DoesNotContain("INC0010011", unattended);
        Assert.Contains("INC0010010", unattended);
        Assert.Contains("IMS0010003", unattended);

        var caller = Assert.Single(snapshot.Bucket(AlertKind.UpdatedByCaller).Rows);
        Assert.Equal("INC0010007", caller.Number);

        var returned = Assert.Single(snapshot.Bucket(AlertKind.ReturnedWithNotes).Rows);
        Assert.Equal("INC0010013", returned.Number);
        Assert.Equal(6, snapshot.Count(AlertKind.AssignedToMe));
        Assert.Equal("INC0010007", Assert.Single(snapshot.Bucket(AlertKind.WatchedGroup).Rows).Number);
    }

    [Fact]
    public async Task DeniedTaskSlaLeavesTheOtherCategoriesWorking()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/task_sla", StringComparison.Ordinal))
                return Api.Json("""{"error":{"message":"ACL","detail":"task_sla denied"}}""", System.Net.HttpStatusCode.Forbidden);
            if (path.Contains("/incident", StringComparison.Ordinal))
                return Api.Json(Api.IncidentList());
            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var snapshot = await client.GetOpenAlertsAsync(
            new AlertSearch("sample-user", "Aus DT - Client Services", ["Brisbane"]),
            CancellationToken.None);

        Assert.Equal(AlertQueryBuilder.SlaUnavailableStatus, snapshot.Bucket(AlertKind.SlaBreaching).Status);
        Assert.Equal(0, snapshot.Count(AlertKind.SlaBreaching));
        Assert.Equal(1, snapshot.Count(AlertKind.AssignedToMe));
        Assert.Equal("", snapshot.Bucket(AlertKind.UpdatedByCaller).Status);
        Assert.Equal("", snapshot.Bucket(AlertKind.WatchedGroup).Status);
        Assert.Contains(handler.Calls, call => call.PathAndQuery.Contains("/task_sla", StringComparison.Ordinal)
            && QueryOf(call.PathAndQuery).Contains("taskIN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedWalkUpFollowUpQuerySkipsOnlyThatCategorySource()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var query = request.RequestUri?.Query ?? "";
            if (path.Contains("/interaction", StringComparison.Ordinal) && query.Contains("follow_up", StringComparison.Ordinal))
                return Api.Json("""{"error":{"message":"Invalid query","detail":"Unknown field follow_up"}}""", System.Net.HttpStatusCode.BadRequest);
            if (path.Contains("/incident", StringComparison.Ordinal) && query.Contains("follow_up", StringComparison.Ordinal))
            {
                return Api.Json("""
                    {"result":[{
                      "sys_id":{"value":"inc-hold","display_value":"inc-hold"},
                      "number":{"value":"INC0090001","display_value":"INC0090001"},
                      "short_description":{"value":"Past follow-up","display_value":"Past follow-up"},
                      "state":{"value":"3","display_value":"On Hold"},
                      "assigned_to":{"value":"sample-user","display_value":"Alex Rivera"},
                      "assigned_to.user_name":{"value":"alex.rivera","display_value":"alex.rivera"},
                      "assignment_group":{"value":"group-cs","display_value":"Client Services"},
                      "sys_updated_on":{"value":"2026-10-01 09:00:00","display_value":"2026-10-01 09:00"},
                      "sys_updated_by":{"value":"alex.rivera","display_value":"alex.rivera"},
                      "caller_id.user_name":{"value":"jordan.lee","display_value":"jordan.lee"},
                      "follow_up":{"value":"2020-01-01 00:00:00","display_value":"2020-01-01 00:00"},
                      "active":{"value":"true","display_value":"true"}
                    }]}
                    """);
            }

            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var snapshot = await client.GetOpenAlertsAsync(new AlertSearch("sample-user", "", []), CancellationToken.None);

        var hold = snapshot.Bucket(AlertKind.OnHoldPastFollowUp);
        Assert.Contains("Walk-up follow-up was skipped", hold.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("follow_up", hold.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("INC0090001", Assert.Single(hold.Rows).Number);
        Assert.Equal("", snapshot.Bucket(AlertKind.UpdatedByCaller).Status);
        Assert.Equal("", snapshot.Bucket(AlertKind.SlaBreaching).Status);
        var interactionCalls = handler.Calls.Where(call => call.PathAndQuery.Contains("/interaction", StringComparison.Ordinal)).ToArray();
        Assert.Contains(interactionCalls, call => call.PathAndQuery.Contains("follow_up", StringComparison.Ordinal));
        Assert.Contains(interactionCalls, call => !call.PathAndQuery.Contains("follow_up", StringComparison.Ordinal));
    }

    [Fact]
    public void TaskSlaQueryUsesTheTableShapeWithoutALessThanComparison()
    {
        var one = Assert.Single(AlertQueryBuilder.TaskSlaQueries(["inc-printer"]));
        Assert.Contains("taskINinc-printer", one);
        Assert.Contains("task.sys_class_nameINincident,sc_req_item,interaction", one);
        Assert.Contains("has_breached=true", one);
        Assert.Contains("^NQ", one);
        Assert.Contains("stage=in_progress", one);
        Assert.DoesNotContain("<", one);
        Assert.DoesNotContain("javascript:gs.nowDateTime", one);
        Assert.Empty(AlertQueryBuilder.TaskSlaQueries([]));
        Assert.Empty(AlertQueryBuilder.TaskSlaQueries(null));

        var many = Enumerable.Range(0, 41).Select(index => "task" + index.ToString("00")).ToArray();
        var queries = AlertQueryBuilder.TaskSlaQueries(many);
        Assert.Equal(2, queries.Count);
        Assert.All(queries, query =>
        {
            Assert.DoesNotContain("<", query);
            Assert.Contains("task.sys_class_nameINincident,sc_req_item,interaction", query);
        });
    }

    [Fact]
    public async Task HtmlSlaResponseKeepsTheOtherCategoriesAndExplainsTheGap()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/task_sla", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("<html><body>Not authorized</body></html>", Encoding.UTF8, "text/html")
                };
            }

            if (path.Contains("/incident", StringComparison.Ordinal))
                return Api.Json("{\"result\":[" + WatchedIncident("inc-caller", "INC0092001", "jordan.lee", "jordan.lee") + "]}");
            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(BrowserSession(), handler);
        var rejected = 0;
        client.BrowserSessionRejected += (_, _) => rejected++;
        var snapshot = await client.GetOpenAlertsAsync(new AlertSearch("sample-user", "", []), CancellationToken.None);

        Assert.Equal(0, rejected);
        Assert.Equal(AlertQueryBuilder.SlaUnavailableStatus, snapshot.Bucket(AlertKind.SlaBreaching).Status);
        Assert.Equal(0, snapshot.Count(AlertKind.SlaBreaching));
        Assert.Equal(1, snapshot.Count(AlertKind.AssignedToMe));
        Assert.Equal("INC0092001", Assert.Single(snapshot.Bucket(AlertKind.UpdatedByCaller).Rows).Number);
        Assert.Equal("", snapshot.Bucket(AlertKind.UpdatedByCaller).Status);
        Assert.Equal("", snapshot.Bucket(AlertKind.OnHoldPastFollowUp).Status);
        var slaCall = Assert.Single(handler.Calls, call => call.PathAndQuery.Contains("/task_sla", StringComparison.Ordinal));
        Assert.StartsWith("/api/now/table/task_sla", slaCall.PathAndQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("<", QueryOf(slaCall.PathAndQuery));
        Assert.Contains("task.sys_class_nameINincident,sc_req_item,interaction", QueryOf(slaCall.PathAndQuery));
    }

    [Fact]
    public async Task BreachedTaskSlaJsonProducesAnSlaRow()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/task_sla", StringComparison.Ordinal))
            {
                Assert.Null(request.Headers.Authorization);
                var cookie = Assert.Single(request.Headers.GetValues("Cookie"));
                Assert.Contains("glide_user_session=abc", cookie);
                Assert.Equal("tok-sla", Assert.Single(request.Headers.GetValues("X-UserToken")));
                Assert.StartsWith("/api/now/table/task_sla", path, StringComparison.Ordinal);
                return Api.Json("{\"result\":[" + string.Join(",",
                    SlaRow("inc-breach", breached: true, "completed", "2020-01-01 00:00:00"),
                    SlaRow("inc-late", breached: false, "in_progress", "2020-01-01 00:00:00"),
                    SlaRow("inc-open", breached: false, "in_progress", "2099-01-01 00:00:00")) + "]}");
            }

            if (path.Contains("/incident", StringComparison.Ordinal))
            {
                return Api.Json("{\"result\":[" + string.Join(",",
                    WatchedIncident("inc-breach", "INC0093001", "alex.rivera", "jordan.lee"),
                    WatchedIncident("inc-late", "INC0093002", "alex.rivera", "jordan.lee"),
                    WatchedIncident("inc-open", "INC0093003", "alex.rivera", "jordan.lee")) + "]}");
            }

            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(BrowserSession(), handler);
        var snapshot = await client.GetOpenAlertsAsync(new AlertSearch("sample-user", "", []), CancellationToken.None);

        var sla = snapshot.Bucket(AlertKind.SlaBreaching);
        Assert.Equal("", sla.Status);
        Assert.Contains(sla.Rows, row => row.Number == "INC0093001" && row.Section == DeskSection.Incidents);
        Assert.Contains(sla.Rows, row => row.Number == "INC0093002");
        Assert.DoesNotContain(sla.Rows, row => row.Number == "INC0093003");
        Assert.Equal(3, snapshot.Count(AlertKind.AssignedToMe));
        var slaCall = Assert.Single(handler.Calls, call => call.PathAndQuery.Contains("/task_sla", StringComparison.Ordinal));
        var query = QueryOf(slaCall.PathAndQuery);
        Assert.Contains("taskINinc-breach,inc-late,inc-open", query);
        Assert.Contains("has_breached=true", query);
        Assert.Contains("stage=in_progress", query);
        Assert.Contains("task.sys_class_nameINincident,sc_req_item,interaction", query);
        Assert.DoesNotContain("<", query);
        Assert.DoesNotContain("javascript:gs.nowDateTime", query);
    }

    [Fact]
    public void ReturnedByDtKeepsItsKeyAndSplitsRolesThatUsedToBeOneQuery()
    {
        Assert.Equal("returned-with-notes", HighlightCatalog.ReturnedWithNotes);
        var legend = HighlightCatalog.Find(HighlightCatalog.ReturnedWithNotes);
        Assert.NotNull(legend);
        Assert.Equal("Returned by DT", legend.Title);
        Assert.Contains("Returned by DT", legend.Explanation);
        Assert.Equal("Returned by DT", AlertCatalog.Title(AlertKind.ReturnedWithNotes));

        var groups = Enumerable.Range(0, 40).Select(index => index.ToString("x32")).ToArray();
        var queries = AlertQueryBuilder.PopulationQueries(
            "sample-user",
            groups,
            "Aus DT - Client Services",
            ["Brisbane", "Gold Coast", "Townsville", "Cairns", "Maroochydore"]);
        Assert.All(queries, query => Assert.True(query.Length <= AlertQueryBuilder.MaxQueryLength, query));
        var assigned = Assert.Single(queries, query => query.StartsWith("assigned_to=sample-user", StringComparison.Ordinal) && !query.Contains("assignment_group", StringComparison.Ordinal));
        Assert.Contains("location.name=\"Brisbane\"", assigned);
        Assert.Contains("location.name=\"Brisbane Office\"", assigned);
        Assert.Contains("^NQ", assigned);
        Assert.DoesNotContain("location.nameIN", assigned);
        var groupQueries = queries.Where(query => query.Contains("assignment_groupIN", StringComparison.Ordinal)).ToArray();
        Assert.True(groupQueries.Length > 1);
        Assert.All(groupQueries, query =>
        {
            Assert.DoesNotContain("assigned_to=", query);
            Assert.DoesNotContain("location.name", query);
            Assert.DoesNotContain("^NQ", query);
        });
        Assert.Contains(queries, query => query.Contains("location.name", StringComparison.Ordinal) && query.Contains("assignment_group.name", StringComparison.Ordinal));
        Assert.DoesNotContain(queries, query => query.Contains("location.nameIN", StringComparison.Ordinal));
        var packed = string.Join(",", groupQueries);
        Assert.All(groups, id => Assert.Contains(id, packed, StringComparison.Ordinal));

        var team = Enumerable.Range(0, 30).Select(index => "team" + index.ToString("D28")).ToArray();
        var leads = AlertQueryBuilder.LeadQueries("Aus DT - Client Services", team);
        Assert.DoesNotContain(leads, query => query.Contains("^NQ", StringComparison.Ordinal));
        Assert.Contains(leads, query => query.Contains("assignment_group.name", StringComparison.Ordinal) && !query.Contains("assigned_to", StringComparison.Ordinal));
        Assert.Contains(leads, query => query.Contains("assigned_toIN", StringComparison.Ordinal) && !query.Contains("location.name", StringComparison.Ordinal));
        Assert.All(leads, query => Assert.True(query.Length <= AlertQueryBuilder.MaxQueryLength, query));

        var ids = Enumerable.Range(0, 80).Select(index => index.ToString("x32")).ToArray();
        var journals = AlertQueryBuilder.LatestJournalQueries(ids);
        Assert.True(journals.Count > 1);
        Assert.All(journals, query =>
        {
            Assert.True(query.Length <= AlertQueryBuilder.MaxQueryLength, query);
            Assert.Contains("elementINcomments,work_notes", query);
            Assert.DoesNotContain("^NQ", query);
        });
        Assert.Empty(AlertQueryBuilder.LatestJournalQueries([]));
        Assert.Empty(AlertQueryBuilder.LeadQueries("   ", []));
    }

    [Fact]
    public async Task ReturnedByDtFetchesPersonalRolesSeparatelyFromLeadAndRegional()
    {
        var groups = Enumerable.Range(0, 40).Select(index => index.ToString("x32")).ToArray();
        var memberJson = string.Join(",", groups.Select(id => "{\"group\":{\"value\":\"" + id + "\",\"display_value\":\"Group\"}}"));
        const string team = "cccccccccccccccccccccccccccccccc";
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var query = Uri.UnescapeDataString(request.RequestUri?.Query ?? "");
            if (path.Contains("sys_user_grmember", StringComparison.Ordinal))
                return Api.Json("{\"result\":[" + memberJson + "]}");
            if (path.Contains("sys_journal_field", StringComparison.Ordinal))
            {
                if (query.Contains("inc-mine", StringComparison.Ordinal))
                    return Api.Json(JournalAuthor("inc-mine", "casey.ng"));
                return Api.Json("""{"result":[]}""");
            }

            if (path.Contains("/incident", StringComparison.Ordinal)
                && query.Contains("caller_id", StringComparison.Ordinal)
                && query.Contains("assigned_to=sample-user", StringComparison.Ordinal)
                && !query.Contains("assigned_toIN", StringComparison.Ordinal))
                return Api.Json(PopulationIncident("inc-mine", "INC0094001"));
            if (path.Contains("/incident", StringComparison.Ordinal)
                && query.Contains("assigned_toIN", StringComparison.Ordinal)
                && query.Contains(team, StringComparison.Ordinal))
                return Api.Json(PopulationIncident("inc-lead", "INC0094002", team, "sam.patel"));
            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var report = await client.GetAlertReportAsync(
            new AlertSearch("sample-user", "Aus DT - Client Services", ["Brisbane"], [team]),
            CancellationToken.None);

        var decoded = handler.Calls.Select(call => (call.PathAndQuery, Query: QueryOf(call.PathAndQuery))).ToArray();
        Assert.All(decoded, call => Assert.True(call.Query.Length <= AlertQueryBuilder.MaxQueryLength, call.Query));

        var personal = decoded.Where(call =>
            call.Query.Contains("assigned_to=sample-user", StringComparison.Ordinal)
            && !call.Query.Contains("assigned_toIN", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(personal);
        Assert.All(personal, call =>
        {
            Assert.DoesNotContain(team, call.Query);
            Assert.DoesNotContain("^NQ", call.Query);
            Assert.DoesNotContain("assignment_groupIN", call.Query);
            Assert.DoesNotContain("sysparm_suppress_pagination_header", call.PathAndQuery);
        });

        var groupCalls = decoded.Where(call => call.Query.Contains("assignment_groupIN", StringComparison.Ordinal)).ToArray();
        Assert.True(groupCalls.Length > 1);
        Assert.All(groupCalls, call =>
        {
            Assert.DoesNotContain("assigned_to=", call.Query);
            Assert.DoesNotContain("location.name", call.Query);
            Assert.DoesNotContain("^NQ", call.Query);
            Assert.DoesNotContain(team, call.Query);
        });
        Assert.Contains(groupCalls, call => call.PathAndQuery.Contains("sysparm_suppress_pagination_header=true", StringComparison.Ordinal));

        var regional = decoded.Where(call =>
            call.Query.Contains("assignment_group.name", StringComparison.Ordinal)
            && !call.Query.Contains("location.name", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(regional);
        Assert.All(regional, call =>
        {
            Assert.DoesNotContain("assigned_to=sample-user", call.Query);
            Assert.DoesNotContain("^NQ", call.Query);
        });

        var journals = decoded.Where(call => call.PathAndQuery.Contains("sys_journal_field", StringComparison.Ordinal)).Select(call => call.Query).ToArray();
        Assert.Contains(journals, query => query.Contains("inc-mine", StringComparison.Ordinal) && !query.Contains("inc-lead", StringComparison.Ordinal));
        Assert.Contains(journals, query => query.Contains("inc-lead", StringComparison.Ordinal) && !query.Contains("inc-mine", StringComparison.Ordinal));
        Assert.Equal("INC0094001", Assert.Single(report.Personal.Bucket(AlertKind.ReturnedWithNotes).Rows).Number);
    }

    [Fact]
    public async Task AFailedPopulationQueryKeepsTheRowsFromTheShorterRequest()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var query = Uri.UnescapeDataString(request.RequestUri?.Query ?? "");
            if (path.Contains("sys_user_grmember", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"group":{"value":"group-cs","display_value":"Client Services"}}]}""");
            if (path.Contains("sys_journal_field", StringComparison.Ordinal))
                return Api.Json(JournalAuthor("inc-mine", "casey.ng"));
            if (path.Contains("/incident", StringComparison.Ordinal) && query.Contains("assignment_groupIN", StringComparison.Ordinal))
            {
                return Api.Json(
                    """{"error":{"message":"The requested query is too long to build the response pagination header URLs. Please do one of the following: shorten the sysparm_query, or query without pagination by setting the parameter 'sysparm_suppress_pagination_header' to true, or set 'sysparm_limit' with a value larger than 546 to bypass the need for pagination."}}""",
                    HttpStatusCode.BadRequest);
            }

            if (path.Contains("/incident", StringComparison.Ordinal)
                && query.Contains("caller_id", StringComparison.Ordinal)
                && query.Contains("assigned_to=sample-user", StringComparison.Ordinal))
                return Api.Json(PopulationIncident("inc-mine", "INC0094001"));
            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var snapshot = await client.GetOpenAlertsAsync(new AlertSearch("sample-user", "", []), CancellationToken.None);
        var returned = snapshot.Bucket(AlertKind.ReturnedWithNotes);
        Assert.Equal("INC0094001", Assert.Single(returned.Rows).Number);
        Assert.Contains(ServiceNowException.QueryTooLongMessage, returned.Status);
        Assert.DoesNotContain("pagination header", returned.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sysparm_suppress_pagination_header", returned.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("546", returned.Status);
    }

    [Fact]
    public void AFailedRefreshKeepsAQueueThatAlreadyHasACount()
    {
        var notifications = new NotificationWorkspaceViewModel();
        var watch = new AlertWatchState();
        var row = new AlertRecord(
            AlertKind.ReturnedWithNotes,
            DeskSection.Incidents,
            "inc-back",
            "INC1",
            "Badge",
            "In Progress",
            "Client Services",
            "Brisbane",
            "2026-10-02",
            "Alex Rivera",
            "sample-user");
        notifications.Apply(Snapshot(AlertKind.ReturnedWithNotes, new([row], 1)), watch);
        var section = notifications.Sections.Single(item => item.Kind == AlertKind.ReturnedWithNotes);
        Assert.Equal("Returned by DT", section.Title);
        Assert.Equal(1, section.Count);

        notifications.Apply(Snapshot(AlertKind.ReturnedWithNotes, new([], 0, ServiceNowException.QueryTooLongMessage)), watch);
        Assert.Equal(1, section.Count);
        Assert.Equal("INC1", Assert.Single(section.Rows).Number);
        Assert.Equal(ServiceNowException.QueryTooLongMessage, section.Status);
        Assert.Equal(1, notifications.Circles.Single(circle => circle.Kind == AlertKind.ReturnedWithNotes).Count);
        Assert.Equal(1, watch.CurrentCount(AlertKind.ReturnedWithNotes));

        notifications.Apply(Snapshot(AlertKind.ReturnedWithNotes, AlertBucket.Empty), watch);
        Assert.Equal(0, section.Count);
        Assert.Empty(section.Rows);

        notifications.NotePollError(
            "The requested query is too long to build the response pagination header URLs. Set sysparm_suppress_pagination_header.");
        Assert.Equal(ServiceNowException.QueryTooLongMessage, notifications.PollError);

        var leads = new LeadsViewModel();
        var team = Snapshot(AlertKind.ReturnedWithNotes, new([row], 1));
        var empty = new AlertSnapshot(new Dictionary<AlertKind, AlertBucket>());
        leads.Show(new LeadBoard(team, empty));
        leads.Show(new LeadBoard(Snapshot(AlertKind.ReturnedWithNotes, new([], 0, ServiceNowException.QueryTooLongMessage)), empty));
        Assert.Equal(1, leads.Board.Sections.Single(item => item.Kind == AlertKind.ReturnedWithNotes).Count);
        leads.Area = LeadArea.Regional;
        Assert.Equal(0, leads.Board.Sections.Single(item => item.Kind == AlertKind.ReturnedWithNotes).Count);
    }

    private static AlertSnapshot Snapshot(AlertKind kind, AlertBucket bucket) =>
        new(new Dictionary<AlertKind, AlertBucket> { [kind] = bucket });

    private static string PopulationIncident(string sysId, string number, string assigneeId = "sample-user", string assigneeUser = "alex.rivera") =>
        "{\"result\":[{"
        + "\"sys_id\":{\"value\":\"" + sysId + "\"},"
        + "\"number\":{\"value\":\"" + number + "\",\"display_value\":\"" + number + "\"},"
        + "\"short_description\":{\"value\":\"Badge\",\"display_value\":\"Badge\"},"
        + "\"state\":{\"value\":\"2\",\"display_value\":\"In Progress\"},"
        + "\"assigned_to\":{\"value\":\"" + assigneeId + "\",\"display_value\":\"Alex\"},"
        + "\"assigned_to.user_name\":{\"value\":\"" + assigneeUser + "\"},"
        + "\"assignment_group\":{\"value\":\"group-cs\",\"display_value\":\"Client Services\"},"
        + "\"sys_updated_on\":{\"value\":\"2026-10-01 09:00:00\",\"display_value\":\"2026-10-01 09:00\"},"
        + "\"sys_updated_by\":{\"value\":\"alex.rivera\"},"
        + "\"caller_id.user_name\":{\"value\":\"jordan.lee\"},"
        + "\"active\":{\"value\":\"true\"}"
        + "}]}";

    private static string JournalAuthor(string sysId, string author) =>
        "{\"result\":[{"
        + "\"element_id\":{\"value\":\"" + sysId + "\"},"
        + "\"element\":{\"value\":\"work_notes\"},"
        + "\"sys_created_by\":{\"value\":\"" + author + "\"},"
        + "\"sys_created_on\":{\"value\":\"2026-10-02 09:00:00\"}"
        + "}]}";

    private static string WatchedIncident(string sysId, string number, string updatedBy, string caller) =>
        $$"""
        {
          "sys_id": {"value": "{{sysId}}", "display_value": "{{sysId}}"},
          "number": {"value": "{{number}}", "display_value": "{{number}}"},
          "short_description": {"value": "Printer", "display_value": "Printer"},
          "state": {"value": "2", "display_value": "In Progress"},
          "assigned_to": {"value": "sample-user", "display_value": "Alex Rivera"},
          "assignment_group": {"value": "group-cs", "display_value": "Client Services"},
          "sys_updated_on": {"value": "2026-10-01 09:00:00", "display_value": "2026-10-01 09:00"},
          "sys_updated_by": {"value": "{{updatedBy}}", "display_value": "{{updatedBy}}"},
          "caller_id.user_name": {"value": "{{caller}}", "display_value": "{{caller}}"},
          "active": {"value": "true", "display_value": "true"}
        }
        """;

    private static string SlaRow(string taskId, bool breached, string stage, string planned)
    {
        var flag = breached ? "true" : "false";
        return $$"""
            {
              "task": {"value": "{{taskId}}", "display_value": "{{taskId}}"},
              "has_breached": {"value": "{{flag}}", "display_value": "{{flag}}"},
              "stage": {"value": "{{stage}}", "display_value": "{{stage}}"},
              "planned_end_time": {"value": "{{planned}}", "display_value": "{{planned}}"}
            }
            """;
    }

    private static ServiceNowSession BrowserSession() => ServiceNowSession.FromSettings(new DeskSettings
    {
        InstanceUrl = "https://example.service-now.com",
        AuthMode = ServiceNowAuthMode.BrowserSession,
        SessionCookie = "glide_user_session=abc",
        UserToken = "tok-sla"
    });

    private static WatchedRecord SampleRecord() => new()
    {
        Section = DeskSection.Incidents,
        SysId = "inc-sample",
        Number = "INC0091001",
        Title = "Sample",
        State = "In Progress",
        StateValue = "2",
        CallerUserName = "jordan.lee",
        AssigneeUserName = "alex.rivera"
    };

    private static void AssertScoped(string pathAndQuery)
    {
        var path = pathAndQuery;
        var query = QueryOf(pathAndQuery);
        var scoped = path.Contains("sys_user_grmember", StringComparison.Ordinal)
            ? query.Contains("user=", StringComparison.Ordinal)
            : query.Contains("assigned_to=", StringComparison.Ordinal)
                || query.Contains("assignment_group", StringComparison.Ordinal)
                || query.Contains("taskIN", StringComparison.Ordinal)
                || query.Contains("element_idIN", StringComparison.Ordinal);
        Assert.True(scoped, pathAndQuery);
    }

    private static IReadOnlyList<AlertKind> Unacknowledged(AlertWatchState watch) =>
        AlertCatalog.All.Where(watch.IsUnacknowledged).ToArray();

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
