using System.Net;
using System.Text;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class ReferenceCatalogTests
{
    [Fact]
    public async Task ServiceOfferingsPageUntilTheListIsComplete()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (!path.Contains("service_offering", StringComparison.Ordinal))
                return Api.Json("""{"result":[]}""");

            if (query.Contains("sysparm_offset=0", StringComparison.Ordinal))
                return Api.Json("{\"result\":[" + Rows("off", "Offering", 0, 200) + "]}", total: 201);

            Assert.Contains("sysparm_offset=200", query, StringComparison.Ordinal);
            return Api.Json("{\"result\":[" + Rows("off", "Offering", 200, 1) + "]}", total: 201);
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var downloaded = await client.DownloadServiceOfferingsAsync(null, CancellationToken.None);

        Assert.False(downloaded.Truncated);
        Assert.Equal(201, downloaded.Count);
        Assert.Equal(2, handler.Calls.Count(call => call.PathAndQuery.Contains("service_offering", StringComparison.Ordinal)));
        var saved = await client.ListServiceOfferingsAsync(CancellationToken.None);
        Assert.Contains(saved, choice => choice.Value == "off-0" && choice.Label == "Offering 0");
        Assert.Contains(saved, choice => choice.Value == "off-200" && choice.Label == "Offering 200");
    }

    [Fact]
    public async Task ConfigurationItemDownloadStopsAtTheCapAndKeepsThoseRows()
    {
        var handler = new StubHandler((request, _) =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri?.Query ?? "");
            Assert.Contains("cmdb_ci", request.RequestUri?.AbsolutePath, StringComparison.Ordinal);
            Assert.Contains("sysparm_limit=2", query, StringComparison.Ordinal);
            return Api.Json("{\"result\":[" + Rows("ci", "CI", 0, 2) + "]}", total: 9);
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);

        var downloaded = await client.DownloadConfigurationItemsAsync(2, null, CancellationToken.None);

        Assert.True(downloaded.Truncated);
        Assert.Equal(2, downloaded.Count);
        var saved = await client.ListConfigurationItemsAsync(CancellationToken.None);
        Assert.Equal(["CI 0", "CI 1"], saved.Select(choice => choice.Label).ToArray());
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task FailedOfferingRefreshKeepsTheSavedCopy()
    {
        var folder = Path.Combine(Path.GetTempPath(), "snd-offerings-" + Guid.NewGuid().ToString("N"));
        var store = new FileFormCatalogStore(folder);
        var session = Api.BasicSession();
        store.Save(session.InstanceUri, new FormCatalogSnapshot
        {
            ServiceOfferingsCapturedAt = DateTimeOffset.UtcNow.AddHours(-25),
            ServiceOfferings = [new CachedNamedRecord { SysId = "offering-print", Name = "Printing" }],
            ConfigurationItemsCapturedAt = DateTimeOffset.UtcNow,
            ConfigurationItems = [new CachedNamedRecord { SysId = "ci-printer", Name = "HQ-PRINTER-01" }]
        });
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/sys_user", StringComparison.Ordinal)
                && !path.Contains("sys_user_group", StringComparison.Ordinal)
                && !path.Contains("sys_user_grmember", StringComparison.Ordinal))
            {
                return Api.Json("""{"result":[{"sys_id":"sample-user","name":"Alex Rivera","user_name":"alex.rivera","email":"alex@example.com"}]}""");
            }

            if (path.Contains("service_offering", StringComparison.Ordinal))
            {
                return Api.Json(
                    """{"error":{"message":"unavailable","detail":"offerings unavailable"},"status":"failure"}""",
                    HttpStatusCode.InternalServerError);
            }

            return Api.Json("""{"result":[]}""");
        });
        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            formCatalog: store,
            clientFactory: (_, catalog) => ServiceNowClient.Create(session, handler, catalog),
            lists: new MemoryDeskListStore());
        main.Connection.InstanceUrl = "https://example.service-now.com";
        main.Connection.Username = "alex";
        main.Connection.Password = "secret";
        await main.ConnectCommand.ExecuteAsync(null);
        handler.Calls.Clear();

        var row = Assert.Single(main.Caches, cache => cache.Name == "Service offerings");
        await main.RefreshCacheCommand.ExecuteAsync(row);

        Assert.True(row.IsFailed);
        Assert.Contains("unavailable", row.Status, StringComparison.OrdinalIgnoreCase);
        var stillThere = store.Load(session.InstanceUri);
        Assert.NotNull(stillThere);
        Assert.Contains(stillThere.ServiceOfferings, offering => offering.SysId == "offering-print" && offering.Name == "Printing");
        Assert.Contains(stillThere.ConfigurationItems, item => item.SysId == "ci-printer" && item.Name == "HQ-PRINTER-01");
        Assert.Contains(main.Incidents.ServiceOffering.Choices, choice => choice.Value == "offering-print" && choice.Label == "Printing");
        Assert.Contains(main.Startup.Lines, line => line.Name == "Service offerings");
    }

    [Fact]
    public async Task PracticeModeIncludesSampleOfferingsAndCanSearchAMissingConfigurationItem()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        main.Connection.UseSampleData = true;
        await main.ConnectCommand.ExecuteAsync(null);

        Assert.Contains(main.Incidents.ServiceOffering.Choices, choice => choice.Value == "offering-print" && choice.Label == "Printing");
        Assert.Contains(main.Incidents.ConfigurationItem.Choices, choice => choice.Value == "ci-printer" && choice.Label == "HQ-PRINTER-01");
        Assert.Contains(main.RequestedItems.ServiceOffering.Choices, choice => choice.Label == "Network access");
        Assert.DoesNotContain(main.Incidents.ConfigurationItem.Choices, choice => choice.Value == "ci-switch");

        main.Incidents.ConfigurationItem.Filter = "core-switch";
        await main.Incidents.ConfigurationItem.WhenReady;

        Assert.Contains(main.Incidents.ConfigurationItem.Choices, choice => choice.Value == "ci-switch" && choice.Label == "CORE-SWITCH-02");
        main.Incidents.ConfigurationItem.Id = "ci-switch";
        Assert.Equal("CORE-SWITCH-02", main.Incidents.ConfigurationItem.SelectedLabel);
        Assert.DoesNotContain("ci-switch", main.Incidents.ConfigurationItem.SelectedLabel, StringComparison.OrdinalIgnoreCase);

        main.Incidents.ConfigurationItem.Filter = "";
        await main.Incidents.ConfigurationItem.WhenReady;
        Assert.Contains(main.Incidents.ConfigurationItem.Choices, choice => choice.Label == "HQ-PRINTER-01");
        Assert.Contains(main.Incidents.ConfigurationItem.Choices, choice => choice.Label == "CORE-SWITCH-02");
    }

    private static string Rows(string prefix, string label, int start, int count)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
                builder.Append(',');
            var number = start + index;
            builder.Append("{\"sys_id\":\"").Append(prefix).Append('-').Append(number).Append("\",\"name\":\"")
                .Append(label).Append(' ').Append(number).Append("\"}");
        }

        return builder.ToString();
    }
}
