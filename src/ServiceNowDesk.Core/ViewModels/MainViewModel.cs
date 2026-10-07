using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Navigation;
using ServiceNowDesk.Services;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IBrowserSignIn? _browserSignIn;
    private readonly IFormCatalogStore? _formCatalog;
    private readonly IDeskListStore? _lists;
    private readonly IDailyWorkStore _dailyWork;
    private readonly Func<ServiceNowSession, IFormCatalogStore?, ServiceNowClient>? _clientFactory;
    private readonly Func<IServiceNowClient>? _sampleClientFactory;
    private int _convertingNote;
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
    private readonly SemaphoreSlim _queueGate = new(1, 1);
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private CancellationTokenSource? _alertCts;
    private CancellationTokenSource? _queueCts;
    private int _alertGeneration;
    private int _queueGeneration;
    private string _signedInUserId = "";
    private string _signedInUserLocation = "";
    private int _mixOpenGeneration;
    private int _sessionEpoch;

    public Task AssignmentDirectoryRefresh { get; private set; } = Task.CompletedTask;

    public bool GroupQueueActive => _queueCts is { IsCancellationRequested: false };
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
        IDailyWorkStore? dailyWork = null,
        IPersonalTaskStore? personalTasks = null,
        Func<IServiceNowClient>? sampleClientFactory = null)
    {
        _store = store;
        _browserSignIn = browserSignIn;
        _formCatalog = formCatalog;
        _clientFactory = clientFactory;
        _sampleClientFactory = sampleClientFactory;
        _lists = lists;
        _dailyWork = dailyWork ?? new MemoryDailyWorkStore();
        Startup.Dismissed += (_, _) => _startupGate = false;
        var recent = recentGroups ?? new MemoryRecentAssignmentGroupStore();
        Connection = new ConnectionViewModel();
        Incidents = new IncidentWorkspaceViewModel(desktop, templates ?? new MemoryIncidentTemplateStore(), recent);
        Hardware = new HardwareWorkspaceViewModel(store);
        Hardware.DefaultSaved += (_, _) =>
            Connection.RememberHardwareOffices(_store.Load().HardwareOfficeLocations);
        Requests = new RequestWorkspaceViewModel(desktop);
        RequestedItems = new RequestedItemWorkspaceViewModel(desktop, recent);
        WalkUps = new InteractionWorkspaceViewModel(desktop, recent);
        WalkUps.IncidentRequested += (_, conversion) => ConvertOpenTask = OpenConvertedIncidentAsync(conversion);
        Mix = new MixWorkspaceViewModel();
        Mix.OpenRequested += (_, row) => MixOpenTask = OpenMixRowAsync(row);
        Search = new SearchWorkspaceViewModel();
        Knowledge = new KnowledgeWorkspaceViewModel(desktop);
        Catalog = new CatalogWorkspaceViewModel();
        Notifications = new NotificationWorkspaceViewModel();
        NotificationSettings = new NotificationSettingsViewModel();
        Legend = new LegendSettingsViewModel();
        Leads = new LeadsViewModel();
        DailyWork = new DailyWorkViewModel(_dailyWork, personalTasks ?? new MemoryPersonalTaskStore());
        Leads.Board.OpenRequested += (_, row) => _ = OpenNotificationAsync(row);
        DailyWork.OpenRequested += (_, row) => _ = OpenDailyWorkAsync(row);
        DailyWork.IncidentRequested += (_, row) => NoteConvertTask = ConvertNoteAsync(row, incident: true);
        DailyWork.RequestedItemRequested += (_, row) => NoteConvertTask = ConvertNoteAsync(row, incident: false);
        Leads.TeamChanged += (_, _) =>
        {
            Connection.RememberLeadTeam(Leads.SelectedMemberIds);
            _store.Save(Connection.BuildSettings());
            if (IsConnected)
                RefreshAlerts();
        };
        Leads.WorkEffortRequested += (_, _) => _ = LoadWorkEffortAsync(force: false);
        Leads.WorkEffort.RefreshRequested += (_, _) => _ = LoadWorkEffortAsync(force: true);
        Leads.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LeadsViewModel.Area) && SelectedSection == DeskSection.Leads)
                ShowRowLegend = Leads.Area != LeadArea.WorkEffort;
        };
        Incidents.PrepareRow = _rows.Paint;
        Requests.PrepareRow = _rows.Paint;
        RequestedItems.PrepareRow = _rows.Paint;
        WalkUps.PrepareRow = _rows.Paint;
        Mix.PrepareRow = _rows.Paint;
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
        RequestedItems.CreateNewRequested += (_, _) => TrySelect(DeskNavigation.OrderCatalogSection);
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
                ApplyOfficeCities();
                StartAlertLoop();
                _ = LoadLeadRosterAsync();
                if (SelectedSection is DeskSection.Incidents or DeskSection.RequestedItems or DeskSection.WalkUps or DeskSection.InTheMix)
                    _ = EnsureSectionAsync();
            }
        };
        NotificationSettings.JiggleSpeedChanged += (_, _) =>
        {
            Connection.Notifications.JiggleSpeed = NotificationSettings.ActiveJiggleSpeed;
            _store.Save(Connection.BuildSettings());
        };
        Connection.DownloadCachePreferenceChanged += (_, _) => _store.Save(Connection.BuildSettings());
        Connection.LeadsAccessChanged += (_, _) =>
        {
            _store.Save(Connection.BuildSettings());
            if (!Connection.LeadsEnabled && SelectedSection == DeskSection.Leads)
                SelectedSection = DeskSection.Incidents;
        };
        Connection.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ConnectionViewModel.LeadsEnabled))
                RebuildNavigation();
        };
        RebuildNavigation();
    }

    public ObservableCollection<DeskNavEntry> Navigation { get; } = [];

    private void RebuildNavigation()
    {
        var selected = SelectedSection;
        Navigation.Clear();
        foreach (var item in DeskNavigation.Visible(Connection.LeadsEnabled))
            Navigation.Add(new DeskNavEntry(item, selected == item.Section, SelectNavigation));
    }

    private void SelectNavigation(DeskSection section)
    {
        if (!TrySelect(section))
            SyncNavigationSelection();
    }

    private void SyncNavigationSelection()
    {
        foreach (var entry in Navigation)
            entry.ShowSelected(entry.Section == SelectedSection);
    }

    /// <summary>
    /// Moves to a section. Leads is refused while it is locked, so a shortcut for that page does nothing.
    /// </summary>
    public bool TrySelect(DeskSection section)
    {
        if (section == DeskSection.Leads && !Connection.LeadsEnabled)
            return false;

        SelectedSection = section;
        return true;
    }

    public ConnectionViewModel Connection { get; }
    public IncidentWorkspaceViewModel Incidents { get; }
    public HardwareWorkspaceViewModel Hardware { get; }
    public RequestWorkspaceViewModel Requests { get; }
    public RequestedItemWorkspaceViewModel RequestedItems { get; }
    public InteractionWorkspaceViewModel WalkUps { get; }
    public MixWorkspaceViewModel Mix { get; }
    public Task MixOpenTask { get; private set; } = Task.CompletedTask;
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
    public Task NoteConvertTask { get; private set; } = Task.CompletedTask;
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
        || (SelectedSection == DeskSection.WalkUps && WalkUps.ShowResolvePanel)
        || (SelectedSection == DeskSection.InTheMix && Mix.Editor?.ShowResolvePanel == true);

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
                created = _sampleClientFactory?.Invoke() ?? new SampleServiceNowClient();
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
            Hardware.RememberViewer(user);
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
            _signedInUserLocation = user.Location ?? "";
            ApplyOfficeCities();
            Catalog.RememberSignedInUser(user);
            Notifications.RememberViewer(_signedInUserId, Connection.Highlights);
            Leads.Board.RememberViewer(_signedInUserId, Connection.Highlights);
            StatusMessage = settings.UseSampleData
                ? "Practice data loaded. Nothing is sent to ServiceNow."
                : "Connected as " + user.Name + ".";
            StartAlertLoop();
            StartGroupQueueLoop();
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
            {
                _ = LoadLeadRosterAsync();
                if (SelectedSection == DeskSection.Leads && Leads.Area == LeadArea.WorkEffort)
                    _ = LoadWorkEffortAsync(force: false);
            }

            if (epoch != _sessionEpoch)
                return;

            _ = Knowledge.RefreshPublishedCountAsync();
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

        if (SelectedSection == DeskSection.InTheMix)
        {
            Mix.SearchText = SearchText;
            await Mix.RefreshAsync();
            if (string.IsNullOrEmpty(Mix.ErrorMessage))
                _loadedFor[DeskSection.InTheMix] = SearchText;
            else
                AbandonIfRejected(Mix.ErrorMessage);
            return;
        }

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

        if (SelectedSection == DeskSection.Hardware)
        {
            Hardware.SearchText = SearchText;
            await Hardware.RefreshAsync();
            AbandonIfRejected(Hardware.ErrorMessage);
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
                _ = Catalog.PrepareGenericRequestAsync();
                await Catalog.RunAsync(_client, SearchText);
                AbandonIfRejected(Catalog.ErrorMessage);
                break;
            case DeskSection.Notifications:
            case DeskSection.Leads:
                RefreshAlerts();
                break;
            case DeskSection.DailyWork:
                RefreshAlerts();
                RequestGroupQueuePoll();
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
        if (SelectedSection == DeskSection.Hardware)
        {
            await Hardware.SaveCommand.ExecuteAsync(null);
            return;
        }

        if (EditingWorkspace is not null)
            await EditingWorkspace.SaveCommand.ExecuteAsync(null);
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
        var workspace = EditingWorkspace;
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
        if (EditingWorkspace?.ShowResolvePanel == true)
            EditingWorkspace.CancelResolveCommand.Execute(null);
    }

    [RelayCommand]
    private void PostActive()
    {
        var workspace = EditingWorkspace;
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
        SyncNavigationSelection();
        if (!_preserveNavigation)
            _returnStack.Clear();

        SearchPlaceholder = value switch
        {
            DeskSection.Incidents => "Search incidents",
            DeskSection.Hardware => "Search hardware by serial, model, or assignee",
            DeskSection.Requests => "Search requests",
            DeskSection.RequestedItems => "Search request items",
            DeskSection.WalkUps => "Search walk-up interactions",
            DeskSection.InTheMix => "Search incidents, request items, and walk-ups",
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
            or DeskSection.InTheMix
            or DeskSection.Search
            or DeskSection.Notifications
            or DeskSection.Leads;
        if (value == DeskSection.Leads && Leads.Area == LeadArea.WorkEffort)
            ShowRowLegend = false;
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

    private RecordWorkspaceViewModel? EditingWorkspace =>
        SelectedSection == DeskSection.InTheMix ? Mix.Editor : ActiveRecord;

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
            case DeskSection.Hardware:
                Hardware.SearchText = SearchText;
                await Hardware.RefreshAsync();
                AbandonIfRejected(Hardware.ErrorMessage);
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
            case DeskSection.InTheMix:
                await LoadMixAsync();
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
                _ = Catalog.PrepareGenericRequestAsync();
                await Catalog.RunAsync(_client, SearchText);
                break;
            case DeskSection.Notifications:
                RefreshAlerts();
                break;
            case DeskSection.Leads:
                RefreshAlerts();
                _ = LoadLeadRosterAsync();
                if (Leads.Area == LeadArea.WorkEffort)
                    _ = LoadWorkEffortAsync(force: false);
                break;
            case DeskSection.DailyWork:
                RefreshAlerts();
                RequestGroupQueuePoll();
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

    private async Task ConvertNoteAsync(PersonalTaskRow row, bool incident)
    {
        if (Interlocked.Exchange(ref _convertingNote, 1) == 1)
            return;

        try
        {
            if (!IsConnected || _client is null)
            {
                ErrorMessage = PersonalTaskConversion.NotConnectedMessage;
                return;
            }

            ErrorMessage = "";
            var userId = string.IsNullOrWhiteSpace(_signedInUserId) ? null : _signedInUserId.Trim();
            if (incident)
            {
                var created = await _client.CreateIncidentAsync(
                    PersonalTaskConversion.ToIncident(row.Text, userId),
                    CancellationToken.None);
                await OpenHitAsync(new SearchHit
                {
                    Section = DeskSection.Incidents,
                    TableLabel = "Incident",
                    SysId = created.SysId,
                    Number = created.Number,
                    Title = created.ShortDescription,
                    StateLabel = created.StateLabel,
                    Tone = StateTone.ForIncident(created.State),
                    Meta = "",
                    When = "",
                    SortKey = created.Number
                }, fromSearch: false);
                DailyWork.RemovePersonalNote(row.Id);
                if (string.IsNullOrEmpty(ErrorMessage))
                    StatusMessage = "Created " + created.Number + " from a note.";
                return;
            }

            var item = await _client.CreateRequestedItemAsync(
                PersonalTaskConversion.ToRequestedItem(row.Text, userId),
                CancellationToken.None);
            await OpenHitAsync(new SearchHit
            {
                Section = DeskSection.RequestedItems,
                TableLabel = "Request item",
                SysId = item.SysId,
                Number = item.Number,
                Title = item.ShortDescription,
                StateLabel = item.StateLabel,
                Tone = StateTone.ForItem(item.State),
                Meta = "",
                When = "",
                SortKey = item.Number
            }, fromSearch: false);
            DailyWork.RemovePersonalNote(row.Id);
            if (string.IsNullOrEmpty(ErrorMessage))
                StatusMessage = "Created " + item.Number + " from a note.";
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _convertingNote, 0);
        }
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

    private async Task LoadMixAsync()
    {
        if (Mix.HasLoaded && _loadedFor.TryGetValue(DeskSection.InTheMix, out var loaded) && loaded == SearchText)
            return;

        Mix.SearchText = SearchText;
        await Mix.RefreshAsync();
        _loadedFor[DeskSection.InTheMix] = SearchText;
    }

    private void ApplyOfficeCities()
    {
        var cities = OfficeQueue.Cities(Connection.Notifications.OfficeLocations, _signedInUserLocation);
        Incidents.UseOfficeCities(cities);
        RequestedItems.UseOfficeCities(cities);
        WalkUps.UseOfficeCities(cities);
        Mix.UseOfficeCities(cities);
        _loadedFor.Remove(DeskSection.Incidents);
        _loadedFor.Remove(DeskSection.RequestedItems);
        _loadedFor.Remove(DeskSection.WalkUps);
        _loadedFor.Remove(DeskSection.InTheMix);
    }

    private async Task OpenMixRowAsync(TicketRow row)
    {
        var generation = ++_mixOpenGeneration;
        RecordWorkspaceViewModel workspace = row.Source switch
        {
            DeskSection.RequestedItems => RequestedItems,
            DeskSection.WalkUps => WalkUps,
            _ => Incidents
        };

        if (Mix.Editor is { IsDirty: true } || workspace.IsDirty)
        {
            if (Mix.Editor is { IsDirty: true } dirty)
            {
                dirty.ShowUnsavedBanner = true;
                dirty.EditorMessage = "Save or discard unsaved changes before opening another record.";
            }

            Mix.RevertSelection();
            return;
        }

        try
        {
            await workspace.EnsureChoicesAsync();
            if (generation != _mixOpenGeneration)
                return;
            await workspace.OpenFromSearchAsync(row.SysId);
            if (generation != _mixOpenGeneration)
                return;
            if (!string.Equals(workspace.Number, row.Number, StringComparison.OrdinalIgnoreCase))
            {
                Mix.RevertSelection();
                return;
            }

            Mix.RememberOpened(row);
            Mix.ShowEditor(workspace);
        }
        catch (Exception ex)
        {
            if (generation != _mixOpenGeneration)
                return;
            ErrorMessage = WorkspaceMessages.Describe(ex);
            Mix.RevertSelection();
        }
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
        var numbers = result.Numbers;
        if (!string.IsNullOrWhiteSpace(result.RequestedItemSysId))
        {
            await OpenHitAsync(new SearchHit
            {
                Section = DeskSection.RequestedItems,
                TableLabel = "Request item",
                SysId = result.RequestedItemSysId,
                Number = result.RequestedItemNumber,
                Title = "",
                StateLabel = "",
                Tone = "new",
                Meta = "",
                When = "",
                SortKey = ""
            }, fromSearch: false);
            StatusMessage = numbers.Length == 0 ? "Catalog item ordered." : "Opened " + numbers + ".";
            return;
        }

        if (string.IsNullOrWhiteSpace(result.RequestSysId))
        {
            StatusMessage = numbers.Length == 0 ? "Catalog item ordered." : numbers + ".";
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
        StatusMessage = numbers.Length == 0 ? "Catalog item ordered." : "Opened " + numbers + ".";
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
        _signedInUserLocation = "";
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
        CancelGroupQueueLoop();
        _watch.Reset();
        Notifications.Clear();
        Incidents.Detach();
        Hardware.Detach();
        Requests.Detach();
        RequestedItems.Detach();
        WalkUps.Detach();
        Mix.Detach();
        Catalog.Attach(null);
        Search.Reset();
        Search.Attach(null);
        Knowledge.Clear();
        _returnStack.Clear();
        UpdateBack();
        CancelWorkEffortLoad();
        _client?.Dispose();
        _client = client;
        if (client is null)
            return;

        Incidents.Attach(client);
        Hardware.Attach(client);
        Requests.Attach(client);
        RequestedItems.Attach(client);
        WalkUps.Attach(client);
        Mix.Attach(client);
        Catalog.Attach(client);
        Search.Attach(client);
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

    private void StartGroupQueueLoop()
    {
        CancelGroupQueueLoop();
        if (_client is null || string.IsNullOrWhiteSpace(_signedInUserId))
            return;

        var cts = new CancellationTokenSource();
        _queueCts = cts;
        var generation = _queueGeneration;
        var client = _client;
        var token = cts.Token;
        _ = Task.Run(() => GroupQueueLoopAsync(client, generation, token));
    }

    private void CancelGroupQueueLoop()
    {
        _queueGeneration++;
        var cts = _queueCts;
        _queueCts = null;
        if (cts is null)
            return;
        cts.Cancel();
        cts.Dispose();
    }

    private void RequestGroupQueuePoll()
    {
        if (_client is null || _queueCts is not { IsCancellationRequested: false } cts)
            return;

        var client = _client;
        var generation = _queueGeneration;
        CancellationToken token;
        try
        {
            token = cts.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ = Task.Run(() => PollGroupQueueOnceAsync(client, generation, token));
    }

    private async Task GroupQueueLoopAsync(IServiceNowClient client, int generation, CancellationToken token)
    {
        while (!token.IsCancellationRequested && generation == _queueGeneration)
        {
            await PollGroupQueueOnceAsync(client, generation, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || generation != _queueGeneration)
                return;

            try
            {
                await Task.Delay(GroupQueueTracker.Interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollGroupQueueOnceAsync(IServiceNowClient client, int generation, CancellationToken token)
    {
        try
        {
            await _queueGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (generation != _queueGeneration || !ReferenceEquals(client, _client))
                return;

            var localNow = DateTime.Now;
            var search = NotificationSettings.Committed.ToSearch(_signedInUserId) with
            {
                TeamMemberIds = Connection.LeadTeamMemberIds
            };

            AlertReport? report = null;
            Exception? reportError = null;
            try
            {
                report = await client.GetAlertReportAsync(search, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                reportError = ex;
            }

            IReadOnlyList<WatchedRecord> queue = [];
            Exception? queueError = null;
            try
            {
                queue = await client.ListUnassignedGroupQueueAsync(search.GroupName, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                queueError = ex;
            }

            var scope = DailyWorkKeys.User(_signedInUserId);
            GroupQueueStep? step = null;
            if (scope is not null && queueError is null)
            {
                var day = DateOnly.FromDateTime(localNow);
                var previous = _dailyWork.FindUnassigned(scope, day);
                step = GroupQueueTracker.Compare(previous, queue.Select(record => record.SysId));
                _dailyWork.SaveUnassigned(scope, day, step.State);
            }

            var status = GroupQueueTracker.StatusText(step?.NewIds.Count ?? 0);
            PostToUi(() =>
            {
                if (generation != _queueGeneration || !ReferenceEquals(client, _client))
                    return;

                if (report is not null)
                {
                    Notifications.Apply(report.Personal, _watch);
                    Leads.Show(report.Leads);
                    DailyWork.Show(report.Daily, _signedInUserId, Connection.LeadTeamMemberIds, localNow);
                    _rows.Use(report.Personal);
                    RepaintRows();
                }
                else if (reportError is not null)
                {
                    Notifications.NotePollError(WorkspaceMessages.Describe(reportError));
                }

                if (step is not null)
                    DailyWork.ShowGroupQueue(queue, step.State, localNow);

                if (queueError is not null)
                    Notifications.NotePollError(WorkspaceMessages.Describe(queueError));

                if (!string.IsNullOrEmpty(status))
                    StatusMessage = status;
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _queueGate.Release();
        }
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
        Paint(Mix.Items);
        foreach (var hit in Search.Results)
            _rows.Paint(hit);
    }

    private void Paint(IEnumerable<TicketRow> rows)
    {
        foreach (var row in rows)
            _rows.Paint(row);
    }

    private CancellationTokenSource? _workEffortCts;
    private int _workEffortGeneration;

    /// <summary>
    /// Loads the selected scale for the people ticked on My team. Leaving Leads or switching to
    /// My team or Regional does not cancel it. Refresh, or choosing another time scale, cancels
    /// only the previous Work Effort query. Coming back while it is running does not start a second one.
    /// A finished background load is cached for the local day and that team.
    /// </summary>
    private async Task LoadWorkEffortAsync(bool force)
    {
        if (Leads.Area != LeadArea.WorkEffort)
            return;

        var team = Leads.DefinedTeam(Connection.LeadTeamMemberIds);
        if (team.Count == 0)
        {
            if (Leads.WorkEffort.IsLoading)
                return;
            Leads.WorkEffort.Show(WorkEffortReport.NoTeam());
            return;
        }

        var client = _client;
        if (client is null || !IsConnected)
        {
            Leads.WorkEffort.ShowError("Connect to load work effort.");
            return;
        }

        var scale = Leads.WorkEffort.Scale;
        var localNow = DateTime.Now;
        var teamKey = WorkEffortTeam.Key(team);
        if (!Leads.WorkEffort.BeginLoad(localNow, force, teamKey))
            return;

        var generation = BeginWorkEffortLoad();
        var cts = new CancellationTokenSource();
        _workEffortCts = cts;
        var sink = new WorkEffortProgressSink(this, generation, client, scale);
        try
        {
            var report = await Task.Run(
                () => client.GetWorkEffortAsync(scale, localNow, team, sink, cts.Token),
                cts.Token).ConfigureAwait(false);
            PostToUi(() =>
            {
                if (generation != _workEffortGeneration || !ReferenceEquals(client, _client))
                    return;
                if (!string.Equals(WorkEffortTeam.Key(Leads.DefinedTeam(Connection.LeadTeamMemberIds)), teamKey, StringComparison.Ordinal))
                {
                    Leads.WorkEffort.AbandonLoad();
                    if (Leads.Area == LeadArea.WorkEffort)
                        _ = LoadWorkEffortAsync(force: false);
                    return;
                }

                Leads.WorkEffort.Remember(scale, localNow, report, teamKey);
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            var message = WorkspaceMessages.Describe(ex);
            PostToUi(() =>
            {
                if (generation != _workEffortGeneration || !ReferenceEquals(client, _client))
                    return;
                if (Leads.Area != LeadArea.WorkEffort || Leads.WorkEffort.Scale != scale)
                    return;
                Leads.WorkEffort.ShowError(message);
            });
        }
    }

    /// <summary>
    /// Cancels the in-flight Work Effort query so a refresh or a different scale can replace it.
    /// </summary>
    private int BeginWorkEffortLoad()
    {
        var generation = ++_workEffortGeneration;
        CancelWorkEffortSource();
        return generation;
    }

    /// <summary>
    /// Stops an in-flight query because the client is going away. Navigation does not call this.
    /// </summary>
    private void CancelWorkEffortLoad()
    {
        _workEffortGeneration++;
        CancelWorkEffortSource();
        Leads.WorkEffort.AbandonLoad();
    }

    private void CancelWorkEffortSource()
    {
        var cts = _workEffortCts;
        _workEffortCts = null;
        if (cts is null)
            return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cts.Dispose();
    }

    private sealed class WorkEffortProgressSink(
        MainViewModel owner,
        int generation,
        IServiceNowClient client,
        WorkEffortScale scale) : IProgress<WorkEffortProgress>
    {
        public void Report(WorkEffortProgress value) => owner.PostToUi(() =>
        {
            if (generation != owner._workEffortGeneration || !ReferenceEquals(client, owner._client))
                return;
            owner.Leads.WorkEffort.Apply(scale, value);
        });
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
