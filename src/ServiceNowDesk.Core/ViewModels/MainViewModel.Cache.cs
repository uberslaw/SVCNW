using System.Globalization;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

public partial class MainViewModel
{
    private async Task RefreshOneCacheAsync(CacheRowModel? row)
    {
        if (row is null)
            return;
        if (!IsConnected)
        {
            row.ReportFailure("Connect before refreshing this cache.");
            return;
        }

        if (Interlocked.CompareExchange(ref _downloadBusy, 1, 0) != 0)
        {
            row.ReportFailure("A download is already running.");
            return;
        }

        try
        {
            row.Status = "";
            row.IsFailed = false;
            row.IsBusy = true;
            var live = _client as ServiceNowClient;
            Startup.Begin(1);
            if (_ui is not null)
                await Task.Yield();
            var run = await RunKeyedSectionAsync(live, row.Key, force: true);
            ApplyRowOutcome(row, run);
        }
        finally
        {
            row.IsBusy = false;
            Interlocked.Exchange(ref _downloadBusy, 0);
        }
    }

    private async Task RefreshEveryCacheAsync()
    {
        if (!IsConnected)
        {
            foreach (var row in Caches)
                row.ReportFailure("Connect before refreshing this cache.");
            return;
        }

        if (Interlocked.CompareExchange(ref _downloadBusy, 1, 0) != 0)
        {
            foreach (var row in Caches)
                row.ReportFailure("A download is already running.");
            return;
        }

        try
        {
            foreach (var row in Caches)
            {
                row.Status = "";
                row.IsFailed = false;
                row.IsBusy = true;
            }

            var live = _client as ServiceNowClient;
            Startup.Begin(Caches.Count);
            if (_ui is not null)
                await Task.Yield();
            foreach (var row in Caches)
            {
                var run = await RunKeyedSectionAsync(live, row.Key, force: true);
                ApplyRowOutcome(row, run);
                row.IsBusy = false;
            }

            TrySeedMixFromDeskCaches();
        }
        finally
        {
            foreach (var row in Caches)
                row.IsBusy = false;
            Interlocked.Exchange(ref _downloadBusy, 0);
        }
    }

    private static void ApplyRowOutcome(CacheRowModel row, SectionRun run)
    {
        if (run.Error is not null)
            row.ReportFailure(run.Error);
        else if (run.Cached)
            row.ReportCached(run.CapturedAt);
        else
            row.ReportSuccess(run.StaleCleared, run.FreshCount, run.CapturedAt, run.Note);
    }

    private async Task<bool> RunDownloadAsync(ServiceNowClient? live, IReadOnlyList<string> keys, bool force)
    {
        if (Interlocked.CompareExchange(ref _downloadBusy, 1, 0) != 0)
            return false;

        try
        {
            if (!force && keys.All(key => !NeedsDownload(live, key)))
            {
                Startup.Reset();
                await ApplyFreshCachesAsync(live);
                SyncCacheRowSummaries(live);
                TrySeedMixFromDeskCaches();
                return true;
            }

            Startup.Begin(keys.Count);
            if (_ui is not null)
                await Task.Yield();
            var mixLeft = new HashSet<string>(MixTicketCacheKeys, StringComparer.Ordinal);
            var mixFailed = false;
            foreach (var key in keys)
            {
                var run = await RunKeyedSectionAsync(live, key, force);
                if (!mixLeft.Remove(key))
                    continue;
                if (run.Error is not null)
                    mixFailed = true;
                if (mixLeft.Count == 0 && !mixFailed)
                    TrySeedMixFromDeskCaches();
            }

            SyncCacheRowSummaries(live);
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _downloadBusy, 0);
        }
    }

    private async Task<SectionRun> RunKeyedSectionAsync(ServiceNowClient? live, string key, bool force)
    {
        Startup.Start(LineName(key));
        if (_ui is not null)
        {
            await Task.Yield();
            if (IsSample)
                await Task.Delay(40);
        }

        try
        {
            var outcome = await RunKeyAsync(live, key, force);
            if (outcome.Note is not null)
                Startup.CompleteNoted(outcome.Note);
            else if (outcome.Cached)
                Startup.CompleteCached();
            else
                Startup.Complete();
            return new SectionRun(
                null,
                outcome.Note,
                outcome.Cached,
                outcome.StaleCleared,
                outcome.FreshCount,
                outcome.CapturedAt);
        }
        catch (Exception ex)
        {
            var message = WorkspaceMessages.Describe(ex);
            Startup.Fail(message);
            return new SectionRun(message, null);
        }
    }

    private Task<SectionOutcome> RunKeyAsync(ServiceNowClient? live, string key, bool force) => key switch
    {
        "choices" => DownloadChoicesAsync(live, force),
        "groups" => DownloadGroupsAsync(live, force),
        "members" => DownloadMembersAsync(live, force),
        "service-offerings" => DownloadServiceOfferingsAsync(live, force),
        "configuration-items" => DownloadConfigurationItemsAsync(live, force),
        "incidents" or "requests" or "request-items" or "walk-ups" => DownloadListAsync(key, force),
        "knowledge" => DownloadKnowledgeSectionAsync(force),
        _ => Task.FromResult(new SectionOutcome(false, null))
    };

    private async Task ApplyFreshCachesAsync(ServiceNowClient? live)
    {
        await LoadChoiceListsAsync();
        await BindGroupsAsync();
        await BindReferenceChoicesAsync();
        var snapshot = LoadLists();
        ApplyFreshList("incidents", snapshot);
        ApplyFreshList("requests", snapshot);
        ApplyFreshList("request-items", snapshot);
        ApplyFreshList("walk-ups", snapshot);
        ApplyFreshKnowledge(snapshot);
        if (live is null)
            RememberPracticeStamp("choices");
        SyncCacheRowSummaries(live);
        TrySeedMixFromDeskCaches();
    }

    private void SyncCacheRowSummaries(ServiceNowClient? live)
    {
        var lists = LoadLists();
        FormCatalogSnapshot? catalog = null;
        if (live is not null)
            catalog = live.ExportCatalog();
        else if (_formCatalog is not null
            && Uri.TryCreate(Connection.InstanceUrl, UriKind.Absolute, out var uri))
        {
            catalog = _formCatalog.Load(uri);
        }

        foreach (var row in Caches)
        {
            var at = CapturedAtFor(row.Key, lists, catalog);
            if (at != default)
                row.RememberGoodDownload(at);
        }
    }

    private static DateTimeOffset CapturedAtFor(string key, DeskListSnapshot? lists, FormCatalogSnapshot? catalog) =>
        key switch
        {
            "incidents" => lists?.Incidents?.CapturedAt ?? default,
            "requests" => lists?.Requests?.CapturedAt ?? default,
            "request-items" => lists?.RequestItems?.CapturedAt ?? default,
            "walk-ups" => lists?.WalkUps?.CapturedAt ?? default,
            "knowledge" => lists?.Knowledge?.CapturedAt ?? default,
            "choices" => FirstStamp(catalog?.CapturedAt ?? default, lists?.ChoicesCapturedAt ?? default),
            "groups" => FirstStamp(catalog?.DirectoryCapturedAt ?? default, lists?.GroupsCapturedAt ?? default),
            "members" => FirstStamp(catalog?.DirectoryCapturedAt ?? default, lists?.MembersCapturedAt ?? default),
            "service-offerings" => FirstStamp(
                catalog?.ServiceOfferingsCapturedAt ?? default,
                lists?.ServiceOfferingsCapturedAt ?? default),
            "configuration-items" => FirstStamp(
                catalog?.ConfigurationItemsCapturedAt ?? default,
                lists?.ConfigurationItemsCapturedAt ?? default),
            _ => default
        };

    private static DateTimeOffset FirstStamp(DateTimeOffset primary, DateTimeOffset fallback) =>
        primary != default ? primary : fallback;

    private bool NeedsDownload(ServiceNowClient? live, string key)
    {
        var snapshot = LoadLists();
        return key switch
        {
            "choices" => live is not null
                ? !(live.HasCachedChoices && !live.FormCatalogIsStale)
                : FormCatalogPolicy.IsStale(snapshot?.ChoicesCapturedAt ?? default, DateTimeOffset.UtcNow),
            "groups" or "members" => live is not null
                ? live.AssignmentDirectoryIsStale
                : FormCatalogPolicy.IsStale(
                    key == "groups" ? snapshot?.GroupsCapturedAt ?? default : snapshot?.MembersCapturedAt ?? default,
                    DateTimeOffset.UtcNow),
            "service-offerings" => live is not null
                ? live.ServiceOfferingsAreStale
                : FormCatalogPolicy.IsStale(snapshot?.ServiceOfferingsCapturedAt ?? default, DateTimeOffset.UtcNow),
            "configuration-items" => live is not null
                ? live.ConfigurationItemsAreStale
                : FormCatalogPolicy.IsStale(snapshot?.ConfigurationItemsCapturedAt ?? default, DateTimeOffset.UtcNow),
            _ => ListIsStale(ListFor(snapshot, key))
        };
    }

    private async Task<SectionOutcome> DownloadChoicesAsync(ServiceNowClient? live, bool force)
    {
        var cached = !force && !NeedsDownload(live, "choices");
        FormCatalogSnapshot? backup = null;
        var stale = 0;
        if (force && live is not null)
        {
            backup = live.ExportCatalog();
            stale = backup.Choices.Sum(list => list.Choices?.Count ?? 0);
            live.ClearChoiceCache();
        }

        if (force && live is null)
        {
            stale = CountPracticeStamp("choices") > 0 ? 1 : 0;
            ClearPracticeStamp("choices");
        }

        Exception? failure = null;
        if (live is not null && !cached)
        {
            try
            {
                await live.RefreshChoiceCatalogAsync(SplashProgress(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                failure = ex;
                if (backup is not null)
                    live.RestoreCatalog(backup);
            }
        }

        if (live is null && !cached)
            Startup.Report(0);
        await Incidents.ReloadChoiceListsAsync();
        if (live is null && !cached)
            Startup.Report(25);
        await Requests.ReloadChoiceListsAsync();
        if (live is null && !cached)
            Startup.Report(50);
        await RequestedItems.ReloadChoiceListsAsync();
        if (live is null && !cached)
            Startup.Report(75);
        await WalkUps.ReloadChoiceListsAsync();
        if (live is null && !cached)
            Startup.Report(100);
        if (failure is not null)
            throw failure;
        if (live is null && !cached)
            RememberPracticeStamp("choices");
        if (cached)
            return new SectionOutcome(true, null, CapturedAt: CapturedAtFor("choices", LoadLists(), live?.ExportCatalog()));
        var fresh = live is not null
            ? live.ExportCatalog().Choices.Sum(list => list.Choices?.Count ?? 0)
            : 1;
        return new SectionOutcome(false, null, stale, fresh, DateTimeOffset.UtcNow);
    }

    private async Task<SectionOutcome> DownloadGroupsAsync(ServiceNowClient? live, bool force)
    {
        var cached = !force && !NeedsDownload(live, "groups");
        FormCatalogSnapshot? backup = null;
        var stale = 0;
        if (force && live is not null)
        {
            backup = live.ExportCatalog();
            stale = backup.Groups.Count;
            live.ClearAssignmentGroupCache();
        }

        if (force && live is null)
        {
            stale = CountPracticeStamp("groups") > 0 ? 1 : 0;
            ClearPracticeStamp("groups");
        }

        try
        {
            if (live is not null && !cached)
                await live.DownloadAssignmentGroupsAsync(SplashProgress(), CancellationToken.None);
        }
        catch (Exception)
        {
            if (backup is not null)
                live!.RestoreCatalog(backup);
            await BindGroupsAsync();
            if (IsConnected && live is not null && await HasSavedGroupsAsync(live))
                StatusMessage = "Connected as " + ConnectedUser + ". Saved assignment lists are still in use.";
            throw;
        }

        await BindGroupsAsync();
        if (live is null && !cached)
            RememberPracticeStamp("groups");
        if (cached)
            return new SectionOutcome(true, null, CapturedAt: CapturedAtFor("groups", LoadLists(), live?.ExportCatalog()));
        var fresh = live is not null ? live.ExportCatalog().Groups.Count : Incidents.Assignment.Groups.Count;
        return new SectionOutcome(false, null, stale, fresh, DateTimeOffset.UtcNow);
    }

    private async Task<SectionOutcome> DownloadMembersAsync(ServiceNowClient? live, bool force)
    {
        var cached = !force && !NeedsDownload(live, "members");
        if (cached)
            return new SectionOutcome(true, null, CapturedAt: CapturedAtFor("members", LoadLists(), live?.ExportCatalog()));

        FormCatalogSnapshot? backup = null;
        var stale = 0;
        if (force && live is not null)
        {
            backup = live.ExportCatalog();
            stale = backup.Members.Count;
            live.ClearAssignmentMemberCache();
        }

        if (force && live is null)
        {
            stale = CountPracticeStamp("members") > 0 ? 1 : 0;
            ClearPracticeStamp("members");
        }

        if (live is not null)
        {
            try
            {
                await live.DownloadAssignmentMembersAsync(SplashProgress(), CancellationToken.None);
                var fresh = live.ExportCatalog().Members.Count;
                return new SectionOutcome(false, null, stale, fresh, DateTimeOffset.UtcNow);
            }
            catch (Exception)
            {
                if (backup is not null)
                    live.RestoreCatalog(backup);
                if (IsConnected && await HasSavedGroupsAsync(live))
                    StatusMessage = "Connected as " + ConnectedUser + ". Saved assignment lists are still in use.";
                throw;
            }
        }

        var groups = Incidents.Assignment.Groups.Where(choice => !string.IsNullOrEmpty(choice.Value)).ToArray();
        var done = 0;
        Startup.Report(0);
        foreach (var group in groups)
        {
            if (_client is null)
                break;
            await _client.ListGroupMembersAsync(group.Value, CancellationToken.None);
            done++;
            var total = Math.Max(groups.Length, 1);
            Startup.Report(done * 100 / total);
        }

        RememberPracticeStamp("members");
        return new SectionOutcome(false, null, stale, done, DateTimeOffset.UtcNow);
    }

    private Task<SectionOutcome> DownloadServiceOfferingsAsync(ServiceNowClient? live, bool force) =>
        DownloadReferenceListAsync(
            live,
            force,
            "service-offerings",
            client => client.ClearServiceOfferingCache(),
            (client, progress, token) => client.DownloadServiceOfferingsAsync(progress, token),
            FormCatalogPolicy.MaxServiceOfferings);

    private Task<SectionOutcome> DownloadConfigurationItemsAsync(ServiceNowClient? live, bool force) =>
        DownloadReferenceListAsync(
            live,
            force,
            "configuration-items",
            client => client.ClearConfigurationItemCache(),
            (client, progress, token) => client.DownloadConfigurationItemsAsync(progress, token),
            FormCatalogPolicy.MaxConfigurationItems);

    private async Task<SectionOutcome> DownloadReferenceListAsync(
        ServiceNowClient? live,
        bool force,
        string key,
        Action<ServiceNowClient> clear,
        Func<ServiceNowClient, IProgress<DownloadTick>, CancellationToken, Task<ReferenceDownload>> download,
        int cap)
    {
        var cached = !force && !NeedsDownload(live, key);
        FormCatalogSnapshot? backup = null;
        var stale = 0;
        if (force && live is not null)
        {
            backup = live.ExportCatalog();
            stale = key == "service-offerings" ? backup.ServiceOfferings.Count : backup.ConfigurationItems.Count;
            clear(live);
        }

        if (force && live is null)
        {
            stale = CountPracticeStamp(key) > 0 ? 1 : 0;
            ClearPracticeStamp(key);
        }

        string? note = null;
        var fresh = 0;
        try
        {
            if (live is not null && !cached)
            {
                var downloaded = await download(live, SplashProgress(), CancellationToken.None);
                fresh = downloaded.Count;
                if (downloaded.Truncated || downloaded.Count >= cap)
                    note = downloaded.Count.ToString(CultureInfo.InvariantCulture)
                        + " saved; stopped at the "
                        + cap.ToString(CultureInfo.InvariantCulture)
                        + " limit";
            }
        }
        catch (Exception)
        {
            if (backup is not null)
                live!.RestoreCatalog(backup);
            await BindReferenceChoicesAsync();
            throw;
        }

        await BindReferenceChoicesAsync();
        if (live is null && !cached)
        {
            RememberPracticeStamp(key);
            fresh = 1;
        }

        if (cached)
            return new SectionOutcome(true, null, CapturedAt: CapturedAtFor(key, LoadLists(), live?.ExportCatalog()));
        return new SectionOutcome(false, note, stale, fresh, DateTimeOffset.UtcNow);
    }

    private async Task BindReferenceChoicesAsync()
    {
        if (_client is null)
            return;
        Incidents.ServiceOffering.Use(_client);
        Incidents.ConfigurationItem.Use(_client);
        RequestedItems.ServiceOffering.Use(_client);
        RequestedItems.ConfigurationItem.Use(_client);
        await Incidents.ServiceOffering.LoadAsync();
        await Incidents.ConfigurationItem.LoadAsync();
        await RequestedItems.ServiceOffering.LoadAsync();
        await RequestedItems.ConfigurationItem.LoadAsync();
    }

    private async Task<SectionOutcome> DownloadListAsync(string key, bool force)
    {
        if (_client is null)
            return new SectionOutcome(false, null);

        var section = SectionFor(key);
        var workspace = WorkspaceFor(key);
        if (!force && TryApplyFreshList(key))
        {
            var cached = ListFor(LoadLists(), key);
            return new SectionOutcome(true, null, CapturedAt: cached?.CapturedAt);
        }

        CachedTicketList? previous = null;
        var stale = 0;
        if (force)
        {
            previous = ClearList(key);
            stale = previous?.Items.Count ?? 0;
        }
        else
        {
            stale = ListFor(LoadLists(), key)?.Items.Count ?? 0;
        }

        try
        {
            Startup.Report(0);
            workspace.SearchText = force ? "" : SearchText;
            if (!await workspace.ReloadAsync())
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(workspace.ErrorMessage)
                    ? "The list could not be downloaded."
                    : workspace.ErrorMessage);

            _loadedFor[section] = workspace.SearchText;
            if (string.IsNullOrWhiteSpace(workspace.SearchText))
                SaveWorkspaceList(section, workspace);
            var captured = DateTimeOffset.UtcNow;
            return new SectionOutcome(false, null, stale, workspace.Items.Count, captured);
        }
        catch
        {
            if (force)
            {
                RestoreList(key, previous);
                if (previous is not null)
                    ApplyList(key, previous);
            }

            throw;
        }
    }

    private async Task LoadChoiceListsAsync()
    {
        await Incidents.ReloadChoiceListsAsync();
        await Requests.ReloadChoiceListsAsync();
        await RequestedItems.ReloadChoiceListsAsync();
        await WalkUps.ReloadChoiceListsAsync();
    }

    private bool TryApplyFreshList(string key)
    {
        var list = ListFor(LoadLists(), key);
        if (list is null || ListIsStale(list))
            return false;
        ApplyList(key, list);
        return true;
    }

    private readonly record struct SectionOutcome(
        bool Cached,
        string? Note,
        int StaleCleared = 0,
        int FreshCount = 0,
        DateTimeOffset? CapturedAt = null);

    private readonly record struct SectionRun(
        string? Error,
        string? Note,
        bool Cached = false,
        int StaleCleared = 0,
        int FreshCount = 0,
        DateTimeOffset? CapturedAt = null);

    private void ApplyFreshList(string key, DeskListSnapshot? snapshot)
    {
        var list = ListFor(snapshot, key);
        if (list is null || ListIsStale(list))
            return;
        ApplyList(key, list);
    }

    private void ApplyList(string key, CachedTicketList list)
    {
        var rows = list.Items.Select(ToTicket).ToArray();
        WorkspaceFor(key).ShowCachedRows(rows, list.TotalCount > 0 ? list.TotalCount : rows.Length);
        _loadedFor[SectionFor(key)] = "";
    }

    private void SaveWorkspaceList(DeskSection section, RecordWorkspaceViewModel workspace)
    {
        if (_lists is null)
            return;
        var snapshot = LoadLists() ?? new DeskListSnapshot();
        var list = new CachedTicketList
        {
            CapturedAt = DateTimeOffset.UtcNow,
            TotalCount = workspace.TotalCount,
            Items = workspace.Items.Select(FromTicket).ToList()
        };
        AssignList(snapshot, KeyFor(section), list);
        _lists.Save(CacheScope(), snapshot);
    }

    private CachedTicketList? ClearList(string key)
    {
        if (_lists is null)
            return null;
        var snapshot = LoadLists() ?? new DeskListSnapshot();
        var previous = ListFor(snapshot, key);
        AssignList(snapshot, key, null);
        _lists.Save(CacheScope(), snapshot);
        return previous;
    }

    private void RestoreList(string key, CachedTicketList? previous)
    {
        if (_lists is null)
            return;
        var snapshot = LoadLists() ?? new DeskListSnapshot();
        AssignList(snapshot, key, previous);
        _lists.Save(CacheScope(), snapshot);
    }

    private void RememberPracticeStamp(string key)
    {
        if (_lists is null || _client is ServiceNowClient)
            return;
        var snapshot = LoadLists() ?? new DeskListSnapshot();
        var now = DateTimeOffset.UtcNow;
        switch (key)
        {
            case "choices":
                snapshot.ChoicesCapturedAt = now;
                break;
            case "groups":
                snapshot.GroupsCapturedAt = now;
                break;
            case "members":
                snapshot.MembersCapturedAt = now;
                break;
            case "service-offerings":
                snapshot.ServiceOfferingsCapturedAt = now;
                break;
            case "configuration-items":
                snapshot.ConfigurationItemsCapturedAt = now;
                break;
        }

        _lists.Save(DeskListScope.Practice, snapshot);
    }

    private void ClearPracticeStamp(string key)
    {
        if (_lists is null || _client is ServiceNowClient)
            return;
        var snapshot = LoadLists() ?? new DeskListSnapshot();
        switch (key)
        {
            case "choices":
                snapshot.ChoicesCapturedAt = default;
                break;
            case "groups":
                snapshot.GroupsCapturedAt = default;
                break;
            case "members":
                snapshot.MembersCapturedAt = default;
                break;
            case "service-offerings":
                snapshot.ServiceOfferingsCapturedAt = default;
                break;
            case "configuration-items":
                snapshot.ConfigurationItemsCapturedAt = default;
                break;
        }

        _lists.Save(DeskListScope.Practice, snapshot);
    }

    private int CountPracticeStamp(string key)
    {
        var snapshot = LoadLists();
        if (snapshot is null)
            return 0;
        var at = key switch
        {
            "choices" => snapshot.ChoicesCapturedAt,
            "groups" => snapshot.GroupsCapturedAt,
            "members" => snapshot.MembersCapturedAt,
            "service-offerings" => snapshot.ServiceOfferingsCapturedAt,
            "configuration-items" => snapshot.ConfigurationItemsCapturedAt,
            _ => default
        };
        return at == default ? 0 : 1;
    }

    private DeskListSnapshot? LoadLists()
    {
        if (_lists is null)
            return null;
        return _lists.Load(CacheScope());
    }

    private string CacheScope()
    {
        if (_client is ServiceNowClient live && live.InstanceUri is Uri uri)
            return DeskListScope.ForInstance(uri);
        return DeskListScope.Practice;
    }

    private RecordWorkspaceViewModel WorkspaceFor(string key) => key switch
    {
        "incidents" => Incidents,
        "requests" => Requests,
        "request-items" => RequestedItems,
        "walk-ups" => WalkUps,
        _ => throw new InvalidOperationException("No list is cached for " + key + ".")
    };

    private static DeskSection SectionFor(string key) => key switch
    {
        "incidents" => DeskSection.Incidents,
        "requests" => DeskSection.Requests,
        "request-items" => DeskSection.RequestedItems,
        "walk-ups" => DeskSection.WalkUps,
        _ => throw new InvalidOperationException("No list is cached for " + key + ".")
    };

    private static string KeyFor(DeskSection section) => section switch
    {
        DeskSection.Incidents => "incidents",
        DeskSection.Requests => "requests",
        DeskSection.RequestedItems => "request-items",
        DeskSection.WalkUps => "walk-ups",
        _ => ""
    };

    private static string LineName(string key) => key switch
    {
        "choices" => "Choices",
        "groups" => "Assignment groups",
        "members" => "Assignment group members",
        "service-offerings" => "Service offerings",
        "configuration-items" => "Configuration items",
        "incidents" => "Incidents",
        "requests" => "Requests",
        "request-items" => "Request items",
        "walk-ups" => "Walk-ups",
        "knowledge" => "Knowledge",
        _ => key
    };

    private static bool ListIsStale(CachedTicketList? list) =>
        list is null
        || list.Items.Count == 0
        || FormCatalogPolicy.IsStale(list.CapturedAt, DateTimeOffset.UtcNow);

    private static CachedTicketList? ListFor(DeskListSnapshot? snapshot, string key) => key switch
    {
        "incidents" => snapshot?.Incidents,
        "requests" => snapshot?.Requests,
        "request-items" => snapshot?.RequestItems,
        "walk-ups" => snapshot?.WalkUps,
        "knowledge" => snapshot?.Knowledge,
        _ => null
    };

    private static void AssignList(DeskListSnapshot snapshot, string key, CachedTicketList? list)
    {
        switch (key)
        {
            case "incidents":
                snapshot.Incidents = list;
                break;
            case "requests":
                snapshot.Requests = list;
                break;
            case "request-items":
                snapshot.RequestItems = list;
                break;
            case "walk-ups":
                snapshot.WalkUps = list;
                break;
            case "knowledge":
                snapshot.Knowledge = list;
                break;
        }
    }

    private async Task<SectionOutcome> DownloadKnowledgeSectionAsync(bool force)
    {
        if (!force && TryApplyFreshKnowledge())
        {
            var cached = ListFor(LoadLists(), "knowledge");
            return new SectionOutcome(true, null, CapturedAt: cached?.CapturedAt);
        }

        var stale = force ? ListFor(LoadLists(), "knowledge")?.Items.Count ?? 0 : 0;
        var outcome = await FetchKnowledgeAsync(reportSplash: true);
        return outcome with
        {
            StaleCleared = stale,
            FreshCount = Knowledge.Articles.Count,
            CapturedAt = outcome.CapturedAt ?? DateTimeOffset.UtcNow
        };
    }

    private bool TryApplyFreshKnowledge()
    {
        var list = ListFor(LoadLists(), "knowledge");
        if (list is null || ListIsStale(list))
            return false;
        Knowledge.ShowArticles(list.Items.Select(KnowledgeListRow.FromCached));
        return true;
    }

    private void ApplyFreshKnowledge(DeskListSnapshot? snapshot)
    {
        var list = ListFor(snapshot, "knowledge");
        if (list is null || ListIsStale(list))
            return;
        Knowledge.ShowArticles(list.Items.Select(KnowledgeListRow.FromCached));
    }

    private void ShowSavedKnowledge()
    {
        var list = ListFor(LoadLists(), "knowledge");
        if (list?.Items is not { Count: > 0 })
            return;
        Knowledge.ShowArticles(list.Items.Select(KnowledgeListRow.FromCached));
    }

    private async Task PrimeSampleKnowledgeAsync()
    {
        if (_client is null)
            return;
        try
        {
            await FetchKnowledgeAsync(reportSplash: false);
        }
        catch (Exception ex)
        {
            Knowledge.NoteListFailure(WorkspaceMessages.Describe(ex));
        }
    }

    /// <summary>
    /// After connect, refresh knowledge off the splash path. Saved articles stay on screen;
    /// a failed refresh keeps the previous copy. Search uses the same quiet download.
    /// </summary>
    private void StartBackgroundKnowledgeRefresh()
    {
        if (_client is null)
            return;
        // Same force rule as splash caches: download on launch when the setting is on,
        // otherwise only when the saved list is missing or older than a day.
        if (!Connection.DownloadCacheOnLaunch && !NeedsDownload(null, "knowledge"))
            return;
        _ = RefreshKnowledgeQuietlyAsync();
    }

    private async Task RefreshKnowledgeQuietlyAsync()
    {
        if (_client is null || Volatile.Read(ref _downloadBusy) != 0)
            return;
        if (Interlocked.CompareExchange(ref _knowledgeQuiet, 1, 0) != 0)
            return;
        try
        {
            if (Volatile.Read(ref _downloadBusy) != 0)
                return;
            await FetchKnowledgeAsync(reportSplash: false);
        }
        catch (Exception ex)
        {
            if (!Knowledge.HasArticles)
                Knowledge.NoteListFailure(WorkspaceMessages.Describe(ex));
        }
        finally
        {
            Interlocked.Exchange(ref _knowledgeQuiet, 0);
        }
    }

    private async Task<SectionOutcome> FetchKnowledgeAsync(bool reportSplash)
    {
        if (_client is null)
            return new SectionOutcome(false, null);

        if (reportSplash)
            Startup.Report(0);
        var downloaded = await _client.DownloadKnowledgeAsync(reportSplash ? SplashProgress() : null, CancellationToken.None);
        var rows = downloaded.Articles.Select(KnowledgeListRow.FromArticle).ToArray();
        SaveKnowledge(rows);
        Knowledge.ShowArticles(rows);
        var captured = DateTimeOffset.UtcNow;
        if (!downloaded.Truncated)
            return new SectionOutcome(false, null, FreshCount: rows.Length, CapturedAt: captured);

        var note = rows.Length.ToString(CultureInfo.InvariantCulture)
            + " saved; stopped at the "
            + FormCatalogPolicy.MaxKnowledgeArticles.ToString(CultureInfo.InvariantCulture)
            + " limit";
        return new SectionOutcome(false, note, FreshCount: rows.Length, CapturedAt: captured);
    }

    private void SaveKnowledge(IReadOnlyList<KnowledgeListRow> rows)
    {
        if (_lists is null)
            return;
        var snapshot = LoadLists() ?? new DeskListSnapshot();
        snapshot.Knowledge = new CachedTicketList
        {
            CapturedAt = DateTimeOffset.UtcNow,
            TotalCount = rows.Count,
            Items = rows.Select(row => row.ToCached()).ToList()
        };
        _lists.Save(CacheScope(), snapshot);
    }

    private static CachedTicketRow FromTicket(TicketRow row) => new()
    {
        SysId = row.SysId,
        Number = row.Number,
        Title = row.Title,
        StateLabel = row.StateLabel,
        Tone = row.Tone,
        Meta = row.Meta,
        When = row.When,
        Badge = row.Badge
    };

    private static TicketRow ToTicket(CachedTicketRow row) => new()
    {
        SysId = row.SysId,
        Number = row.Number,
        Title = row.Title,
        StateLabel = row.StateLabel,
        Tone = row.Tone,
        Meta = row.Meta,
        When = row.When,
        Badge = row.Badge
    };
}
