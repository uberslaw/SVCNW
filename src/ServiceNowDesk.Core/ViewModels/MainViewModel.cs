using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IBrowserSignIn? _browserSignIn;
    private readonly IFormCatalogStore? _formCatalog;
    private IServiceNowClient? _client;
    private CancellationTokenSource? _searchCts;
    private readonly Dictionary<DeskSection, string> _loadedFor = [];
    private bool _openingRecord;

    public MainViewModel(ISettingsStore store, IDesktopServices desktop, IBrowserSignIn? browserSignIn = null, IFormCatalogStore? formCatalog = null)
    {
        _store = store;
        _browserSignIn = browserSignIn;
        _formCatalog = formCatalog;
        Connection = new ConnectionViewModel();
        Incidents = new IncidentWorkspaceViewModel(desktop);
        Requests = new RequestWorkspaceViewModel(desktop);
        RequestedItems = new RequestedItemWorkspaceViewModel(desktop);
        Search = new SearchWorkspaceViewModel();
        Catalog = new CatalogWorkspaceViewModel();
        Requests.RelatedItemRequested += (_, sysId) => _ = OpenRequestedItemAsync(sysId);
        Search.OpenRequested += (_, hit) => _ = OpenSearchResultAsync(hit);
        Catalog.RequestOrdered += (_, result) => _ = OpenOrderedRequestAsync(result);
    }

    public ConnectionViewModel Connection { get; }
    public IncidentWorkspaceViewModel Incidents { get; }
    public RequestWorkspaceViewModel Requests { get; }
    public RequestedItemWorkspaceViewModel RequestedItems { get; }
    public SearchWorkspaceViewModel Search { get; }
    public CatalogWorkspaceViewModel Catalog { get; }
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

    public bool ResolvePanelOpen =>
        (SelectedSection == DeskSection.Incidents && Incidents.ShowResolvePanel)
        || (SelectedSection == DeskSection.Requests && Requests.ShowResolvePanel)
        || (SelectedSection == DeskSection.RequestedItems && RequestedItems.ShowResolvePanel);

    public async Task InitializeAsync()
    {
        var settings = _store.Load();
        Connection.Load(settings);
        if (settings.UseSampleData || !string.IsNullOrWhiteSpace(settings.InstanceUrl))
            await ConnectAsync();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        IServiceNowClient? created = null;
        try
        {
            IsBusy = true;
            ErrorMessage = "";
            var settings = Connection.BuildSettings();
            ServiceNowClient? live = null;
            if (settings.UseSampleData)
            {
                created = new SampleServiceNowClient();
            }
            else
            {
                var session = ServiceNowSession.FromSettings(settings);
                live = ServiceNowClient.Create(session, formCatalog: _formCatalog);
                created = live;
            }

            var user = await created.GetCurrentUserAsync(CancellationToken.None);
            ReplaceClient(created);
            created = null;
            _store.Save(settings);
            ConnectedUser = user.Name;
            IsSample = settings.UseSampleData;
            InstanceLabel = settings.UseSampleData ? "Practice data" : ServiceNowSession.NormalizeInstance(settings.InstanceUrl).GetLeftPart(UriPartial.Authority);
            WindowTitle = "ServiceNow Desk — " + InstanceLabel;
            IsConnected = true;
            StatusMessage = settings.UseSampleData
                ? "Practice data loaded. Nothing is sent to ServiceNow."
                : "Connected as " + user.Name + ".";
            if (live is not null)
                await RefreshFormsIfNeededAsync(live, user.Name);
            _loadedFor.Clear();
            if (SelectedSection == DeskSection.Connection)
                SelectedSection = DeskSection.Incidents;
            await EnsureSectionAsync();
            RefreshActivity();
        }
        catch (Exception ex)
        {
            created?.Dispose();
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
            Connection.SessionCapturedAt = DateTimeOffset.Now;
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
        ReplaceClient(null);
        IsConnected = false;
        IsSample = false;
        ConnectedUser = "";
        InstanceLabel = "";
        WindowTitle = "ServiceNow Desk";
        StatusMessage = "Disconnected.";
        SelectedSection = DeskSection.Connection;
        Activity.Clear();
        _loadedFor.Clear();
    }

    [RelayCommand]
    private async Task RefreshActiveAsync()
    {
        if (!IsConnected)
            return;
        _loadedFor.Remove(SelectedSection);
        await EnsureSectionAsync();
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
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        _ = DebouncedSearchAsync(token);
    }

    partial void OnSelectedSectionChanged(DeskSection value)
    {
        SearchPlaceholder = value switch
        {
            DeskSection.Incidents => "Search incidents",
            DeskSection.Requests => "Search requests",
            DeskSection.RequestedItems => "Search request items",
            DeskSection.Search => "Search incidents, requests, and items",
            DeskSection.Catalog => "Search the catalog",
            _ => "Search"
        };
        if (IsConnected && !_openingRecord)
            _ = EnsureSectionAsync();
    }

    private RecordWorkspaceViewModel? ActiveRecord => SelectedSection switch
    {
        DeskSection.Incidents => Incidents,
        DeskSection.Requests => Requests,
        DeskSection.RequestedItems => RequestedItems,
        _ => null
    };

    private async Task DebouncedSearchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
            if (!IsConnected)
                return;
            _loadedFor.Remove(SelectedSection);
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
            case DeskSection.Search:
                await Search.RunAsync(_client, SearchText);
                break;
            case DeskSection.Catalog:
                await Catalog.RunAsync(_client, SearchText);
                break;
            case DeskSection.Connection:
                RefreshActivity();
                break;
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
    }

    public Task OpenSearchResultAsync(SearchHit hit) => OpenHitAsync(hit);

    private async Task OpenHitAsync(SearchHit hit)
    {
        try
        {
            _openingRecord = true;
            SelectedSection = hit.Section;
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
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            _openingRecord = false;
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
        });

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
        });
        StatusMessage = "Opened " + result.RequestNumber + ".";
    }

    private async Task RefreshFormsIfNeededAsync(ServiceNowClient client, string userName)
    {
        if (_formCatalog is null || !client.FormCatalogIsStale)
            return;

        if (!client.HasCachedChoices)
        {
            StatusMessage = "Connected as " + userName + ". Downloading form lists.";
            try
            {
                await client.RefreshFormCatalogAsync(CancellationToken.None);
                StatusMessage = "Connected as " + userName + ".";
            }
            catch (Exception ex)
            {
                StatusMessage = "Connected as " + userName + ".";
                ErrorMessage = "Form lists could not be downloaded. " + WorkspaceMessages.Describe(ex);
            }

            return;
        }

        _ = RefreshFormCatalogQuietlyAsync(client);
    }

    private async Task RefreshFormCatalogQuietlyAsync(ServiceNowClient client)
    {
        try
        {
            await client.RefreshFormCatalogAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            if (!ReferenceEquals(_client, client))
                return;
            StatusMessage = "Connected as " + ConnectedUser + ". Saved form lists are still in use.";
        }
    }

    private void ReplaceClient(IServiceNowClient? client)
    {
        Incidents.Detach();
        Requests.Detach();
        RequestedItems.Detach();
        Catalog.Attach(null);
        _client?.Dispose();
        _client = client;
        if (client is null)
            return;

        Incidents.Attach(client);
        Requests.Attach(client);
        RequestedItems.Attach(client);
        Catalog.Attach(client);
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
