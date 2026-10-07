using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public partial class HardwareWorkspaceViewModel : ObservableObject
{
    private readonly ISettingsStore? _settings;
    private readonly List<HardwareAsset> _loadedRows = [];
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private IServiceNowClient? _client;
    private HardwareAsset? _loaded;
    private bool _choicesReady;
    private bool _suppressSelection;
    private bool _officesReady;
    private bool _suppressOffice;
    private bool _missingLocationNotice;
    private bool _locationFilterIgnored;
    private string _signedInLocation = "";
    private int _substateGeneration;

    public HardwareWorkspaceViewModel(ISettingsStore? settings = null)
    {
        _settings = settings;
        AssignedTo = new ReferenceFieldModel(SearchUsersAsync, match: MatchUsersAsync);
        Location = new ReferenceFieldModel(SearchLocationsAsync);
        Stockroom = new ReferenceFieldModel(SearchStockroomsAsync);
        ReceiveStockroom = new ReferenceFieldModel(SearchStockroomsAsync);
        AssignedTo.Changed += (_, _) => Touch();
        Location.Changed += (_, _) => Touch();
        Stockroom.Changed += (_, _) => Touch();
        ReceiveStockroom.Changed += (_, _) =>
        {
            if (!string.IsNullOrEmpty(ReceiveStockroom.SysId))
                StockroomApply = ApplyWaitingAsync();
        };
        Batch.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasBatch));
        Offices.CollectionChanged += (_, _) => RefreshVisibleOffices();
    }

    public ReferenceFieldModel AssignedTo { get; }
    public ReferenceFieldModel Location { get; }
    public ReferenceFieldModel Stockroom { get; }
    public ReferenceFieldModel ReceiveStockroom { get; }
    public ObservableCollection<HardwareAsset> Items { get; } = [];
    public ObservableCollection<HardwareOfficeOption> Offices { get; } = [];
    public ObservableCollection<HardwareOfficeOption> VisibleOffices { get; } = [];
    public ObservableCollection<Choice> InstallStatuses { get; } = [];
    public ObservableCollection<Choice> Substatuses { get; } = [];
    public ObservableCollection<HardwareScanRow> Batch { get; } = [];

    public bool HasBatch => Batch.Count > 0;
    public bool ShowSubstate => Substatuses.Any(choice => !string.IsNullOrEmpty(choice.Value));
    public string StateLabelText => HardwareCatalog.LabelOf(InstallStatuses, InstallStatus);
    public bool StockroomRequired => HardwareCatalog.RequiresStockroom(StateLabelText);
    public bool LocationRequired => HardwareCatalog.RequiresLocation(StateLabelText);

    public Task SubstateLoad { get; private set; } = Task.CompletedTask;
    public Task OpenTask { get; private set; } = Task.CompletedTask;
    public Task StockroomApply { get; private set; } = Task.CompletedTask;
    public Task OfficeLoad { get; private set; } = Task.CompletedTask;

    public event EventHandler? DefaultSaved;

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string officeSearchText = "";
    [ObservableProperty] private string officeStatus = "";
    [ObservableProperty] private string serialFilter = "";
    [ObservableProperty] private string modelFilter = "";
    [ObservableProperty] private string assignedFilter = "";
    [ObservableProperty] private string locationFilter = "";
    [ObservableProperty] private string stateFilter = "";
    [ObservableProperty] private string substatusFilter = "";
    [ObservableProperty] private string commentsFilter = "";
    [ObservableProperty] private HardwareAsset? selected;
    [ObservableProperty] private bool hasEditor;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string errorMessage = "";
    [ObservableProperty] private string editorMessage = "";
    [ObservableProperty] private string receiveMessage = "";
    [ObservableProperty] private string scanText = "";
    [ObservableProperty] private string serialNumber = "";
    [ObservableProperty] private string displayName = "";
    [ObservableProperty] private string modelName = "";
    [ObservableProperty] private string installStatus = "";
    [ObservableProperty] private string substatus = "";
    [ObservableProperty] private string comments = "";

    private bool Applying { get; set; }

    public void Attach(IServiceNowClient? client)
    {
        _client = client;
        _choicesReady = false;
        _officesReady = false;
    }

    public void RememberViewer(CurrentUser? user) =>
        _signedInLocation = user?.Location?.Trim() ?? "";

    public void Detach()
    {
        _client = null;
        _choicesReady = false;
        _officesReady = false;
        _missingLocationNotice = false;
        _signedInLocation = "";
        _loaded = null;
        HasEditor = false;
        IsDirty = false;
        _loadedRows.Clear();
        Items.Clear();
        _suppressOffice = true;
        Offices.Clear();
        _suppressOffice = false;
        InstallStatuses.Clear();
        Substatuses.Clear();
        AssignedTo.Clear();
        Location.Clear();
        Stockroom.Clear();
        ReceiveStockroom.Clear();
        Batch.Clear();
        SerialFilter = "";
        ModelFilter = "";
        AssignedFilter = "";
        LocationFilter = "";
        StateFilter = "";
        SubstatusFilter = "";
        CommentsFilter = "";
        OfficeSearchText = "";
        OfficeStatus = "";
        OnPropertyChanged(nameof(OfficeSelectionSummary));
    }

    public string OfficeSelectionSummary
    {
        get
        {
            var names = SelectedOfficeNames();
            return names.Count == 0 ? "All locations" : string.Join(", ", names);
        }
    }

    public Task RefreshAsync() => RefreshCoreAsync();

    private async Task RefreshCoreAsync()
    {
        if (_client is null)
            return;

        await _refreshGate.WaitAsync();
        try
        {
            IsLoading = true;
            ErrorMessage = "";
            _locationFilterIgnored = false;
            if (!_officesReady)
                await PrepareOfficesAsync();
            await EnsureChoicesAsync();
            var offices = SelectedOfficeNames();
            var page = await LoadHardwarePageAsync(offices);
            var keep = Selected?.SysId ?? _loaded?.SysId;
            _loadedRows.Clear();
            var outsideSelection = 0;
            foreach (var asset in page.Items)
            {
                var inOffice = HardwareCatalog.MatchesLocation(asset, offices);
                if (inOffice && HardwareCatalog.MatchesSearch(asset, SearchText))
                    _loadedRows.Add(asset);
                else if (offices.Count > 0 && !inOffice)
                    outsideSelection++;
            }

            if (offices.Count > 0 && _loadedRows.Count == 0 && outsideSelection > 0)
                _locationFilterIgnored = true;

            ApplyColumnFilters(keep);
            PublishScope(offices);
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            IsLoading = false;
            _refreshGate.Release();
        }
    }

    public async Task EnsureChoicesAsync()
    {
        if (_choicesReady || _client is null)
            return;

        _choicesReady = true;
        var choices = await ReadChoicesAsync("install_status", null, HardwareCatalog.InstallStatuses);
        InstallStatuses.Clear();
        foreach (var choice in choices)
            InstallStatuses.Add(choice);
        OnPropertyChanged(nameof(StateLabelText));
        OnPropertyChanged(nameof(StockroomRequired));
        OnPropertyChanged(nameof(LocationRequired));
    }

    [RelayCommand]
    private Task ReceiveScanAsync()
    {
        var scan = ScanText ?? "";
        if (string.IsNullOrWhiteSpace(scan) || _client is null)
            return Task.CompletedTask;

        // The scanner's Enter only queues the exact text. Lookup waits until
        // Look up, Enter on the row, or leaving the field.
        ScanText = "";
        Batch.Insert(0, new HardwareScanRow { Text = scan });
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task LookupRowAsync(HardwareScanRow? row) =>
        row is null ? Task.CompletedTask : LookupCoreAsync(row);

    private async Task ApplyWaitingAsync()
    {
        foreach (var row in Batch.ToArray())
        {
            if (row.Status == "Needs stockroom")
                await LookupCoreAsync(row);
        }
    }

    private async Task LookupCoreAsync(HardwareScanRow row)
    {
        var text = row.Text ?? "";
        row.Hint = HardwareSerial.HintFor(text);
        if (!string.Equals(row.LastText, text, StringComparison.Ordinal))
        {
            row.OldState = "";
            row.NewState = "";
            row.LastText = text;
        }

        if (_client is null)
            return;

        if (string.IsNullOrWhiteSpace(text))
        {
            row.Status = "Unmatched";
            row.MatchedSerial = "";
            row.Detail = "No hardware asset matches this serial.";
            return;
        }

        try
        {
            ErrorMessage = "";
            await EnsureChoicesAsync();
            var asset = await _client.FindHardwareBySerialAsync(text, true, CancellationToken.None);
            if (asset is null)
            {
                row.MatchedSerial = "";
                row.Model = "";
                row.AssignedToName = "";
                row.Status = "Unmatched";
                row.Detail = "No hardware asset matches this serial.";
                ReceiveMessage = "No hardware asset matches " + text + ".";
                return;
            }

            row.MatchedSerial = asset.SerialNumber;
            row.Model = asset.Model;
            row.AssignedToName = asset.AssignedTo.Display;
            if (HardwareCatalog.IsInStock(asset.InstallStatusLabel, asset.InstallStatus, InstallStatuses))
            {
                if (string.IsNullOrEmpty(row.OldState))
                    row.OldState = asset.InstallStatusLabel;
                row.NewState = asset.InstallStatusLabel;
                row.Status = "Already in stock";
                row.Detail = asset.SerialNumber + " is already in stock.";
                ReceiveMessage = row.Detail;
                return;
            }

            if (string.IsNullOrEmpty(ReceiveStockroom.SysId))
            {
                row.Status = "Needs stockroom";
                row.Detail = "Choose the stockroom first.";
                ReceiveMessage = "Choose the stockroom first.";
                return;
            }

            var previous = asset.InstallStatusLabel;
            var statusValue = HardwareCatalog.ValueForLabel(InstallStatuses, HardwareCatalog.InStock, HardwareCatalog.InStock);
            var substates = await ReadChoicesAsync("substatus", statusValue, HardwareCatalog.RealSubstates(HardwareCatalog.LabelOf(InstallStatuses, statusValue)));
            var available = HardwareCatalog.ValueForLabel(substates, HardwareCatalog.Available, HardwareCatalog.Available);
            var updated = await _client.UpdateHardwareAsync(asset.SysId, new HardwareChanges
            {
                InstallStatus = statusValue,
                Substatus = available,
                StockroomId = ReceiveStockroom.SysId
            }, CancellationToken.None);

            row.OldState = previous;
            row.NewState = updated.InstallStatusLabel;
            row.Status = "Received";
            row.Detail = updated.SerialNumber + "  " + previous + " → " + updated.InstallStatusLabel;
            ReceiveMessage = "Received " + updated.SerialNumber + ".";
            ReplaceItem(updated);
            if (_loaded?.SysId == updated.SysId && !IsDirty)
                await ShowAsync(updated);
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
            ReceiveMessage = ErrorMessage;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_client is null || _loaded is null)
            return;

        if (!TryValidate(out var message))
        {
            ErrorMessage = message;
            return;
        }

        var changes = BuildChanges();
        if (!changes.HasChanges)
        {
            IsDirty = false;
            EditorMessage = "No changes to save.";
            return;
        }

        try
        {
            ErrorMessage = "";
            var updated = await _client.UpdateHardwareAsync(_loaded.SysId, changes, CancellationToken.None);
            await ShowAsync(updated);
            ReplaceItem(updated);
            EditorMessage = "Saved " + updated.SerialNumber + ".";
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
    }

    [RelayCommand]
    private async Task DiscardAsync()
    {
        if (_loaded is null)
            return;
        await ShowAsync(_loaded);
        EditorMessage = "Changes discarded.";
        ErrorMessage = "";
    }

    public Task OpenAsync(string sysId)
    {
        OpenTask = OpenCoreAsync(sysId);
        return OpenTask;
    }

    partial void OnSelectedChanged(HardwareAsset? value)
    {
        if (_suppressSelection || value is null)
            return;
        if (IsDirty && _loaded is not null && value.SysId != _loaded.SysId)
        {
            _suppressSelection = true;
            Selected = Items.FirstOrDefault(asset => asset.SysId == _loaded.SysId);
            _suppressSelection = false;
            EditorMessage = "Save or discard unsaved changes first.";
            return;
        }

        if (_loaded?.SysId == value.SysId && HasEditor)
            return;
        OpenTask = OpenCoreAsync(value.SysId);
    }

    partial void OnInstallStatusChanged(string value)
    {
        OnPropertyChanged(nameof(StateLabelText));
        OnPropertyChanged(nameof(StockroomRequired));
        OnPropertyChanged(nameof(LocationRequired));
        if (Applying)
            return;
        Touch();
        SubstateLoad = LoadSubstatesAsync(value, Substatus, preserveUnknown: false);
    }

    partial void OnSubstatusChanged(string value)
    {
        if (!Applying)
            Touch();
    }

    partial void OnCommentsChanged(string value)
    {
        if (!Applying)
            Touch();
    }

    partial void OnSerialFilterChanged(string value) => ApplyColumnFilters(Selected?.SysId);

    partial void OnModelFilterChanged(string value) => ApplyColumnFilters(Selected?.SysId);

    partial void OnAssignedFilterChanged(string value) => ApplyColumnFilters(Selected?.SysId);

    partial void OnLocationFilterChanged(string value) => ApplyColumnFilters(Selected?.SysId);

    partial void OnStateFilterChanged(string value) => ApplyColumnFilters(Selected?.SysId);

    partial void OnSubstatusFilterChanged(string value) => ApplyColumnFilters(Selected?.SysId);

    partial void OnCommentsFilterChanged(string value) => ApplyColumnFilters(Selected?.SysId);

    [RelayCommand]
    private async Task SearchAllLocationsAsync()
    {
        _missingLocationNotice = false;
        _suppressOffice = true;
        foreach (var office in Offices)
            office.IsSelected = false;
        _suppressOffice = false;
        OnPropertyChanged(nameof(OfficeSelectionSummary));
        await RefreshCoreAsync();
    }

    [RelayCommand]
    private void SetDefaultOffices()
    {
        var names = SelectedOfficeNames();
        if (_settings is not null)
        {
            var settings = _settings.Load();
            settings.HardwareOfficeLocations = names;
            _settings.Save(settings);
        }

        OfficeStatus = names.Count == 0
            ? "Default saved: all locations."
            : "Default saved: " + string.Join(", ", names) + ".";
        DefaultSaved?.Invoke(this, EventArgs.Empty);
    }

    private async Task OpenCoreAsync(string sysId)
    {
        if (_client is null || string.IsNullOrWhiteSpace(sysId))
            return;

        try
        {
            ErrorMessage = "";
            await EnsureChoicesAsync();
            var asset = await _client.GetHardwareAsync(sysId, CancellationToken.None);
            await ShowAsync(asset);
            EditorMessage = "";
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
    }

    private async Task ShowAsync(HardwareAsset asset)
    {
        Applying = true;
        try
        {
            _loaded = asset;
            HasEditor = true;
            SerialNumber = asset.SerialNumber;
            DisplayName = asset.DisplayName;
            ModelName = asset.Model;
            Comments = asset.Comments;
            AssignedTo.Set(asset.AssignedTo.SysId, asset.AssignedTo.Display);
            Location.Set(asset.Location.SysId, asset.Location.Display);
            Stockroom.Set(asset.Stockroom.SysId, asset.Stockroom.Display);
            InstallStatus = string.IsNullOrEmpty(asset.InstallStatus) ? asset.InstallStatusLabel : asset.InstallStatus;
            if (!string.IsNullOrEmpty(InstallStatus) && InstallStatuses.All(choice => choice.Value != InstallStatus))
                InstallStatuses.Add(new Choice(InstallStatus, string.IsNullOrWhiteSpace(asset.InstallStatusLabel) ? InstallStatus : asset.InstallStatusLabel));
        }
        finally
        {
            Applying = false;
        }

        await LoadSubstatesAsync(InstallStatus, asset.Substatus, preserveUnknown: true);
        IsDirty = false;
    }

    private async Task LoadSubstatesAsync(string installStatus, string selected, bool preserveUnknown)
    {
        var generation = ++_substateGeneration;
        var label = HardwareCatalog.LabelOf(InstallStatuses, installStatus);
        var fallback = HardwareCatalog.RealSubstates(label);
        var choices = await ReadChoicesAsync("substatus", installStatus, fallback);
        if (generation != _substateGeneration)
            return;

        Substatuses.Clear();
        if (choices.Count > 0)
            Substatuses.Add(new Choice("", "None"));
        foreach (var choice in choices)
        {
            if (string.IsNullOrEmpty(choice.Value))
                continue;
            if (Substatuses.All(existing => existing.Value != choice.Value))
                Substatuses.Add(choice);
        }

        if (preserveUnknown && !string.IsNullOrEmpty(selected) && Substatuses.All(choice => choice.Value != selected))
            Substatuses.Add(new Choice(selected, selected));

        var keep = Substatuses.Any(choice => choice.Value == selected) ? selected : "";
        Applying = true;
        Substatus = keep;
        Applying = false;
        OnPropertyChanged(nameof(ShowSubstate));
        OnPropertyChanged(nameof(StockroomRequired));
        OnPropertyChanged(nameof(LocationRequired));
    }

    private HardwareChanges BuildChanges()
    {
        var record = _loaded ?? throw new InvalidOperationException("Open a hardware asset before saving.");
        var substatus = FieldDiff.Changed(Substatus, record.Substatus);
        var clearSubstatus = substatus is not null && string.IsNullOrEmpty(Substatus);
        return new HardwareChanges
        {
            InstallStatus = FieldDiff.Changed(InstallStatus, record.InstallStatus),
            Substatus = clearSubstatus ? null : substatus,
            ClearSubstatus = clearSubstatus,
            Comments = FieldDiff.Changed(Comments, record.Comments),
            AssignedToId = !string.IsNullOrEmpty(AssignedTo.SysId) && AssignedTo.SysId != record.AssignedTo.SysId ? AssignedTo.SysId : null,
            ClearAssignedTo = string.IsNullOrEmpty(AssignedTo.SysId) && !record.AssignedTo.IsEmpty,
            LocationId = !string.IsNullOrEmpty(Location.SysId) && Location.SysId != record.Location.SysId ? Location.SysId : null,
            ClearLocation = string.IsNullOrEmpty(Location.SysId) && !record.Location.IsEmpty,
            StockroomId = !string.IsNullOrEmpty(Stockroom.SysId) && Stockroom.SysId != record.Stockroom.SysId ? Stockroom.SysId : null,
            ClearStockroom = string.IsNullOrEmpty(Stockroom.SysId) && !record.Stockroom.IsEmpty
        };
    }

    private bool TryValidate(out string message)
    {
        if (!ReferenceIsChosen(AssignedTo))
        {
            message = "Choose the assigned person from the list, or clear the field.";
            return false;
        }

        if (StockroomRequired && string.IsNullOrEmpty(Stockroom.SysId))
        {
            message = "Choose a stockroom.";
            return false;
        }

        if (!ReferenceIsChosen(Stockroom))
        {
            message = "Choose the stockroom from the list, or clear the field.";
            return false;
        }

        if (LocationRequired && string.IsNullOrEmpty(Location.SysId))
        {
            message = "Choose a location.";
            return false;
        }

        if (!ReferenceIsChosen(Location))
        {
            message = "Choose the location from the list, or clear the field.";
            return false;
        }

        message = "";
        return true;
    }

    private bool ComputeDirty()
    {
        if (_loaded is null)
            return false;
        var record = _loaded;
        return SerialNumber != record.SerialNumber
            || Comments != record.Comments
            || InstallStatus != record.InstallStatus
            || Substatus != record.Substatus
            || AssignedTo.SysId != record.AssignedTo.SysId
            || Location.SysId != record.Location.SysId
            || Stockroom.SysId != record.Stockroom.SysId;
    }

    private void Touch()
    {
        if (Applying)
            return;
        IsDirty = ComputeDirty();
    }

    private void ReplaceItem(HardwareAsset updated)
    {
        var index = _loadedRows.FindIndex(asset => asset.SysId == updated.SysId);
        if (index < 0)
            return;

        _loadedRows[index] = updated;
        ApplyColumnFilters(updated.SysId);
    }

    private async Task PrepareOfficesAsync()
    {
        _suppressOffice = true;
        try
        {
            Offices.Clear();
            foreach (var name in HardwareCatalog.KnownOfficeNames)
                EnsureOffice(name, false, HardwareOfficeLabelKind.Seed);
            EnsureOffice(_signedInLocation, false, HardwareOfficeLabelKind.Account);
            await AddReferenceLocationsAsync();

            var saved = ReadSavedOffices();
            if (saved is not null)
            {
                _missingLocationNotice = false;
                foreach (var name in saved)
                    EnsureOffice(name, true, HardwareOfficeLabelKind.Seed);
            }
            else if (!string.IsNullOrWhiteSpace(_signedInLocation))
            {
                _missingLocationNotice = false;
                EnsureOffice(_signedInLocation, true, HardwareOfficeLabelKind.Account);
            }
            else
            {
                _missingLocationNotice = true;
            }
        }
        finally
        {
            _suppressOffice = false;
            _officesReady = true;
        }
    }

    private async Task AddReferenceLocationsAsync()
    {
        if (_client is null)
            return;

        var seeds = HardwareCatalog.KnownOfficeNames
            .Append(_signedInLocation)
            .Select(name => (name ?? "").Trim())
            .Where(name => name.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in seeds)
        {
            IReadOnlyList<ReferenceSuggestion> found;
            try
            {
                found = await _client.SearchLocationsAsync(seed, CancellationToken.None);
            }
            catch
            {
                continue;
            }

            foreach (var place in found)
                EnsureOffice(place.Display, false, HardwareOfficeLabelKind.Reference);
        }
    }

    private IReadOnlyList<string>? ReadSavedOffices()
    {
        if (_settings is null)
            return null;

        var saved = _settings.Load().HardwareOfficeLocations;
        if (saved is null)
            return null;

        return saved
            .Select(name => name?.Trim() ?? "")
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private Task<PagedResult<HardwareAsset>> LoadHardwarePageAsync(IReadOnlyList<string> offices) =>
        _client!.SearchHardwareAsync(new TicketQuery
        {
            Text = SearchText,
            Limit = 100,
            Activity = ActivityFilter.Any,
            Locations = offices.ToList()
        }, CancellationToken.None);

    partial void OnOfficeSearchTextChanged(string value) => RefreshVisibleOffices(value);

    private void RefreshVisibleOffices() => RefreshVisibleOffices(OfficeSearchText);

    private void RefreshVisibleOffices(string? term)
    {
        var filter = (term ?? "").Trim();
        var desired = Offices
            .Where(office => filter.Length == 0 || office.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (desired.Length == VisibleOffices.Count && desired.SequenceEqual(VisibleOffices))
            return;

        VisibleOffices.Clear();
        foreach (var office in desired)
            VisibleOffices.Add(office);
    }

    /// <summary>
    /// Adds or selects an office. A bare city and the same city plus " Office" share one checkbox.
    /// </summary>
    private void EnsureOffice(string? name, bool selected, HardwareOfficeLabelKind kind)
    {
        var trimmed = HardwareOfficeNames.Normalize(name);
        if (trimmed.Length == 0)
            return;

        var existing = Offices.FirstOrDefault(office => HardwareOfficeNames.SamePlace(office.Name, trimmed));
        if (existing is null)
        {
            var option = new HardwareOfficeOption(trimmed) { LabelKind = kind };
            option.SelectionChanged += OnOfficeSelectionChanged;
            Offices.Add(option);
            if (selected)
                option.IsSelected = true;
            return;
        }

        var adopt = HardwareOfficeNames.UseIncomingLabel(existing.Name, existing.LabelKind, trimmed, kind);
        if ((int)kind > (int)existing.LabelKind)
            existing.LabelKind = kind;
        if (adopt)
        {
            existing.Name = trimmed;
            RefreshVisibleOffices();
        }

        if (selected && !existing.IsSelected)
            existing.IsSelected = true;
    }

    private void OnOfficeSelectionChanged(object? sender, EventArgs e)
    {
        if (!_suppressOffice)
            OnPropertyChanged(nameof(OfficeSelectionSummary));
        if (_suppressOffice || _client is null)
            return;

        _missingLocationNotice = false;
        OfficeLoad = RefreshCoreAsync();
    }

    private List<string> SelectedOfficeNames() =>
        Offices
            .Where(office => office.IsSelected)
            .Select(office => office.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void PublishScope(IReadOnlyList<string> offices)
    {
        if (_locationFilterIgnored && offices.Count > 0)
            OfficeStatus = "The location filter did not apply. This page is not limited to " + string.Join(", ", offices) + ".";
        else if (offices.Count == 0 && _missingLocationNotice)
            OfficeStatus = "No location on the signed-in account. Showing all locations.";
        else if (offices.Count == 0)
            OfficeStatus = "Showing all locations.";
        else
            OfficeStatus = "Showing " + string.Join(", ", offices) + ".";
        OnPropertyChanged(nameof(OfficeSelectionSummary));
    }

    private void ApplyColumnFilters(string? keepSysId)
    {
        _suppressSelection = true;
        Items.Clear();
        foreach (var asset in _loadedRows)
        {
            if (PassesColumnFilters(asset))
                Items.Add(asset);
        }

        Selected = string.IsNullOrEmpty(keepSysId)
            ? null
            : Items.FirstOrDefault(asset => asset.SysId == keepSysId);
        _suppressSelection = false;
    }

    private bool PassesColumnFilters(HardwareAsset asset) =>
        ColumnMatch(asset.SerialNumber, SerialFilter)
        && ColumnMatch(asset.Model, ModelFilter)
        && ColumnMatch(asset.AssignedTo.Display, AssignedFilter)
        && ColumnMatch(asset.Location.Display, LocationFilter)
        && ColumnMatch(asset.InstallStatusLabel, StateFilter)
        && ColumnMatch(asset.SubstatusLabel, SubstatusFilter)
        && ColumnMatch(asset.Comments, CommentsFilter);

    private static bool ColumnMatch(string? value, string? filter)
    {
        var term = (filter ?? "").Trim();
        if (term.Length == 0)
            return true;

        return (value ?? "").Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<Choice>> ReadChoicesAsync(string element, string? dependent, IReadOnlyList<Choice> fallback)
    {
        if (_client is null)
            return fallback;

        try
        {
            var choices = await _client.GetChoicesAsync("alm_hardware", element, dependent, CancellationToken.None);
            if (choices is null || choices.Count == 0)
                return fallback;
            return choices;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool ReferenceIsChosen(ReferenceFieldModel field)
    {
        var text = field.Text ?? "";
        var id = field.SysId ?? "";
        return string.IsNullOrWhiteSpace(text) == (id.Length == 0);
    }

    private Task<IReadOnlyList<ReferenceSuggestion>> SearchUsersAsync(string text, CancellationToken cancellationToken) =>
        _client is null
            ? Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([])
            : _client.SearchUsersAsync(text, cancellationToken);

    private Task<IReadOnlyList<ReferenceSuggestion>> MatchUsersAsync(string text, CancellationToken cancellationToken) =>
        _client is null
            ? Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([])
            : _client.MatchUsersAsync(text, cancellationToken);

    private Task<IReadOnlyList<ReferenceSuggestion>> SearchLocationsAsync(string text, CancellationToken cancellationToken) =>
        _client is null
            ? Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([])
            : _client.SearchLocationsAsync(text, cancellationToken);

    private Task<IReadOnlyList<ReferenceSuggestion>> SearchStockroomsAsync(string text, CancellationToken cancellationToken) =>
        _client is null
            ? Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([])
            : _client.SearchStockroomsAsync(text, cancellationToken);
}

public partial class HardwareOfficeOption : ObservableObject
{
    public HardwareOfficeOption(string officeName)
    {
        name = officeName;
    }

    [ObservableProperty] private string name;

    internal HardwareOfficeLabelKind LabelKind { get; set; }

    [ObservableProperty] private bool isSelected;

    public event EventHandler? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this, EventArgs.Empty);
}

public partial class HardwareScanRow : ObservableObject
{
    [ObservableProperty] private string text = "";
    [ObservableProperty] private string hint = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string detail = "";
    [ObservableProperty] private string matchedSerial = "";
    [ObservableProperty] private string oldState = "";
    [ObservableProperty] private string newState = "";
    [ObservableProperty] private string model = "";
    [ObservableProperty] private string assignedToName = "";

    public string LastText { get; set; } = "\0";

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    partial void OnTextChanged(string value) => Hint = HardwareSerial.HintFor(value);

    partial void OnHintChanged(string value) => OnPropertyChanged(nameof(HasHint));
}
