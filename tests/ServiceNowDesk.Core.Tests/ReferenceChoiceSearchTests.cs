using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class ReferenceChoiceSearchTests
{
    [Fact]
    public void PrintStarMatchesThePrinterAndNotTheLaptop()
    {
        Assert.True(ReferenceNameMatcher.Matches("HQ-PRINTER-01", "print*"));
        Assert.False(ReferenceNameMatcher.Matches("LAPTOP-FIN-014", "print*"));
        Assert.True(ReferenceNameMatcher.Matches("HQ-PRINTER-01", "printer"));
        Assert.True(ReferenceNameMatcher.Matches("HQ-PRINTER-01", "HQ-PRINTER-0?"));
        Assert.False(ReferenceNameMatcher.Matches("HQ-PRINTER-01", "HQ-PRINTER-0??"));
    }

    [Fact]
    public void PlusRequiresEveryTerm()
    {
        Assert.True(ReferenceNameMatcher.Matches("VPN-GATEWAY", "vpn + gateway"));
        Assert.True(ReferenceNameMatcher.Matches("VPN-GATEWAY", "gateway+vpn"));
        Assert.False(ReferenceNameMatcher.Matches("VPN-ROUTER", "vpn + gateway"));
        Assert.False(ReferenceNameMatcher.Matches("EDGE-GATEWAY", "vpn + gateway"));
    }

    [Fact]
    public async Task OpeningAWarmConfigurationItemCacheDoesNotCallServiceNow()
    {
        var folder = Path.Combine(Path.GetTempPath(), "snd-ci-warm-" + Guid.NewGuid().ToString("N"));
        var store = new FileFormCatalogStore(folder);
        var session = Api.BasicSession();
        store.Save(session.InstanceUri, new FormCatalogSnapshot
        {
            ConfigurationItemsCapturedAt = DateTimeOffset.UtcNow,
            ConfigurationItems =
            [
                new CachedNamedRecord { SysId = "ci-printer", Name = "HQ-PRINTER-01" },
                new CachedNamedRecord { SysId = "ci-laptop", Name = "LAPTOP-FIN-014" },
                new CachedNamedRecord { SysId = "ci-vpn", Name = "VPN-GATEWAY" }
            ]
        });
        var handler = new StubHandler((_, _) => Api.Json("""{"result":[]}"""));
        using var client = ServiceNowClient.Create(session, handler, store);
        handler.Calls.Clear();

        var field = new ReferenceChoiceField((live, token) => live.ListConfigurationItemsAsync(token), searchRemote: true);
        field.Use(client);
        await field.LoadAsync();

        Assert.Empty(handler.Calls);
        Assert.Contains(field.Choices, choice => choice.Value == "ci-printer" && choice.Label == "HQ-PRINTER-01");

        field.Filter = "print*";
        await field.WhenReady;

        Assert.Empty(handler.Calls);
        Assert.Contains(field.Choices, choice => choice.Label == "HQ-PRINTER-01");
        Assert.DoesNotContain(field.Choices, choice => choice.Label == "LAPTOP-FIN-014");
    }

    [Fact]
    public async Task DiskCacheOpensWithoutALiveConfigurationItemQuery()
    {
        var session = Api.BasicSession();
        var saved = new FormCatalogSnapshot
        {
            ConfigurationItemsCapturedAt = DateTimeOffset.UtcNow,
            ConfigurationItems = [new CachedNamedRecord { SysId = "ci-printer", Name = "HQ-PRINTER-01" }]
        };
        var store = new SecondLoadCatalogStore(saved);
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("configuration item open must not call ServiceNow"));
        using var client = ServiceNowClient.Create(session, handler, store);

        var field = new ReferenceChoiceField((live, token) => live.ListConfigurationItemsAsync(token), searchRemote: true);
        field.Use(client);
        await field.LoadAsync();

        Assert.Equal(2, store.Loads);
        Assert.Contains(field.Choices, choice => choice.Label == "HQ-PRINTER-01");
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SearchExpressionsPublishTheCachedPage()
    {
        using var client = Client();
        var field = Field(client,
        [
            new Choice("ci-printer", "HQ-PRINTER-01"),
            new Choice("ci-laptop", "LAPTOP-FIN-014"),
            new Choice("ci-vpn", "VPN-GATEWAY"),
            new Choice("ci-router", "VPN-ROUTER"),
            new Choice("ci-edge", "EDGE-GATEWAY")
        ]);
        await field.LoadAsync();

        field.Filter = "print*";
        await field.WhenReady;
        Assert.Contains(field.Choices, choice => choice.Label == "HQ-PRINTER-01");
        Assert.DoesNotContain(field.Choices, choice => choice.Label == "LAPTOP-FIN-014");

        field.Filter = "vpn + gateway";
        await field.WhenReady;
        Assert.Contains(field.Choices, choice => choice.Label == "VPN-GATEWAY");
        Assert.DoesNotContain(field.Choices, choice => choice.Label == "VPN-ROUTER");
        Assert.DoesNotContain(field.Choices, choice => choice.Label == "EDGE-GATEWAY");
    }

    [Fact]
    public async Task ServiceOfferingSearchUsesTheSameMatcher()
    {
        using var client = Client();
        var field = new ReferenceChoiceField((live, token) => Task.FromResult<IReadOnlyList<Choice>>(
        [
            new Choice("offering-print", "Printing"),
            new Choice("offering-network", "Network access")
        ]));
        field.Use(client);
        await field.LoadAsync();

        field.Filter = "print*";
        await field.WhenReady;

        Assert.Contains(field.Choices, choice => choice.Label == "Printing");
        Assert.DoesNotContain(field.Choices, choice => choice.Label == "Network access");
    }

    [Fact]
    public async Task MoreThanFiftyMatchesPublishAtMostFiftyRows()
    {
        using var client = Client();
        var rows = Enumerable.Range(0, 80)
            .Select(index => new Choice("ci-" + index.ToString("000"), "CI " + index.ToString("000")))
            .ToArray();
        var field = Field(client, rows);
        await field.LoadAsync();

        var published = field.Choices.Where(choice => choice.Value.Length > 0).ToList();
        Assert.True(field.MatchCount > ReferenceNameMatcher.PageSize);
        Assert.InRange(published.Count, 1, ReferenceNameMatcher.PageSize);
        Assert.Equal("50 of 80", field.MatchSummary);

        field.Filter = "CI 0";
        await field.WhenReady;
        published = field.Choices.Where(choice => choice.Value.Length > 0).ToList();
        Assert.True(field.MatchCount > ReferenceNameMatcher.PageSize);
        Assert.InRange(published.Count, 1, ReferenceNameMatcher.PageSize);
    }

    [Fact]
    public async Task TypingDoesNotQueryAndCommitLooksUpACacheMissOnce()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (!path.Contains("cmdb_ci", StringComparison.Ordinal))
                return Api.Json("""{"result":[]}""");
            return Api.Json("""{"result":[{"sys_id":"ci-switch","name":"CORE-SWITCH-02"}]}""");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var field = Field(client, [new Choice("ci-printer", "HQ-PRINTER-01")]);
        await field.LoadAsync();
        handler.Calls.Clear();

        field.Filter = "co";
        field.Filter = "core-switch";
        await field.WhenReady;

        Assert.Empty(handler.Calls);
        Assert.DoesNotContain(field.Choices, choice => choice.Value == "ci-switch");

        await field.CommitAsync();
        await field.CommitAsync();

        Assert.Single(handler.Calls);
        Assert.Contains(handler.Calls, call => call.PathAndQuery.Contains("cmdb_ci", StringComparison.Ordinal));
        Assert.Contains(field.Choices, choice => choice.Value == "ci-switch" && choice.Label == "CORE-SWITCH-02");
    }

    private static ServiceNowClient Client()
    {
        var handler = new StubHandler((_, _) => Api.Json("""{"result":[]}"""));
        return ServiceNowClient.Create(Api.BasicSession(), handler);
    }

    private static ReferenceChoiceField Field(ServiceNowClient client, IReadOnlyList<Choice> rows)
    {
        var field = new ReferenceChoiceField((_, _) => Task.FromResult(rows), searchRemote: true);
        field.Use(client);
        return field;
    }

    private sealed class SecondLoadCatalogStore : IFormCatalogStore
    {
        private readonly FormCatalogSnapshot _saved;

        public SecondLoadCatalogStore(FormCatalogSnapshot saved) => _saved = saved;

        public int Loads { get; private set; }

        public FormCatalogSnapshot? Load(Uri instanceUri)
        {
            Loads++;
            return Loads == 1 ? null : _saved;
        }

        public void Save(Uri instanceUri, FormCatalogSnapshot snapshot)
        {
        }
    }
}
