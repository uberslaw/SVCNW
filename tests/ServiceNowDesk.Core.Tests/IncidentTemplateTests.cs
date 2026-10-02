using ServiceNowDesk.Client;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class IncidentTemplateTests
{
    [Fact]
    public async Task SaveListApplyAndDeleteATemplateOnANewIncident()
    {
        var folder = NewFolder();
        var store = new FileIncidentTemplateStore(folder);
        using var client = new SampleServiceNowClient();
        var workspace = await OpenAsync(client, store);

        workspace.NewRecordCommand.Execute(null);
        workspace.ShortDescription = "Printer jam";
        workspace.Description = "Tray is stuck";
        workspace.State = "3";
        workspace.Impact = "2";
        workspace.Urgency = "1";
        workspace.Priority = "2";
        workspace.Category = "hardware";
        workspace.Subcategory = "printer";
        workspace.ContactType = "email";
        workspace.HoldReason = "awaiting_caller";
        workspace.Caller.Set("user-jordan", "Jordan Lee");
        await workspace.Assignment.ShowAsync("group-cs", "Client Services", "sample-user", "Alex Rivera");
        workspace.TemplateName = "  Printer  ";
        workspace.SaveTemplateCommand.Execute(null);

        Assert.Contains("Saved template Printer", workspace.TemplateMessage);
        var saved = Assert.Single(new FileIncidentTemplateStore(folder).List());
        Assert.Equal("Printer", saved.Name);
        Assert.Equal("Printer jam", saved.ShortDescription);
        Assert.Equal("Tray is stuck", saved.Description);
        Assert.Equal("3", saved.State);
        Assert.Equal("2", saved.Impact);
        Assert.Equal("1", saved.Urgency);
        Assert.Equal("2", saved.Priority);
        Assert.Equal("hardware", saved.Category);
        Assert.Equal("printer", saved.Subcategory);
        Assert.Equal("Printer", saved.SubcategoryLabel);
        Assert.Equal("email", saved.ContactType);
        Assert.Equal("awaiting_caller", saved.HoldReason);
        Assert.Equal("group-cs", saved.AssignmentGroupId);
        Assert.Equal("Client Services", saved.AssignmentGroupDisplay);
        Assert.Equal("sample-user", saved.AssignedToId);
        Assert.Equal("Alex Rivera", saved.AssignedToDisplay);
        Assert.Equal("user-jordan", saved.CallerId);
        Assert.Equal("Jordan Lee", saved.CallerDisplay);

        var again = await OpenAsync(client, store);
        var template = Assert.Single(again.Templates);
        var posts = client.RecentActivity.Count(entry => entry.Method == "POST");
        await again.ApplyTemplateCommand.ExecuteAsync(template);

        Assert.Equal(posts, client.RecentActivity.Count(entry => entry.Method == "POST"));
        Assert.True(again.IsNew);
        Assert.True(again.IsDirty);
        Assert.Equal("", again.Number);
        Assert.DoesNotContain(again.Items, row => row.Title == "Printer jam");
        Assert.Equal("Printer jam", again.ShortDescription);
        Assert.Equal("Tray is stuck", again.Description);
        Assert.Equal("3", again.State);
        Assert.True(again.ShowHoldReason);
        Assert.Equal("2", again.Impact);
        Assert.Equal("1", again.Urgency);
        Assert.Equal("2", again.Priority);
        Assert.Equal("hardware", again.Category);
        Assert.Equal("printer", again.Subcategory);
        Assert.Equal("email", again.ContactType);
        Assert.Equal("awaiting_caller", again.HoldReason);
        Assert.Equal("user-jordan", again.Caller.SysId);
        Assert.Equal("Jordan Lee", again.Caller.Text);
        Assert.Equal("group-cs", again.Assignment.GroupId);
        Assert.Equal("sample-user", again.Assignment.MemberId);
        Assert.Contains(again.Assignment.Groups, group => group.Value == "group-cs" && group.Label == "Client Services");
        Assert.Contains(again.Assignment.Members, member => member.Value == "sample-user" && member.Label == "Alex Rivera");

        await again.SaveCommand.ExecuteAsync(null);
        Assert.False(again.IsNew);
        Assert.StartsWith("INC", again.Number);
        Assert.Contains(client.RecentActivity, entry => entry.Method == "POST" && entry.Path.Contains("incident", StringComparison.Ordinal));

        again.DeleteTemplateCommand.Execute(template);
        Assert.Empty(again.Templates);
        Assert.Empty(store.List());
        Assert.Contains("Deleted template Printer", again.TemplateMessage);
    }

    [Fact]
    public async Task EmptyTemplateNamesAreRejectedAndTheSameNameReplaces()
    {
        var store = new FileIncidentTemplateStore(NewFolder());
        using var client = new SampleServiceNowClient();
        var workspace = await OpenAsync(client, store);
        workspace.NewRecordCommand.Execute(null);
        workspace.ShortDescription = "VPN down";
        workspace.TemplateName = "   ";
        workspace.SaveTemplateCommand.Execute(null);

        Assert.Contains("name", workspace.TemplateMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.List());
        Assert.Throws<ArgumentException>(() => store.Save(new IncidentTemplate { Name = "  ", ShortDescription = "VPN down" }));

        workspace.TemplateName = "Vpn";
        workspace.SaveTemplateCommand.Execute(null);
        workspace.ShortDescription = "VPN down again";
        workspace.TemplateName = "vpn";
        workspace.SaveTemplateCommand.Execute(null);

        var template = Assert.Single(store.List());
        Assert.Equal("vpn", template.Name);
        Assert.Equal("VPN down again", template.ShortDescription);
        Assert.Contains("Replaced template vpn", workspace.TemplateMessage);
    }

    [Fact]
    public async Task PracticeModeUsesTheSameTemplateStore()
    {
        var store = new FileIncidentTemplateStore(NewFolder());
        store.Save(new IncidentTemplate
        {
            Name = "Vpn",
            ShortDescription = "VPN down",
            CallerId = "user-jordan",
            CallerDisplay = "Jordan Lee"
        });
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices(), templates: store);
        main.Connection.UseSampleData = true;
        await main.ConnectCommand.ExecuteAsync(null);

        var template = Assert.Single(main.Incidents.Templates);
        await main.Incidents.ApplyTemplateCommand.ExecuteAsync(template);

        Assert.True(main.IsSample);
        Assert.True(main.Incidents.IsNew);
        Assert.Equal("VPN down", main.Incidents.ShortDescription);
        Assert.Equal("user-jordan", main.Incidents.Caller.SysId);
        Assert.DoesNotContain(main.Incidents.Items, row => row.Title == "VPN down");
    }

    [Fact]
    public async Task ReloadingGroupsKeepsAnUnsavedAssignment()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenAsync(client, new MemoryIncidentTemplateStore());
        workspace.NewRecordCommand.Execute(null);
        await workspace.Assignment.ShowAsync("group-cs", "Client Services", "sample-user", "Alex Rivera");
        workspace.ShortDescription = "Headset";
        Assert.True(workspace.IsDirty);

        await workspace.Assignment.LoadGroupsAsync();

        Assert.Equal("group-cs", workspace.Assignment.GroupId);
        Assert.Equal("sample-user", workspace.Assignment.MemberId);
        Assert.Equal("Headset", workspace.ShortDescription);
        Assert.True(workspace.IsNew);
    }

    [Fact]
    public void CorruptTemplateFileIsIgnored()
    {
        var folder = NewFolder();
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "incident-templates.json"), "{");

        Assert.Empty(new FileIncidentTemplateStore(folder).List());
    }

    private static async Task<IncidentWorkspaceViewModel> OpenAsync(IServiceNowClient client, IIncidentTemplateStore store)
    {
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices(), store);
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        await workspace.RefreshAsync();
        return workspace;
    }

    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "snd-templates-" + Guid.NewGuid().ToString("N"));
}
