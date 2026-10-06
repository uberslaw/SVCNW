using System.Reflection;
using System.Runtime.ExceptionServices;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Mapping;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class PersonalTaskTests
{
    [Fact]
    public void NotesAddListChangeColorRemoveAndReloadFromATempFile()
    {
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, DeskAppData.DailyTasksFileName);
            var page = new DailyWorkViewModel(new MemoryDailyWorkStore(), new FilePersonalTaskStore(path));
            page.NoteText = "  Call the lab  ";
            Assert.True(page.TryAddNote());
            var first = Assert.Single(page.Notes);
            Assert.Equal("Call the lab", first.Text);
            Assert.Equal(TrafficLight.Amber, first.Light);
            Assert.Equal(DateTimeKind.Utc, first.CreatedUtc.Kind);
            Assert.NotEqual(Guid.Empty, first.Id);
            Assert.Contains("Call the lab", File.ReadAllText(path));
            Assert.Contains("\"trafficLight\": \"Amber\"", File.ReadAllText(path));

            page.NoteLight = TrafficLight.Red;
            page.NoteText = new string('x', PersonalTask.MaxTextLength + 40);
            Assert.True(page.TryAddNote());
            Assert.Equal(2, page.Notes.Count);
            Assert.Equal(PersonalTask.MaxTextLength, page.Notes[1].Text.Length);
            Assert.Equal(TrafficLight.Red, page.Notes[1].Light);

            page.ChangeNoteColor(first.Id, TrafficLight.Green);
            Assert.Equal(TrafficLight.Green, page.Notes[0].Light);
            Assert.Equal(PersonalTask.Hex(TrafficLight.Green), page.Notes[0].LightHex);

            Assert.True(page.RemovePersonalNote(page.Notes[1].Id));
            page.NoteText = "   ";
            Assert.False(page.TryAddNote());
            Assert.Single(page.Notes);
            page.Clear();
            Assert.Equal("Call the lab", Assert.Single(page.Notes).Text);

            var again = new DailyWorkViewModel(new MemoryDailyWorkStore(), new FilePersonalTaskStore(path));
            var reloaded = Assert.Single(again.Notes);
            Assert.Equal(first.Id, reloaded.Id);
            Assert.Equal("Call the lab", reloaded.Text);
            Assert.Equal(TrafficLight.Green, reloaded.Light);
            Assert.Equal(first.CreatedUtc, reloaded.CreatedUtc);
        }
        finally
        {
            Delete(folder);
        }
    }

    [Fact]
    public async Task ExpiredSessionAndDisconnectLeaveTheTaskFile()
    {
        var folder = NewFolder();
        try
        {
            var tasks = new FilePersonalTaskStore(Path.Combine(folder, DeskAppData.DailyTasksFileName));
            var settings = new MemorySettingsStore();
            var signedIn = DateTimeOffset.UtcNow.AddHours(-25);
            settings.Save(new DeskSettings
            {
                InstanceUrl = "https://kept.service-now.com",
                AuthMode = ServiceNowAuthMode.BrowserSession,
                Password = "kept-password",
                SessionCookie = "glide_user_session=abc",
                UserToken = "tok-ck",
                SignedInAt = signedIn
            });
            var main = new MainViewModel(settings, new RecordingDesktopServices(), personalTasks: tasks);
            main.DailyWork.NoteText = "Keep the toner note";
            Assert.True(main.DailyWork.TryAddNote());
            var before = File.ReadAllBytes(tasks.Path);

            await main.InitializeAsync();

            Assert.Equal(BrowserSignInClock.ExpiredStatus, main.StatusMessage);
            Assert.False(main.IsConnected);
            Assert.Equal(before, File.ReadAllBytes(tasks.Path));
            Assert.Equal("Keep the toner note", Assert.Single(main.DailyWork.Notes).Text);

            DeskAppData.WriteSettings("{\"sessionCookie\":\"\"}", folder);
            Assert.Equal(before, File.ReadAllBytes(tasks.Path));
            Assert.True(File.Exists(Path.Combine(folder, DeskAppData.SettingsFileName)));
            Assert.Equal(DeskAppData.SettingsFileName, Path.GetFileName(DeskAppData.SettingsPath));
            Assert.Equal(DeskAppData.DailyTasksFileName, Path.GetFileName(DeskAppData.DailyTasksPath));
            Assert.Equal(Path.GetDirectoryName(DeskAppData.SettingsPath), Path.GetDirectoryName(DeskAppData.DailyTasksPath));
            Assert.NotEqual(DeskAppData.SettingsPath, DeskAppData.DailyTasksPath);
            Assert.Equal(DeskAppData.DailyTasksPath, FilePersonalTaskStore.InApplicationData().Path);

            main.Connection.UseSampleData = true;
            await main.ConnectCommand.ExecuteAsync(null);
            Assert.True(main.IsConnected);
            main.DisconnectCommand.Execute(null);
            Assert.False(main.IsConnected);
            Assert.Equal(before, File.ReadAllBytes(tasks.Path));
            Assert.Equal("Keep the toner note", Assert.Single(main.DailyWork.Notes).Text);
        }
        finally
        {
            Delete(folder);
        }
    }

    [Fact]
    public void ConvertBuildsIncidentAndRequestedItemChangesFromTheNote()
    {
        var incident = PersonalTaskConversion.ToIncident("Replace the toner", "sample-user");
        Assert.Equal("Replace the toner", incident.ShortDescription);
        Assert.Equal("sample-user", incident.CallerId);
        Assert.Null(PersonalTaskConversion.ToIncident("Replace the toner", "  ").CallerId);

        var item = PersonalTaskConversion.ToRequestedItem("Replace the toner", "sample-user");
        Assert.Equal("Replace the toner", item.ShortDescription);
        Assert.Equal("sample-user", item.RequestedForId);
        Assert.Null(PersonalTaskConversion.ToRequestedItem("Replace the toner", null).RequestedForId);

        using var json = System.Text.Json.JsonDocument.Parse(ChangeJson.FromRequestedItem(item));
        Assert.Equal("Replace the toner", json.RootElement.GetProperty("short_description").GetString());
        Assert.Equal("sample-user", json.RootElement.GetProperty("requested_for").GetString());
        Assert.False(json.RootElement.TryGetProperty("cat_item", out _));
    }

    [Fact]
    public async Task SampleClientCreatesARequestedItemAndKeepsExistingOnes()
    {
        using var client = new SampleServiceNowClient();
        var existing = await client.GetRequestedItemAsync("ritm-laptop", CancellationToken.None);
        Assert.Equal("RITM0010001", existing.Number);
        Assert.Equal("Standard laptop", existing.ShortDescription);

        var created = await client.CreateRequestedItemAsync(new RequestedItemChanges
        {
            ShortDescription = "Order a spare dock",
            RequestedForId = "sample-user"
        }, CancellationToken.None);

        Assert.StartsWith("RITM", created.Number);
        Assert.NotEqual("RITM0010001", created.Number);
        Assert.Equal("Order a spare dock", created.ShortDescription);
        Assert.True(created.CatalogItem.IsEmpty);
        Assert.True(created.Request.IsEmpty);
        Assert.Contains(client.RecentActivity, activity => activity.Method == "POST" && activity.Path == "api/now/table/sc_req_item");

        var again = await client.GetRequestedItemAsync(created.SysId, CancellationToken.None);
        Assert.Equal(created.Number, again.Number);
        Assert.Equal(created.SysId, again.SysId);
        Assert.Equal("Order a spare dock", again.ShortDescription);

        var still = await client.GetRequestedItemAsync("ritm-laptop", CancellationToken.None);
        Assert.Equal("RITM0010001", still.Number);
        Assert.Equal("Standard laptop", still.ShortDescription);
    }

    [Fact]
    public async Task PracticeModeTurnsANoteIntoAnIncidentAndARequestedItem()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        main.Connection.UseSampleData = true;
        await main.ConnectCommand.ExecuteAsync(null);
        Assert.True(main.IsConnected);

        main.DailyWork.NoteText = "Replace the toner";
        Assert.True(main.DailyWork.TryAddNote());
        main.DailyWork.NoteLight = TrafficLight.Green;
        main.DailyWork.NoteText = "Order a spare dock";
        Assert.True(main.DailyWork.TryAddNote());
        var toner = main.DailyWork.Notes[0];
        var dock = main.DailyWork.Notes[1];

        main.DailyWork.CreateIncidentCommand.Execute(toner);
        await main.NoteConvertTask;

        Assert.Equal(DeskSection.Incidents, main.SelectedSection);
        Assert.True(main.Incidents.HasEditor);
        Assert.Equal("Replace the toner", main.Incidents.ShortDescription);
        Assert.Equal("sample-user", main.Incidents.Caller.SysId);
        Assert.StartsWith("INC", main.Incidents.Number);
        Assert.DoesNotContain(main.DailyWork.Notes, note => note.Id == toner.Id);
        Assert.Contains(main.DailyWork.Notes, note => note.Id == dock.Id);

        main.DailyWork.CreateRequestedItemCommand.Execute(dock);
        await main.NoteConvertTask;

        Assert.Equal(DeskSection.RequestedItems, main.SelectedSection);
        Assert.True(main.RequestedItems.HasEditor);
        Assert.Equal("Order a spare dock", main.RequestedItems.ShortDescription);
        Assert.StartsWith("RITM", main.RequestedItems.Number);
        Assert.DoesNotContain(main.RequestedItems.Number, new[] { "RITM0010001", "RITM0010002", "RITM0010003", "RITM0010004", "RITM0010005", "RITM0010006" });
        Assert.Empty(main.DailyWork.Notes);
    }

    [Fact]
    public async Task ConvertRequiresAConnectionAndARejectedRitmStaysInTheList()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        main.DailyWork.NoteText = "Stay on the list";
        Assert.True(main.DailyWork.TryAddNote());
        var note = Assert.Single(main.DailyWork.Notes);

        main.DailyWork.CreateIncidentCommand.Execute(note);
        await main.NoteConvertTask;
        Assert.Equal(PersonalTaskConversion.NotConnectedMessage, main.ErrorMessage);
        Assert.Single(main.DailyWork.Notes);

        main.DailyWork.CreateRequestedItemCommand.Execute(note);
        await main.NoteConvertTask;
        Assert.Equal(PersonalTaskConversion.NotConnectedMessage, main.ErrorMessage);
        Assert.Equal("Stay on the list", Assert.Single(main.DailyWork.Notes).Text);

        var connected = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            sampleClientFactory: RejectingSample);
        connected.Connection.UseSampleData = true;
        await connected.ConnectCommand.ExecuteAsync(null);
        Assert.True(connected.IsConnected);
        connected.DailyWork.NoteText = "Order a spare dock";
        Assert.True(connected.DailyWork.TryAddNote());
        var dock = Assert.Single(connected.DailyWork.Notes);

        connected.DailyWork.CreateRequestedItemCommand.Execute(dock);
        await connected.NoteConvertTask;

        Assert.Contains("Insert rejected", connected.ErrorMessage);
        Assert.Equal("Order a spare dock", Assert.Single(connected.DailyWork.Notes).Text);
        Assert.False(connected.RequestedItems.HasEditor);
    }

    private static IServiceNowClient RejectingSample()
    {
        var proxy = DispatchProxy.Create<IServiceNowClient, SampleGate>();
        var gate = (SampleGate)(object)proxy;
        gate.Inner = new SampleServiceNowClient();
        gate.RejectRequestedItems = true;
        return proxy;
    }

    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "snd-notes-" + Guid.NewGuid().ToString("N"));

    private static void Delete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class SampleGate : DispatchProxy
{
    public SampleServiceNowClient Inner { get; set; } = null!;

    public bool RejectRequestedItems { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            throw new InvalidOperationException("Missing ServiceNow method.");
        if (RejectRequestedItems && targetMethod.Name == nameof(IServiceNowClient.CreateRequestedItemAsync))
            throw new ServiceNowException(403, "ServiceNow: Insert rejected", "ACL");

        try
        {
            return targetMethod.Invoke(Inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
