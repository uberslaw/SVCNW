using System.Text;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Tests;

public class LeadsTeamTests
{
    [Fact]
    public void SaveHidesTheCheckboxesAndAReloadShowsTheSamePeople()
    {
        var leads = new LeadsViewModel();
        var roster = new[]
        {
            new Choice("user-jordan", "Jordan Lee"),
            new Choice("user-sam", "Sam Patel"),
            new Choice("user-casey", "Casey Ng")
        };
        leads.SetRoster(roster, []);
        leads.Members.Single(member => member.SysId == "user-jordan").IsSelected = true;
        leads.Members.Single(member => member.SysId == "user-sam").IsSelected = true;
        Assert.True(leads.ShowCheckboxes);
        Assert.False(leads.ShowSavedNames);

        var saved = false;
        leads.TeamPersisted += (_, _) => saved = true;
        leads.SaveTeam();
        Assert.True(saved);
        Assert.True(leads.TeamSaved);
        Assert.False(leads.ShowCheckboxes);
        Assert.False(leads.ShowSaveTeam);
        Assert.True(leads.ShowSavedNames);
        Assert.True(leads.ShowEditTeam);
        Assert.Equal("Team saved.", leads.TeamSavedNote);
        Assert.Equal(["Jordan Lee", "Sam Patel"], leads.SavedNames);
        Assert.Equal(["user-jordan"], leads.DefinedTeam(null).Select(person => person.SysId).Where(id => id == "user-jordan"));
        Assert.Equal(["user-jordan", "user-sam"], leads.DefinedTeam(null).Select(person => person.SysId).OrderBy(id => id, StringComparer.Ordinal));

        var connection = new ConnectionViewModel();
        connection.RememberLeadTeam(leads.SelectedMemberIds);
        connection.RememberLeadTeamSaved(true);
        var json = DeskSettingsFile.Serialize(connection.BuildSettings(), Protect);
        Assert.DoesNotContain("iddqd", json, StringComparison.Ordinal);

        var loaded = new ConnectionViewModel();
        loaded.Load(DeskSettingsFile.Deserialize(json, Unprotect));
        var again = new LeadsViewModel();
        again.ApplyTeamState(loaded.LeadTeamSaved, loaded.LeadsTeamLocked);
        again.SetRoster(roster, loaded.LeadTeamMemberIds);
        Assert.False(again.ShowCheckboxes);
        Assert.Equal("Team saved.", again.TeamSavedNote);
        Assert.Equal(["Jordan Lee", "Sam Patel"], again.SavedNames);
        Assert.Equal(["user-jordan", "user-sam"], again.SelectedMemberIds.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(["user-jordan", "user-sam"], again.DefinedTeam(loaded.LeadTeamMemberIds).Select(person => person.SysId).OrderBy(id => id, StringComparer.Ordinal));

        again.EditTeam();
        Assert.True(again.ShowCheckboxes);
        Assert.True(again.ShowSaveTeam);
        Assert.False(again.ShowSavedNames);
        Assert.False(again.ShowEditTeam);
        Assert.True(again.Members.Single(member => member.SysId == "user-jordan").IsSelected);
        Assert.True(again.Members.Single(member => member.SysId == "user-sam").IsSelected);
        Assert.False(again.Members.Single(member => member.SysId == "user-casey").IsSelected);

        again.SaveTeam();
        Assert.False(again.ShowCheckboxes);
        Assert.Equal(["Jordan Lee", "Sam Patel"], again.SavedNames);
    }

    [Fact]
    public async Task SavedTeamSurvivesRestartAndWorkEffortKeepsThosePeople()
    {
        var store = new MemorySettingsStore();
        var main = new MainViewModel(store, new RecordingDesktopServices());
        main.Connection.UseSampleData = true;
        main.Connection.DownloadCacheOnLaunch = false;
        main.Connection.LeadsPassword = "iddqd";
        main.Connection.UnlockLeads();
        Assert.False(main.Connection.LeadsTeamLocked);
        await main.ConnectCommand.ExecuteAsync(null);
        Assert.True(main.TrySelect(DeskSection.Leads));
        await WaitUntilAsync(() => main.Leads.Members.Count >= 2);

        foreach (var member in main.Leads.Members)
            member.IsSelected = true;
        var names = main.Leads.Members.Select(member => member.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        main.Leads.SaveTeam();
        Assert.False(main.Leads.ShowCheckboxes);
        Assert.Equal("Team saved.", main.Leads.TeamSavedNote);
        Assert.Equal(names, main.Leads.SavedNames.OrderBy(name => name, StringComparer.Ordinal));
        Assert.True(store.Current.LeadTeamSaved);
        Assert.False(store.Current.LeadsTeamLocked);

        var again = new MainViewModel(store, new RecordingDesktopServices());
        await again.InitializeAsync();
        await WaitUntilAsync(() => again.Leads.SavedNames.Count == names.Length && !again.Leads.ShowCheckboxes);
        Assert.False(again.Connection.LeadsTeamLocked);
        Assert.Equal(names, again.Leads.SavedNames.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(
            store.Current.LeadTeamMemberIds!.OrderBy(id => id, StringComparer.Ordinal),
            again.Leads.DefinedTeam(again.Connection.LeadTeamMemberIds).Select(person => person.SysId).OrderBy(id => id, StringComparer.Ordinal));

        again.Leads.EditTeam();
        Assert.True(again.Leads.ShowCheckboxes);
        Assert.All(again.Leads.Members, member => Assert.True(member.IsSelected));
        again.DisconnectCommand.Execute(null);
        main.DisconnectCommand.Execute(null);
    }

    [Fact]
    public void IddqdDoesNotLockTheTeam()
    {
        var leads = new LeadsViewModel();
        leads.ApplyTeamState(saved: true, locked: false);
        leads.SetRoster([new Choice("user-jordan", "Jordan Lee")], ["user-jordan"]);
        Assert.False(leads.TeamLocked);
        Assert.False(leads.ShowLockedTeam);
        Assert.False(leads.ShowCheckboxes);
        Assert.Equal("user-jordan", Assert.Single(leads.DefinedTeam(["someone-else"])).SysId);
        Assert.True(leads.ShowEditTeam);
        Assert.False(leads.ShowSaveTeam);
    }

    [Fact]
    public void LockedRosterKeepsOnlyTheWatchedAusGroupInTheSameCity()
    {
        var rows = new[]
        {
            new LockedLeadMembership("Aus DT - Client Services", "user-ada", "Ada Brisbane", "Brisbane Office"),
            new LockedLeadMembership("APAC DT - Client Services", "user-ada", "Ada Brisbane", "Brisbane Office"),
            new LockedLeadMembership("Client Services", "user-bea", "Bea Brisbane", "Brisbane"),
            new LockedLeadMembership("APAC DT - Client Services", "user-sid", "Sid Sydney", "Sydney Office"),
            new LockedLeadMembership("APAC DT - Client Services", "user-apac-bne", "Pat Apac", "Brisbane Office"),
            new LockedLeadMembership("Network", "user-ned", "Ned Network", "Brisbane Office"),
            new LockedLeadMembership("Client Services", "user-none", "No Place", ""),
            new LockedLeadMembership("Aus DT - Client Services", "user-cbd", "Cbd Person", "Brisbane CBD")
        };

        var team = LockedLeadTeam.Select("Brisbane", rows, "Aus DT - Client Services");
        Assert.Equal(["Ada Brisbane"], team.Select(person => person.Name));
        Assert.DoesNotContain(team, person => person.SysId is "user-sid" or "user-ned" or "user-none" or "user-cbd" or "user-bea" or "user-apac-bne");
        Assert.True(HardwareOfficeNames.SamePlace("Brisbane", "Brisbane Office"));
        Assert.Empty(LockedLeadTeam.Select("  ", rows, "Aus DT - Client Services"));
        Assert.False(LockedLeadTeam.HasCity(null));
        Assert.False(LockedLeadTeam.IsWatchedGroup("APAC DT - Client Services", "Aus DT - Client Services"));
        Assert.True(LockedLeadTeam.IsWatchedGroup("AUS DT - Client Services", "Aus DT - Client Services"));
        Assert.DoesNotContain("LIKE", LockedLeadTeam.MembershipQuery("Aus DT - Client Services"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("group.name=\"Aus DT - Client Services\"", LockedLeadTeam.MembershipQuery("Aus DT - Client Services"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdkfaBuildsTheCityTeamAndSkipsAnInstanceWideQueryWhenThereIsNoLocation()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.Contains("/table/sys_user?", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":{"value":"everyone","display_value":"everyone"},"name":{"value":"Everyone","display_value":"Everyone On The Instance"},"location":{"value":"loc","display_value":"Brisbane"}}]}""");
            return Api.Json(MemberPayload);
        });
        using var blank = ServiceNowClient.Create(Api.BasicSession(), handler);
        var nobody = await blank.ListLockedLeadTeamAsync(" ", CancellationToken.None);
        Assert.Empty(nobody);
        Assert.Empty(handler.Calls);

        handler.Calls.Clear();
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var team = await client.ListLockedLeadTeamAsync("Brisbane", "Aus DT - Client Services", CancellationToken.None);
        Assert.Equal(["user-ada"], team.Select(person => person.SysId));
        Assert.DoesNotContain(team, person => person.Name.Contains("Everyone", StringComparison.Ordinal));
        Assert.DoesNotContain(team, person => person.SysId is "user-sid" or "user-ned" or "user-none" or "user-bea" or "user-apac-bne");

        var call = Assert.Single(handler.Calls);
        var query = Uri.UnescapeDataString(call.PathAndQuery);
        Assert.Contains("/table/sys_user_grmember", query, StringComparison.Ordinal);
        Assert.Contains(LockedLeadTeam.MembershipQuery("Aus DT - Client Services"), query, StringComparison.Ordinal);
        Assert.Contains("group.name=\"Aus DT - Client Services\"", query, StringComparison.Ordinal);
        Assert.DoesNotContain("LIKEClient Services", query, StringComparison.Ordinal);
        Assert.DoesNotContain("APAC", query, StringComparison.Ordinal);
        Assert.DoesNotContain("group=group-", query, StringComparison.Ordinal);
        Assert.DoesNotContain("/table/sys_user?", call.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoUserLocationShowsThePromptAndDoesNotQueryTheLockedTeam()
    {
        using var client = new SampleServiceNowClient();
        Assert.True(string.IsNullOrWhiteSpace(client.SignedInUser.Location));
        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            sampleClientFactory: () => client);
        main.Connection.UseSampleData = true;
        main.Connection.DownloadCacheOnLaunch = false;
        main.Connection.RememberLeadTeam(["user-sam", "user-jordan"]);
        main.Connection.LeadsPassword = "idkfa";
        main.Connection.UnlockLeads();
        await main.ConnectCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => main.Leads.LockedTeamNote == LockedLeadTeam.NoLocationPrompt);
        Assert.True(main.Connection.LeadsTeamLocked);
        Assert.Empty(main.Leads.LockedNames);
        Assert.Empty(main.Leads.LockedMemberIds);
        Assert.Empty(main.Leads.DefinedTeam(main.Connection.LeadTeamMemberIds));
        Assert.False(main.Leads.ShowCheckboxes);
        Assert.False(main.Leads.ShowSaveTeam);
        Assert.False(main.Leads.ShowEditTeam);
        Assert.Equal(0, client.LockedTeamQueries);
        Assert.DoesNotContain(client.RecentActivity, call =>
            call.Path.Contains("/table/sys_user?", StringComparison.Ordinal)
            || call.Path.Contains("sys_user_grmember", StringComparison.Ordinal));
        main.DisconnectCommand.Execute(null);
    }

    [Fact]
    public async Task IdkfaWorkEffortUsesTheLockedCityRoster()
    {
        using var client = new SampleServiceNowClient();
        client.SignedInUser = client.SignedInUser with { Location = "Brisbane" };
        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            sampleClientFactory: () => client);
        main.Connection.UseSampleData = true;
        main.Connection.DownloadCacheOnLaunch = false;
        main.Connection.RememberLeadTeam(["user-sam"]);
        main.Connection.LeadsPassword = "idkfa";
        main.Connection.UnlockLeads();
        await main.ConnectCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => main.Leads.LockedNames.Contains("Jordan Lee"));
        Assert.Equal(LockedLeadTeam.Explanation, main.Leads.LockedTeamNote);
        Assert.Contains("Alex Rivera", main.Leads.LockedNames);
        Assert.Contains("Jordan Lee", main.Leads.LockedNames);
        Assert.DoesNotContain("Sam Patel", main.Leads.LockedNames);
        Assert.DoesNotContain("Pat Apac", main.Leads.LockedNames);
        Assert.DoesNotContain("Bea Brisbane", main.Leads.LockedNames);
        Assert.DoesNotContain("Casey Ng", main.Leads.LockedNames);
        Assert.DoesNotContain("No Location", main.Leads.LockedNames);
        Assert.False(main.Leads.ShowCheckboxes);
        Assert.False(main.Leads.ShowSaveTeam);
        Assert.False(main.Leads.ShowEditTeam);
        var effort = main.Leads.DefinedTeam(main.Connection.LeadTeamMemberIds).Select(person => person.SysId).ToArray();
        Assert.Contains("sample-user", effort);
        Assert.Contains("user-jordan", effort);
        Assert.DoesNotContain("user-sam", effort);
        Assert.DoesNotContain("user-apac-bne", effort);
        Assert.Equal(1, client.LockedTeamQueries);
        var query = Uri.UnescapeDataString(client.RecentActivity.Single(call => call.Path.Contains("sys_user_grmember", StringComparison.Ordinal)).Path);
        Assert.Contains("group.name=\"Aus DT - Client Services\"", query, StringComparison.Ordinal);
        Assert.DoesNotContain("LIKEClient Services", query, StringComparison.Ordinal);

        Assert.True(main.TrySelect(DeskSection.Leads));
        main.Leads.Area = LeadArea.WorkEffort;
        await WaitUntilAsync(() =>
            main.Leads.WorkEffort.HasRows
            && !main.Leads.WorkEffort.IsLoading
            && main.Leads.WorkEffort.Rows.Count > 0);
        var names = main.Leads.WorkEffort.Rows.ToArray().Select(row => row?.Name ?? "").ToArray();
        Assert.Contains("Alex Rivera", names);
        Assert.Contains("Jordan Lee", names);
        Assert.DoesNotContain("Sam Patel", names);
    }

    private const string MemberPayload = """
        {"result":[
          {"group":{"value":"g-aus","display_value":"Aus DT - Client Services"},"group.name":{"value":"Aus DT - Client Services","display_value":"Aus DT - Client Services"},"user":{"value":"user-ada","display_value":"Ada Brisbane"},"user.name":{"value":"Ada Brisbane","display_value":"Ada Brisbane"},"user.location":{"value":"loc-bne","display_value":"Brisbane Office"}},
          {"group":{"value":"g-apac","display_value":"APAC DT - Client Services"},"group.name":{"value":"APAC DT - Client Services","display_value":"APAC DT - Client Services"},"user":{"value":"user-ada","display_value":"Ada Brisbane"},"user.name":{"value":"Ada Brisbane","display_value":"Ada Brisbane"},"user.location":{"value":"loc-bne","display_value":"Brisbane Office"}},
          {"group":{"value":"g-cs","display_value":"Client Services"},"group.name":{"value":"Client Services","display_value":"Client Services"},"user":{"value":"user-bea","display_value":"Bea Brisbane"},"user.name":{"value":"Bea Brisbane","display_value":"Bea Brisbane"},"user.location":{"value":"loc-city","display_value":"Brisbane"}},
          {"group":{"value":"g-syd","display_value":"APAC DT - Client Services"},"group.name":{"value":"APAC DT - Client Services","display_value":"APAC DT - Client Services"},"user":{"value":"user-sid","display_value":"Sid Sydney"},"user.name":{"value":"Sid Sydney","display_value":"Sid Sydney"},"user.location":{"value":"loc-syd","display_value":"Sydney Office"}},
          {"group":{"value":"g-net","display_value":"Network"},"group.name":{"value":"Network","display_value":"Network"},"user":{"value":"user-ned","display_value":"Ned Network"},"user.name":{"value":"Ned Network","display_value":"Ned Network"},"user.location":{"value":"loc-bne","display_value":"Brisbane"}},
          {"group":{"value":"g-cs","display_value":"Client Services"},"group.name":{"value":"Client Services","display_value":"Client Services"},"user":{"value":"user-none","display_value":"No Place"},"user.name":{"value":"No Place","display_value":"No Place"},"user.location":{"value":"","display_value":""}}
        ]}
        """;

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!ready() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(ready());
    }

    private static string Protect(string? value) =>
        string.IsNullOrEmpty(value) ? "" : "ENC:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        const string prefix = "ENC:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
            return "";
        return Encoding.UTF8.GetString(Convert.FromBase64String(value[prefix.Length..]));
    }
}
