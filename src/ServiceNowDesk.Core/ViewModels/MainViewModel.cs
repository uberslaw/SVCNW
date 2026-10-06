using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IBrowserSignIn? _browserSignIn;
    private readonly IFormCatalogStore? _formCatalog;
    private readonly IDeskListStore? _lists;
    private readonly IDailyWorkStore _dailyWork;
    private readonly Func<ServiceNowSession, IFormCatalogStore?, ServiceNowClient>? _clientFactory;
    private readonly Stack<DeskSection> _returnStack = [];
    private IServiceNowClient? _client;
    private CancellationTokenSource? _searchCts;
    private readonly Dictionary<DeskSection, string> _loadedFor = [];
    private bool _openingRecord;
    private bool _preserveNavigation;
    private bool _suppressSearchText;
    private readonly AlertWatchState _watch = new();
    private readonly RowHighlighter _rows = new();
    private readonly SemaphoreSlim _alertGate = new(1, 1);
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private CancellationTokenSource? _alertCts;
    private int _alertGeneration;
    private string _signedInUserId = "";
    private int _sessionEpoch;

    public Task AssignmentDirectoryRefresh { get; private set; } = Task.CompletedTask;
    public StartupDownloadModel Startup { get; } = new();
    public ObservableCollection<CacheRowModel> Caches { get; } = new(
    [
        new CacheRowModel("incidents", "Incidents"),
        new CacheRowModel("requests", "Requests"),
        new CacheRowModel("request-items", "Request items"),
        new CacheRowModel("walk-ups", "Walk-ups"),
        new CacheRowModel("knowledge", "Knowledge"),
        new CacheRowModel("choices", "Choices (menus)"),
        new CacheRowModel("groups", "Assignment groups"),
        new CacheRowModel("members", "Assignment group members"),
        new CacheRowModel("service-offerings", "Service offerings"),
        new CacheRowModel("configuration-items", "Configuration items")
    ]);
    private bool _startupGate;
    private int _downloadBusy;
    private static readonly string[] StartupCacheKeys = ["choices", "groups", "members", "service-offerings", "configuration-items", "incidents", "requests", "walk-ups", "knowledge"];
    private int _knowledgeQuiet;

    public MainViewModel(
        ISettingsStore store,
        IDesktopServices desktop,
        IBrowserSignIn? browserSignIn = null,
        IFormCatalogStore? formCatalog = null,
        IIncidentTemplateStore? templates = null,
        Func<ServiceNowSession, IFormCatalogStore?, ServiceNowClient>? clientFactory = null,
        IRecentAssignmentGroupStore? recentGroups = null,
        IDeskListStore? lists = null,
        IDailyWorkStore? dailyWork = null)
    {
        _store = store;
        _browserSignIn = browserSignIn;
        _formCatalog = formCatalog;
        _clientFactory = clientFactory;
        _lists = lists;
        _dailyWork = dailyWork ?? new MemoryDailyWorkStore();
        Startup.Dismissed += (_, _) => _startupGate = false;
        var recent = recentGroups ?? new MemoryRecentAssignmentGroupStore();
        Connection = new ConnectionViewModel();
        Incidents = new IncidentWorkspaceViewModel(desktop, templates ?? new MemoryIncidentTemplateStore(), recent);
        Requests = new RequestWorkspaceViewModel(desktop);
        RequestedItems = new RequestedItemWorkspaceViewModel(desktop, recent);
        WalkUps = new InteractionWorkspaceViewModel(desktop, recent);
        WalkUps.IncidentRequested += (_, conversion) => ConvertOpenTask = OpenConvertedIncidentAsync(conversion);
        Search = new SearchWorkspaceViewModel();
        Knowledge = new KnowledgeWorkspaceViewModel();
        Catalog = new CatalogWorkspaceViewModel();
        Notifications = new NotificationWorkspaceViewModel();
        NotificationSettings = new NotificationSettingsViewModel();
        Legend = new LegendSettingsViewModel();
        Leads = new LeadsViewModel();
        DailyWork = new DailyWorkViewModel(_dailyWork);
        Leads.Board.OpenRequested += (_, row) => _ = OpenNotificationAsync(row);
        DailyWork.OpenRequested += (_, row) => _ = OpenDailyWorkAsync(row);
        Leads.TeamChanged += (_, _) =>
        {
            Connection.RememberLeadTeam(Leads.SelectedMemberIds);
            _store.Save(Connection.BuildSettings());
            if (IsConnected)
                RefreshAlerts();
        };
        Incidents.PrepareRow = _rows.Paint;
        Requests.PrepareRow = _rows.Paint;
        RequestedItems.PrepareRow = _rows.Paint;
        WalkUps.PrepareRow = _rows.Paint;
        Search.PrepareHit = _rows.Paint;
        Legend.Changed += (_, preferences) =>
        {
            if (preferences is null)
                return;
            Connection.RememberHighlights(preferences);
            _rows.Use(preferences);
            RepaintRows();
            Notifications.RememberViewer(_signedInUserId, preferences);
            Leads.Board.RememberViewer(_signedInUserId, preferences);
            _store.Save(Connection.BuildSettings());
        };
        Requests.RelatedItemRequested += (_, sysId) => _ = OpenRequestedItemAsync(sysId);
        Search.OpenRequested += (_, hit) => SearchOpenTask = OpenSearchResultAsync(hit);
        Search.KnowledgeSearchRequested += (_, _) => _ = RefreshKnowledgeQuietlyAsync();
        Catalog.RequestOrdered += (_, result) => _ = OpenOrderedRequestAsync(result);
        Notifications.OpenRequested += (_, row) => _ = OpenNotificationAsync(row);
        Notifications.QueueSelected += (_, _) =>
        {
            AcknowledgeNotifications();
            SelectedSection = DeskSection.Notifications;
        };
        NotificationSettings.SettingsChanged += (_, _) =>
        {
            Connection.RememberNotifications(NotificationSettings.Committed);
            _store.Save(Connection.BuildSettings());
            Leads.SetGroupName(NotificationSettings.Committed.WatchedGroupName);
            if (IsConnected)
            {
                StartAlertLoop();
                _ = LoadLeadRosterAsync();
            }
        };
        Connection.DownloadCachePreferenceChanged += (_, _) => _store.Save(Connection.BuildSettings());
    }

    public ConnectionViewModel Connection { get; }
    public IncidentWorkspaceViewModel Incidents { get; }
    public RequestWorkspaceViewModel Requests { get; }
    public RequestedItemWorkspaceViewModel RequestedItems { get; }
    public InteractionWorkspaceViewModel WalkUps { get; }
    public Task ConvertOpenTask { get; private set; } = Task.CompletedTask;
    public SearchWorkspaceViewModel Search { get; }
    public KnowledgeWorkspaceViewModel Knowledge { get; }
    public CatalogWorkspaceViewModel Catalog { get; }
    public NotificationWorkspaceViewModel Notifications { get; }
    public NotificationSettingsViewModel NotificationSettings { get; }
    public LegendSettingsViewModel Legend { get; }
    public LeadsViewModel Leads { get; }
    public DailyWorkViewModel DailyWork { get; }
    public Task SearchOpenTask { get; private set; } = Task.CompletedTask;
    public ObservableCollection<ApiActivity> Activity { get; } = [];

    [ObservableProperty] private DeskSection selectedSection = DeskSection.Connection;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string searchPlaceholder = "Search";
    [ObservableProperty] private string statusMessage = "Not connected.";
    [ObservableProperty] private string errorMessage = "";
    [ObservableProperty] private string connectedUser = "";
    [ObservableProperty] private string instanceLabel = "";
    [ObservableProperty] private string windowTitle = "ServiceNow Desk";
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool isSample;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool showBack;
    [ObservableProperty] private bool showRowLegend;

    public bool ResolvePanelOpen =>
        (SelectedSection == DeskSection.Incidents && Incidents.ShowResolvePanel)
        || (SelectedSection == DeskSection.Requests && Requests.ShowResolvePanel)
        || (SelectedSection == DeskSection.RequestedItems && RequestedItems.ShowResolvePanel)
        || (SelectedSection == DeskSection.WalkUps && WalkUps.ShowResolvePanel);

    public async Task InitializeAsync()
    {
        var settings = _store.Load();
        Connection.Load(settings);
        NotificationSettings.Load(Connection.Notifications);
        Legend.Load(Connection.Highlights);
        _rows.Use(Connection.Highlights);
        if (BrowserSignInClock.IsSavedSessionExpired(settings, DateTimeOffset.UtcNow))
        {
            AbandonExpiredBrowserSession();
            return;
        }

        if (ShouldAutoConnect(settings))
            await ConnectAsync();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        IServiceNowClient? created = null;
        var epoch = _sessionEpoch;
        try
        {
            IsBusy = true;
            ErrorMessage = "";
            epoch = ++_sessionEpoch;
            var settings = Connection.BuildSettings();
            ServiceNowClient? live = null;
            if (settings.UseSampleData)
            {
                created = new SampleServiceNowClient();
            }
            else
            {
                var session = ServiceNowSession.FromSettings(settings);
                live = _clientFactory is null
                    ? ServiceNowClient.Create(session, formCatalog: _formCatalog)
                    : _clientFactory(session, _formCatalog);
                WatchBrowserSession(live);
                created = live;
            }

            var user = await created.GetCurrentUserAsync(CancellationToken.None);
            ReplaceClient(created);
            created = null;
            BrowserSignInClock.Preserve(settings);
            Connection.SignedInAt = settings.SignedInAt;
            Connection.SessionCapturedAt = settings.SessionCapturedAt;
            Connection.SessionExpiresAt = settings.SessionExpiresAt;
            _store.Save(settings);
            ConnectedUser = user.Name;
            IsSample = settings.UseSampleData;
            InstanceLabel = settings.UseSampleData ? "Practice data" : ServiceNowSession.NormalizeInstance(settings.InstanceUrl).GetLeftPart(UriPartial.Authority);
            WindowTitle = "ServiceNow Desk — " + InstanceLabel;
            IsConnected = true;
            _signedInUserId = user.SysId;
            Notifications.RememberViewer(_signedInUserId, Connection.Highlights);
            Leads.Board.RememberViewer(_signedInUserId, Connection.Highlights);
            StatusMessage = settings.UseSampleData
                ? "Practice data loaded. Nothing is sent to ServiceNow."
                : "Connected as " + user.Name + ".";
            StartAlertLoop();
            _loadedFor.Clear();
            ShowSavedKnowledge();
            if (settings.UseSampleData && !Knowledge.HasArticles)
                await PrimeSampleKnowledgeAsync();
            _startupGate = true;
            var startup = DownloadStartupAsync(live);
            AssignmentDirectoryRefresh = startup;
            try
            {
                if (epoch == _sessionEpoch && SelectedSection == DeskSection.Connection)
                    SelectedSection = DeskSection.Incidents;
                await startup;
            }
            finally
            {
                _startupGate = false;
            }

            if (epoch == _sessionEpoch)
                _ = LoadLeadRosterAsync();

            if (epoch != _sessionEpoch)
                return;

            RefreshActivity();
        }
        catch (Exception ex)
        {
            created?.Dispose();
            if (_sessionEpoch != epoch)
                return;
            if (BrowserSignInClock.IsRejection(ex))
            {
                AbandonExpiredBrowserSession();
                return;
            }

            ErrorMessage = WorkspaceMessages.Describe(ex);
            StatusMessage = "Not connected.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignInWithBrowserAsync()
    {
        if (_browserSignIn is null)
        {
            ErrorMessage = "Browser sign-in is not available.";
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = "";
            Connection.UseSampleData = false;
            Connection.AuthMode = ServiceNowAuthMode.BrowserSession;
            var uri = ServiceNowSession.NormalizeInstance(Connection.InstanceUrl);
            var result = await _browserSignIn.SignInAsync(uri, CancellationToken.None);
            Connection.SessionCookie = result.CookieHeader;
            Connection.UserToken = result.UserToken;
            var clock = BrowserSignInClock.FromSignIn(DateTimeOffset.UtcNow, result.ExpiresInSeconds, result.ExpiresAt);
            Connection.SignedInAt = clock.SignedInAtUtc;
            Connection.SessionCapturedAt = clock.SignedInAtUtc;
            Connection.SessionExpiresAt = clock.ExpiresAtUtc;
            _store.Save(Connection.BuildSettings());
            await ConnectAsync();
        }
        catch (BrowserSignInCanceledException)
        {
            if (!IsConnected)
                StatusMessage = "Not connected.";
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
            if (!IsConnected)
                StatusMessage = "Not connected.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task PracticeAsync()
    {
        Connection.UseSampleData = true;
        return ConnectAsync();
    }

    [RelayCommand]
    private void Disconnect()
    {
        _sessionEpoch++;
        ClearSavedBrowserSignIn();
        DropConnection("Disconnected.");
    }

    [RelayCommand]
    private async Task RefreshActiveAsync()
    {
        if (!IsConnected || _client is null)
            return;

        var workspace = ActiveRecord;
        if (workspace is not null)
        {
            workspace.SearchText = SearchText;
            if (await workspace.ReloadAsync())
            {
                _loadedFor[SelectedSection] = SearchText;
                if (string.IsNullOrWhiteSpace(SearchText))
                    SaveWorkspaceList(SelectedSection, workspace);
            }
            else
                AbandonIfRejected(workspace.ErrorMessage);

            return;
        }

        switch (SelectedSection)
        {
            case DeskSection.Search:
                Search.MarkStale();
                await Search.RunAsync(_client, SearchText);
                AbandonIfRejected(Search.ErrorMessage);
                break;
            case DeskSection.Catalog:
                await Catalog.RunAsync(_client, SearchText);
                AbandonIfRejected(Catalog.ErrorMessage);
                break;
            case DeskSection.Notifications:
            case DeskSection.Leads:
            case DeskSection.DailyWork:
                RefreshAlerts();
                break;
            case DeskSection.Connection:
            case DeskSection.Settings:
                RefreshActivity();
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (!CanGoBack())
            return;

        _returnStack.Pop();
        _searchCts?.Cancel();
        if (!string.Equals(SearchText, Search.Query, StringComparison.Ordinal))
        {
            _suppressSearchText = true;
            SearchText = Search.Query;
            _suppressSearchText = false;
        }

        _preserveNavigation = true;
        SelectedSection = DeskSection.Search;
        _preserveNavigation = false;
        UpdateBack();
    }

    [RelayCommand]
    private async Task SaveActiveAsync()
    {
        if (ActiveRecord is not null)
            await ActiveRecord.SaveCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NewActive()
    {
        if (ActiveRecord is { AllowCreate: true })
            ActiveRecord.NewRecordCommand.Execute(null);
    }

    [RelayCommand]
    private void ResolveActive()
    {
        var workspace = ActiveRecord;
        if (workspace is null)
            return;
        if (workspace.ShowResolvePanel)
            workspace.ConfirmResolveCommand.Execute(null);
        else
            workspace.BeginResolveCommand.Execute(null);
    }

    [RelayCommand]
    private void CloseStartup() => Startup.Dismiss();

    [RelayCommand]
    private Task RefreshCacheAsync(CacheRowModel? row) => RefreshOneCacheAsync(row);

    [RelayCommand]
    private Task RefreshAllCaches() => RefreshEveryCacheAsync();

    [RelayCommand]
    private void DismissMainError() => ErrorMessage = "";

    [RelayCommand]
    private void CancelActive()
    {
        if (ActiveRecord?.ShowResolvePanel == true)
            ActiveRecord.CancelResolveCommand.Execute(null);
    }

    [RelayCommand]
    private void PostActive()
    {
        var workspace = ActiveRecord;
        if (workspace is null)
            return;
        if (workspace.ShowResolvePanel)
            workspace.ConfirmResolveCommand.Execute(null);
        else
            workspace.PostJournalCommand.Execute(null);
    }

    public void NoteActivity() => RefreshActivity();

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressSearchText)
            return;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        _ = DebouncedSearchAsync(token);
    }

    partial void OnSelectedSectionChanged(DeskSection value)
    {
        if (!_preserveNavigation)
            _returnStack.Clear();

        SearchPlaceholder = value switch
        {
            DeskSection.Incidents => "Search incidents",
            DeskSection.Requests => "Search requests",
            DeskSection.RequestedItems => "Search request items",
            DeskSection.WalkUps => "Search walk-up interactions",
            DeskSection.Search => "Search incidents, requests, items, walk-ups, and knowledge",
            DeskSection.Knowledge => "Filter knowledge articles",
            DeskSection.Catalog => "Search the catalog",
            DeskSection.Notifications => "Notifications",
            DeskSection.Leads => "Leads",
            DeskSection.DailyWork => "Daily work",
            DeskSection.Legend => "Legend",
            DeskSection.Settings => "Settings",
            _ => "Search"
        };
        ShowRowLegend = value is DeskSection.Incidents
            or DeskSection.Requests
            or DeskSection.RequestedItems
            or DeskSection.WalkUps
            or DeskSection.Search
            or DeskSection.Notifications
            or DeskSection.Leads
            or DeskSection.DailyWork;
        UpdateBack();
        if (IsConnected && !_openingRecord && !_preserveNavigation && !_startupGate)
            _ = EnsureSectionAsync();
    }

    private bool CanGoBack() =>
        _returnStack.Count > 0
        && _returnStack.Peek() == DeskSection.Search
        && SelectedSection is DeskSection.Incidents or DeskSection.Requests or DeskSection.RequestedItems or DeskSection.WalkUps or DeskSection.Knowledge;

    private void UpdateBack()
    {
        ShowBack = CanGoBack();
        BackCommand.NotifyCanExecuteChanged();
    }

    private RecordWorkspaceViewModel? ActiveRecord => SelectedSection switch
    {
        DeskSection.Incidents => Incidents,
        DeskSection.Requests => Requests,
        DeskSection.RequestedItems => RequestedItems,
        DeskSection.WalkUps => WalkUps,
        _ => null
    };

    private async Task DebouncedSearchAsync(CancellationToken token)
    {
        var section = SelectedSection;
        try
        {
            await Task.Delay(250, token);
            if (!IsConnected || SelectedSection != section)
                return;
            if (section == DeskSection.Knowledge)
            {
                Knowledge.FilterText = SearchText.Trim();
                return;
            }

            _loadedFor.Remove(section);
            await EnsureSectionAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EnsureSectionAsync()
    {
        if (_client is null)
            return;

        switch (SelectedSection)
        {
            case DeskSection.Incidents:
                await LoadRecordSectionAsync(DeskSection.Incidents, Incidents);
                break;
            case DeskSection.Requests:
                await LoadRecordSectionAsync(DeskSection.Requests, Requests);
                break;
            case DeskSection.RequestedItems:
                await LoadRecordSectionAsync(DeskSection.RequestedItems, RequestedItems);
                break;
            case DeskSection.WalkUps:
                await LoadRecordSectionAsync(DeskSection.WalkUps, WalkUps);
                break;
            case DeskSection.Search:
                if (!Search.HasCurrentResultsFor(SearchText))
                    await Search.RunAsync(_client, SearchText);
                break;
            case DeskSection.Knowledge:
                if (!Knowledge.HasArticles)
                    ShowSavedKnowledge();
                break;
            case DeskSection.Catalog:
                await Catalog.RunAsync(_client, SearchText);
                break;
            case DeskSection.Notifications:
                RefreshAlerts();
                break;
            case DeskSection.Leads:
                RefreshAlerts();
                _ = LoadLeadRosterAsync();
                break;
            case DeskSection.DailyWork:
                RefreshAlerts();
                break;
            case DeskSection.Connection:
                RefreshActivity();
                break;
        }
    }

    public void AcknowledgeNotifications()
    {
        _watch.Acknowledge();
        Notifications.RefreshAcknowledgement(_watch);
    }

    public Task OpenNotificationAsync(AlertRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return OpenHitAsync(new SearchHit
        {
            Section = row.Section,
            TableLabel = row.TableLabel,
            SysId = row.SysId,
            Number = row.Number,
            Title = row.Title,
            StateLabel = row.State,
            Tone = "open",
            Meta = row.Detail,
            When = row.Updated,
            SortKey = row.Updated
        }, fromSearch: false);
    }

    private Task OpenDailyWorkAsync(DailyWorkRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var table = row.Section switch
        {
            DeskSection.Incidents => "Incident",
            DeskSection.Requests => "Request",
            DeskSection.RequestedItems => "Request item",
            DeskSection.WalkUps => "Walk-up",
            _ => "Record"
        };
        return OpenHitAsync(new SearchHit
        {
            Section = row.Section,
            TableLabel = table,
            SysId = row.SysId,
            Number = row.Number,
            Title = row.Title,
            StateLabel = row.State,
            Tone = "open",
            Meta = row.Reasons,
            When = "",
            SortKey = row.Number
        }, fromSearch: false);
    }

    private async Task LoadRecordSectionAsync(DeskSection section, RecordWorkspaceViewModel workspace)
    {
        await workspace.EnsureChoicesAsync();
        if (workspace.HasLoaded && _loadedFor.TryGetValue(section, out var loaded) && loaded == SearchText)
            return;

        workspace.SearchText = SearchText;
        await workspace.RefreshAsync();
        _loadedFor[section] = SearchText;
        if (workspace.HasLoaded && string.IsNullOrWhiteSpace(SearchText))
            SaveWorkspaceList(section, workspace);
    }

    public Task OpenSearchResultAsync(SearchHit hit) => OpenHitAsync(hit, fromSearch: true);

    private async Task OpenHitAsync(SearchHit hit, bool fromSearch)
    {
        try
        {
            _searchCts?.Cancel();
            _openingRecord = true;
            _preserveNavigation = true;
            if (fromSearch)
            {
                _returnStack.Clear();
                _returnStack.Push(DeskSection.Search);
            }
            else
            {
                _returnStack.Clear();
            }

            SelectedSection = hit.Section;
            _preserveNavigation = false;
            UpdateBack();
            await EnsureSectionAsync();
            switch (hit.Section)
            {
                case DeskSection.Incidents:
                    await Incidents.OpenFromSearchAsync(hit.SysId);
                    break;
                case DeskSection.Requests:
                    await Requests.OpenFromSearchAsync(hit.SysId);
                    break;
                case DeskSection.RequestedItems:
                    await RequestedItems.OpenFromSearchAsync(hit.SysId);
                    break;
                case DeskSection.WalkUps:
                    await WalkUps.OpenFromSearchAsync(hit.SysId);
                    break;
                case DeskSection.Knowledge:
                    await Knowledge.OpenAsync(_client, hit.SysId);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(hit.Number))
                StatusMessage = "Opened " + hit.Number + ".";
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            _openingRecord = false;
            _preserveNavigation = false;
            UpdateBack();
        }
    }

    private Task OpenRequestedItemAsync(string sysId) =>
        OpenHitAsync(new SearchHit
        {
            Section = DeskSection.RequestedItems,
            TableLabel = "Request item",
            SysId = sysId,
            Number = "",
            Title = "",
            StateLabel = "",
            Tone = "open",
            Meta = "",
            When = "",
            SortKey = ""
        }, fromSearch: false);

    private async Task OpenOrderedRequestAsync(CatalogOrderResult result)
    {
        if (string.IsNullOrWhiteSpace(result.RequestSysId))
        {
            StatusMessage = "Catalog item ordered.";
            return;
        }

        await OpenHitAsync(new SearchHit
        {
            Section = DeskSection.Requests,
            TableLabel = "Request",
            SysId = result.RequestSysId,
            Number = result.RequestNumber,
            Title = "",
            StateLabel = "",
            Tone = "new",
            Meta = "",
            When = "",
            SortKey = ""
        }, fromSearch: false);
        StatusMessage = "Opened " + result.RequestNumber + ".";
    }

    private async Task OpenConvertedIncidentAsync(InteractionConversion conversion)
    {
        if (!string.IsNullOrWhiteSpace(conversion.LinkError))
            ErrorMessage = conversion.LinkError;

        await OpenHitAsync(new SearchHit
        {
            Section = DeskSection.Incidents,
            TableLabel = "Incident",
            SysId = conversion.Incident.SysId,
            Number = conversion.Incident.Number,
            Title = conversion.Incident.ShortDescription,
            StateLabel = conversion.Incident.StateLabel,
            Tone = StateTone.ForIncident(conversion.Incident.State),
            Meta = "",
            When = "",
            SortKey = ""
        }, fromSearch: false);

        StatusMessage = conversion.Created
            ? "Created " + conversion.Incident.Number + " from the walk-up."
            : "Opened " + conversion.Incident.Number + ", already linked to this walk-up.";
    }

    private Task<bool> DownloadStartupAsync(ServiceNowClient? live) =>
        RunDownloadAsync(live, StartupCacheKeys, force: Connection.DownloadCacheOnLaunch);

    private async Task BindGroupsAsync()
    {
        if (_client is null)
            return;
        Incidents.Assignment.Use(_client);
        RequestedItems.Assignment.Use(_client);
        WalkUps.Assignment.Use(_client);
        await Incidents.Assignment.LoadGroupsAsync();
        await RequestedItems.Assignment.LoadGroupsAsync();
        await WalkUps.Assignment.LoadGroupsAsync();
    }

    private async Task<bool> HasSavedGroupsAsync(ServiceNowClient client)
    {
        try
        {
            var groups = await client.ListAssignmentGroupsAsync(CancellationToken.None);
            return groups.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private IProgress<DownloadTick> SplashProgress()
    {
        if (_ui is null)
            return new InlineProgress(tick => Startup.Report(tick.Percent));
        return new Progress<DownloadTick>(tick => Startup.Report(tick.Percent));
    }

    private sealed class InlineProgress(Action<DownloadTick> report) : IProgress<DownloadTick>
    {
        public void Report(DownloadTick value) => report(value);
    }

    private static bool ShouldAutoConnect(DeskSettings settings)
    {
        if (settings.UseSampleData)
            return true;
        if (string.IsNullOrWhiteSpace(settings.InstanceUrl))
            return false;
        if (settings.AuthMode != ServiceNowAuthMode.BrowserSession)
            return true;

        return !string.IsNullOrWhiteSpace(settings.SessionCookie)
            && !string.IsNullOrWhiteSpace(settings.UserToken);
    }

    private void WatchBrowserSession(ServiceNowClient live)
    {
        var epoch = _sessionEpoch;
        live.BrowserSessionRejected += (_, _) => PostToUi(() =>
        {
            if (epoch != _sessionEpoch)
                return;
            AbandonExpiredBrowserSession();
        });
    }

    private void AbandonIfRejected(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        if (message.Contains("browser sign-in expired", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            AbandonExpiredBrowserSession();
    }

    private void AbandonExpiredBrowserSession()
    {
        _sessionEpoch++;
        ClearSavedBrowserSignIn();
        DropConnection(BrowserSignInClock.ExpiredStatus);
    }

    private void ClearSavedBrowserSignIn()
    {
        var hasBrowserSecret = !string.IsNullOrWhiteSpace(Connection.SessionCookie)
            || !string.IsNullOrWhiteSpace(Connection.UserToken)
            || Connection.SignedInAt is not null
            || Connection.SessionCapturedAt is not null
            || Connection.SessionExpiresAt is not null;
        if (!hasBrowserSecret)
            return;

        Connection.SessionCookie = "";
        Connection.UserToken = "";
        Connection.SignedInAt = null;
        Connection.SessionCapturedAt = null;
        Connection.SessionExpiresAt = null;
        _store.Save(Connection.BuildSettings());
    }

    private void DropConnection(string status)
    {
        _signedInUserId = "";
        Notifications.RememberViewer("", Connection.Highlights);
        Leads.Board.RememberViewer("", Connection.Highlights);
        Leads.Clear();
        DailyWork.Clear();
        ReplaceClient(null);
        IsConnected = false;
        IsSample = false;
        ConnectedUser = "";
        InstanceLabel = "";
        WindowTitle = "ServiceNow Desk";
        ErrorMessage = "";
        StatusMessage = status;
        SelectedSection = DeskSection.Connection;
        Activity.Clear();
        _loadedFor.Clear();
        ConnectCommand.NotifyCanExecuteChanged();
    }

    private void ReplaceClient(IServiceNowClient? client)
    {
        CancelAlertLoop();
        _watch.Reset();
        Notifications.Clear();
        Incidents.Detach();
        Requests.Detach();
        RequestedItems.Detach();
        WalkUps.Detach();
        Catalog.Attach(null);
        Search.Reset();
        Knowledge.Clear();
        _returnStack.Clear();
        UpdateBack();
        _client?.Dispose();
        _client = client;
        if (client is null)
            return;

        Incidents.Attach(client);
        Requests.Attach(client);
        RequestedItems.Attach(client);
        WalkUps.Attach(client);
        Catalog.Attach(client);
        Knowledge.Attach(client);
    }

    private void StartAlertLoop()
    {
        CancelAlertLoop();
        if (_client is null || string.IsNullOrWhiteSpace(_signedInUserId))
            return;

        var cts = new CancellationTokenSource();
        _alertCts = cts;
        var generation = _alertGeneration;
        var client = _client;
        var token = cts.Token;
        _ = Task.Run(() => AlertLoopAsync(client, generation, token));
    }

    private void CancelAlertLoop()
    {
        _alertGeneration++;
        var cts = _alertCts;
        _alertCts = null;
        if (cts is null)
            return;
        cts.Cancel();
        cts.Dispose();
    }

    private void RefreshAlerts()
    {
        if (_client is null || _alertCts is not { IsCancellationRequested: false } cts)
            return;
        var client = _client;
        var generation = _alertGeneration;
        CancellationToken token;
        try
        {
            token = cts.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ = Task.Run(() => PollAlertsOnceAsync(client, generation, token));
    }

    private async Task AlertLoopAsync(IServiceNowClient client, int generation, CancellationToken token)
    {
        while (!token.IsCancellationRequested && generation == _alertGeneration)
        {
            await PollAlertsOnceAsync(client, generation, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || generation != _alertGeneration)
                return;

            try
            {
                var seconds = Math.Max(NotificationPreferences.MinimumPollSeconds, NotificationSettings.Committed.PollSeconds);
                await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollAlertsOnceAsync(IServiceNowClient client, int generation, CancellationToken token)
    {
        try
        {
            await _alertGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (generation != _alertGeneration || !ReferenceEquals(client, _client))
                return;

            var search = NotificationSettings.Committed.ToSearch(_signedInUserId) with
            {
                TeamMemberIds = Connection.LeadTeamMemberIds
            };
            var report = await client.GetAlertReportAsync(search, token).ConfigureAwait(false);
            PostToUi(() =>
            {
                if (generation != _alertGeneration || !ReferenceEquals(client, _client))
                    return;
                Notifications.Apply(report.Personal, _watch);
                Leads.Show(report.Leads);
                DailyWork.Show(report.Daily, _signedInUserId, Connection.LeadTeamMemberIds);
                _rows.Use(report.Personal);
                RepaintRows();
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            PostToUi(() =>
            {
                if (generation != _alertGeneration || !ReferenceEquals(client, _client))
                    return;
                Notifications.NotePollError(WorkspaceMessages.Describe(ex));
            });
        }
        finally
        {
            _alertGate.Release();
        }
    }

    private void RepaintRows()
    {
        Paint(Incidents.Items);
        Paint(Requests.Items);
        Paint(Requests.RelatedItems);
        Paint(RequestedItems.Items);
        Paint(WalkUps.Items);
        foreach (var hit in Search.Results)
            _rows.Paint(hit);
    }

    private void Paint(IEnumerable<TicketRow> rows)
    {
        foreach (var row in rows)
            _rows.Paint(row);
    }

    private async Task LoadLeadRosterAsync()
    {
        var client = _client;
        if (client is null)
            return;

        var name = NotificationSettings.Committed.WatchedGroupName;
        try
        {
            var groups = await client.ListAssignmentGroupsAsync(CancellationToken.None).ConfigureAwait(false);
            var group = groups.FirstOrDefault(choice =>
                string.Equals(choice.Label, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(choice.Value, name, StringComparison.OrdinalIgnoreCase));
            var members = group is null
                ? Array.Empty<Choice>()
                : await client.ListGroupMembersAsync(group.Value, CancellationToken.None).ConfigureAwait(false);
            PostToUi(() =>
            {
                if (!ReferenceEquals(client, _client))
                    return;
                Leads.SetGroupName(name);
                Leads.SetRoster(members, Connection.LeadTeamMemberIds);
                Leads.RosterNote = group is null
                    ? "No assignment group matches the watched group name."
                    : members.Count == 0
                        ? "That group has no cached members yet. Refresh assignment group members under Settings, Cache."
                        : "";
            });
        }
        catch (Exception ex)
        {
            PostToUi(() =>
            {
                if (ReferenceEquals(client, _client))
                    Leads.RosterNote = WorkspaceMessages.Describe(ex);
            });
        }
    }

    private void PostToUi(Action action)
    {
        if (_ui is null || ReferenceEquals(SynchronizationContext.Current, _ui))
            action();
        else
            _ui.Post(_ => action(), null);
    }

    private void RefreshActivity()
    {
        Activity.Clear();
        if (_client is null)
            return;
        foreach (var entry in _client.RecentActivity)
            Activity.Add(entry);
    }
}
