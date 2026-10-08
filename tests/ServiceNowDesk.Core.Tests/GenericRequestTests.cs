using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class GenericRequestTests
{
    [Fact]
    public void EmptyVariableListUsesTheFallbackNames()
    {
        var resolved = GenericRequestVariables.Resolve([]);
        var payload = resolved.ToPayload("user-jordan", " Badge reel ", " The catalog has no reel. ");

        Assert.Equal(GenericRequestItem.RequestedForVariable, resolved.RequestedFor);
        Assert.Equal(GenericRequestItem.TitleVariable, resolved.Title);
        Assert.Equal(GenericRequestItem.DescriptionVariable, resolved.Description);
        Assert.True(resolved.IncludeRequestedForVariable);
        Assert.Equal("user-jordan", payload[GenericRequestItem.RequestedForVariable]);
        Assert.Equal("Badge reel", payload[GenericRequestItem.TitleVariable]);
        Assert.Equal("The catalog has no reel.", payload[GenericRequestItem.DescriptionVariable]);
    }

    [Fact]
    public void LiveLabelsReplaceTheFallbackNames()
    {
        var resolved = GenericRequestVariables.Resolve(
        [
            new CatalogVariableDefinition("u_title", "Request Title", true, []),
            new CatalogVariableDefinition("u_details", "Request Description", true, []),
            new CatalogVariableDefinition("u_requested_for", "Requested for", true, [])
        ]);
        var payload = resolved.ToPayload("user-jordan", "Keyboard", "The dock has no keyboard.");

        Assert.Equal("u_title", resolved.Title);
        Assert.Equal("u_details", resolved.Description);
        Assert.Equal("u_requested_for", resolved.RequestedFor);
        Assert.Equal("Keyboard", payload["u_title"]);
        Assert.Equal("The dock has no keyboard.", payload["u_details"]);
        Assert.Equal("user-jordan", payload["u_requested_for"]);
    }

    [Fact]
    public void ALoadedItemWithoutRequestedForOmitsThatVariable()
    {
        var resolved = GenericRequestVariables.Resolve(
        [
            new CatalogVariableDefinition("request_title", "Heading", true, []),
            new CatalogVariableDefinition("short_description", "Notes", true, []),
            new CatalogVariableDefinition("request_description", "Details", true, [])
        ]);
        var payload = resolved.ToPayload("user-jordan", "Keyboard", "More detail");

        Assert.Equal("request_title", resolved.Title);
        Assert.Equal("request_description", resolved.Description);
        Assert.False(resolved.IncludeRequestedForVariable);
        Assert.False(payload.ContainsKey(GenericRequestItem.RequestedForVariable));
        Assert.Equal("More detail", payload["request_description"]);
    }

    [Fact]
    public async Task PracticeFormPrefillsTheSignedInUserAndReturnsBothNumbers()
    {
        using var client = new SampleServiceNowClient();
        var catalog = new CatalogWorkspaceViewModel();
        catalog.Attach(client);
        await catalog.PrepareGenericRequestAsync();

        Assert.Equal("sample-user", catalog.GenericRequestedFor.SysId);
        Assert.Equal("Alex Rivera", catalog.GenericRequestedFor.Text);

        catalog.GenericTitle = "Spare keyboard";
        catalog.GenericDescription = "No catalog item covers a keyboard.";
        CatalogOrderResult? ordered = null;
        catalog.RequestOrdered += (_, result) => ordered = result;
        await catalog.SubmitGenericRequestCommand.ExecuteAsync(null);

        Assert.Equal("", catalog.GenericError);
        Assert.NotNull(ordered);
        Assert.StartsWith("REQ", ordered.RequestNumber);
        Assert.StartsWith("RITM", ordered.RequestedItemNumber);
        Assert.Contains(ordered.RequestNumber, catalog.GenericMessage);
        Assert.Contains(ordered.RequestedItemNumber, catalog.GenericMessage);
        Assert.Equal(GenericRequestItem.SysId, client.LastOrderedItemId);
        Assert.Equal("Spare keyboard", client.LastOrderedVariables[GenericRequestItem.TitleVariable]);
        Assert.Equal("No catalog item covers a keyboard.", client.LastOrderedVariables[GenericRequestItem.DescriptionVariable]);
        Assert.Equal("sample-user", client.LastOrderedVariables[GenericRequestItem.RequestedForVariable]);
        Assert.Equal("", catalog.GenericTitle);
        Assert.Equal("", catalog.GenericDescription);

        var request = await client.GetRequestAsync(ordered.RequestSysId, CancellationToken.None);
        Assert.Equal("sample-user", request.RequestedFor.SysId);
        Assert.Equal("Spare keyboard", request.ShortDescription);
        var item = await client.GetRequestedItemAsync(ordered.RequestedItemSysId, CancellationToken.None);
        Assert.Equal(ordered.RequestedItemNumber, item.Number);
        Assert.Equal(request.Number, item.Request.Display);
        var order = Assert.Single(client.RecentActivity);
        Assert.Equal("POST", order.Method);
        Assert.Contains(GenericRequestItem.SysId, order.Path);
        Assert.Contains("order_now", order.Path);
        Assert.DoesNotContain("http", order.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenericRequestRefusesAnEmptyTitleDescriptionOrPerson()
    {
        using var client = new SampleServiceNowClient();
        var before = (await client.SearchRequestsAsync(new TicketQuery { Activity = ActivityFilter.Any }, CancellationToken.None)).Items.Count;
        var catalog = new CatalogWorkspaceViewModel();
        catalog.Attach(client);
        await catalog.PrepareGenericRequestAsync();

        catalog.GenericRequestedFor.Clear();
        catalog.GenericTitle = "Spare keyboard";
        catalog.GenericDescription = "No catalog item covers a keyboard.";
        await catalog.SubmitGenericRequestCommand.ExecuteAsync(null);
        Assert.Contains("who this request is for", catalog.GenericError);

        catalog.GenericRequestedFor.Set("user-jordan", "Jordan Lee");
        catalog.GenericTitle = "   ";
        await catalog.SubmitGenericRequestCommand.ExecuteAsync(null);
        Assert.Contains("title", catalog.GenericError, StringComparison.OrdinalIgnoreCase);

        catalog.GenericTitle = "Spare keyboard";
        catalog.GenericDescription = " ";
        await catalog.SubmitGenericRequestCommand.ExecuteAsync(null);
        Assert.Contains("description", catalog.GenericError, StringComparison.OrdinalIgnoreCase);

        var after = (await client.SearchRequestsAsync(new TicketQuery { Activity = ActivityFilter.Any }, CancellationToken.None)).Items.Count;
        Assert.Equal(before, after);
        Assert.DoesNotContain(client.RecentActivity, call => call.Path.Contains("order_now", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypedNameIsUsedWhenItMatchesOnePerson()
    {
        using var client = new SampleServiceNowClient();
        var catalog = new CatalogWorkspaceViewModel();
        catalog.Attach(client);
        await catalog.PrepareGenericRequestAsync();
        catalog.GenericRequestedFor.Clear();
        catalog.GenericRequestedFor.Text = "j";
        await Task.Delay(400);
        Assert.False(catalog.GenericRequestedFor.HasSuggestions);

        catalog.GenericRequestedFor.Text = "jordan.lee@example.com";
        await WaitUntilAsync(() => catalog.GenericRequestedFor.HasSuggestions);
        Assert.Equal("Jordan Lee", catalog.GenericRequestedFor.Suggestions[0].Display);
        Assert.Equal("jordan.lee@example.com", catalog.GenericRequestedFor.Suggestions[0].Detail);
        Assert.Equal("", catalog.GenericRequestedFor.SysId);

        catalog.GenericTitle = "Spare keyboard";
        catalog.GenericDescription = "No catalog item covers a keyboard.";
        await catalog.SubmitGenericRequestCommand.ExecuteAsync(null);

        Assert.Equal("", catalog.GenericError);
        Assert.Equal("user-jordan", catalog.GenericRequestedFor.SysId);
        Assert.Equal("user-jordan", client.LastOrderedVariables[GenericRequestItem.RequestedForVariable]);

        catalog.GenericRequestedFor.Clear();
        catalog.GenericRequestedFor.Text = "Casey Ng";
        catalog.GenericTitle = "Spare keyboard";
        catalog.GenericDescription = "No catalog item covers a keyboard.";
        await catalog.SubmitGenericRequestCommand.ExecuteAsync(null);
        Assert.Contains("list", catalog.GenericError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", catalog.GenericRequestedFor.SysId);
    }

    [Fact]
    public async Task PracticeSubmitOpensTheRequestItemWithBothNumbers()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        main.Connection.UseSampleData = true;
        await main.ConnectCommand.ExecuteAsync(null);
        await main.Catalog.PrepareGenericRequestAsync();

        CatalogOrderResult? ordered = null;
        main.Catalog.RequestOrdered += (_, result) => ordered = result;
        main.Catalog.GenericTitle = "Spare keyboard";
        main.Catalog.GenericDescription = "No catalog item covers a keyboard.";
        await main.Catalog.SubmitGenericRequestCommand.ExecuteAsync(null);

        await WaitUntilAsync(() =>
            ordered is not null
            && main.RequestedItems.Number == ordered.RequestedItemNumber
            && main.StatusMessage.Contains(ordered.RequestedItemNumber, StringComparison.Ordinal));

        Assert.NotNull(ordered);
        Assert.Equal(DeskSection.RequestedItems, main.SelectedSection);
        Assert.Equal(ordered.RequestedItemNumber, main.RequestedItems.Number);
        Assert.Equal(ordered.RequestNumber, main.RequestedItems.RequestNumber);
        Assert.Contains(ordered.RequestNumber, main.StatusMessage);
        Assert.Contains(ordered.RequestedItemNumber, main.StatusMessage);
        Assert.Equal("Spare keyboard", main.RequestedItems.ShortDescription);
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for the generic request.");
            await Task.Delay(50);
        }
    }
}
