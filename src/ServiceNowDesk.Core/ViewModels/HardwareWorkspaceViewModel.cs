using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public partial class HardwareWorkspaceViewModel : ObservableObject
{
    /// <summary>
    /// Pause after the last column-filter keystroke before rebuilding the visible list.
    /// Keeps typing responsive on large catalogs and drives the Filtering… indicator.
    /// </summary>
    public static readonly TimeSpan ColumnFilterDelay = TimeSpan.FromMilliseconds(150);

    private readonly ISettingsStore? _settings;
    private readonly IHardwareCatalogStore _catalogStore;
    private readonly Func<string> _cacheScope;
    private readonly List<HardwareAsset> _catalog = [];
    private readonly List<HardwareAsset> _loadedRows = [];
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _columnFilterCts;
    private IServiceNowClient? _client;
    private HardwareAsset? _loaded;
    private bool _choicesReady;
    private bool _suppressSelection;
    private bool _suppressColumnFilterSchedule;
    private bool _officesReady;
    private bool _locationLookupDone;
    private bool _suppressOffice;
    private bool _suppressRegion;
    private bool _missingLocationNotice;
    private bool _overrideActive;
    private bool _catalogIsAllLocations;
    private DateTimeOffset _catalogCapturedAt;
    private string _signedInLocation = "";
    private int _substateGeneration;
    private HardwareSortColumn? _sortColumn;
    private bool _sortDescending;
    private readonly HashSet<string> _selectedModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedLocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedSubstates = new(StringComparer.OrdinalIgnoreCase);

    public HardwareWorkspaceViewModel(
        ISettingsStore? settings = null,
        IHardwareCatalogStore? catalogStore = null,
        Func<string>? cacheScope = null)
    {
        _settings = settings;
        _catalogStore = catalogStore ?? new MemoryHardwareCatalogStore();
        _cacheScope = cacheScope ?? (() => DeskListScope.Practice);
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
        RegionPicker = new RegionSettingsViewModel();
        RegionPicker.Changed += (_, _) =>
        {
            if (_suppressRegion || _client is null)
                return;
            OfficeLoad = ApplyHardwareRegionAsync();
        };
    }

    /// <summary>
    /// Region tree for this hardware window. It does not write the Settings region.
    /// </summary>
    public RegionSettingsViewModel RegionPicker { get; }

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
    public ObservableCollection<string> ModelChoices { get; } = [];
    public ObservableCollection<string> LocationChoices { get; } = [];
    public ObservableCollection<string> StateChoices { get; } = [];
    public ObservableCollection<string> SubstateChoices { get; } = [];

    public bool HasBatch => Batch.Count > 0;
    public bool ShowSubstate => Substatuses.Any(choice => !string.IsNullOrEmpty(choice.Value));
    public string StateLabelText => HardwareCatalog.LabelOf(InstallStatuses, InstallStatus);
    public bool StockroomRequired => HardwareCatalog.RequiresStockroom(StateLabelText);
    public bool LocationRequired => HardwareCatalog.RequiresLocation(StateLabelText);

    public Task SubstateLoad { get; private set; } = Task.CompletedTask;
    public Task OpenTask { get; private set; } = Task.CompletedTask;
    public Task StockroomApply { get; private set; } = Task.CompletedTask;
    public Task OfficeLoad { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Completes when the latest scheduled column-filter pass has been applied (or cancelled).
    /// </summary>
    public Task ColumnFiltersReady { get; private set; } = Task.CompletedTask;

    public event EventHandler? DefaultSaved;

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string officeSearchText = "";
    [ObservableProperty] private string officeStatus = "";
    [ObservableProperty] private string catalogStatus = "";
    [ObservableProperty] private string serialFilter = "";
    [ObservableProperty] private string assignedFilter = "";
    [ObservableProperty] private string commentsFilter = "";
    [ObservableProperty] private int choiceFilterRevision;
    [ObservableProperty] private bool searchTipsOpen;
    [ObservableProperty] private HardwareAsset? selected;
    [ObservableProperty] private bool hasEditor;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isDownloading;
    [ObservableProperty] private bool isFiltering;
    [ObservableProperty] private int downloadPercent;
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
        _locationLookupDone = false;
    }

    /// <summary>
    /// True when the in-memory catalog is empty or older than <see cref="FormCatalogPolicy.MaxAge"/>.
    /// </summary>
    public bool CatalogNeedsRefresh =>
        _catalog.Count == 0
        || FormCatalogPolicy.IsStale(_catalogCapturedAt, DateTimeOffset.UtcNow);

    /// <summary>Computers currently held in the hardware cache (in memory or last load from disk).</summary>
    public int CatalogCount => _catalog.Count;

    /// <summary>When the hardware catalog was last saved; default when never downloaded.</summary>
    public DateTimeOffset CatalogCapturedAt => _catalogCapturedAt;

    /// <summary>
    /// Loads the on-disk catalog immediately (local office seeds only — no network) so
    /// search and office filters work while startup or a background refresh is still running.
    /// </summary>
    public void ShowSavedCatalog()
    {
        if (_client is null)
            return;

        if (!_officesReady)
            PrepareOfficesLocal();
        if (_catalog.Count == 0)
            LoadCatalogFromStore();

        var offices = SelectedOfficeNames();
        ApplyCatalogToRows(offices);
        PublishScope(offices);
    }

    /// <summary>
    /// Refreshes the saved catalog in the background for the selected offices (merge).
    /// Full all-locations download stays on Refresh all. Region filters the loaded list by Settings offices.
    /// The current list stays searchable until the download finishes and is applied.
    /// </summary>
    public Task RefreshCatalogInBackgroundAsync() =>
        DownloadCatalogAsync(allLocations: false, merge: true);

    public void RememberViewer(CurrentUser? user)
    {
        _signedInLocation = user?.Location?.Trim() ?? "";
        OnPropertyChanged(nameof(AccountOfficeText));
    }

    /// <summary>
    /// The office name on the signed-in account, shown even when an override is active.
    /// </summary>
    public string AccountOfficeText =>
        string.IsNullOrWhiteSpace(_signedInLocation)
            ? "No location on the signed-in account."
            : "Your office: " + _signedInLocation;

    public void Detach()
    {
        _client = null;
        _choicesReady = false;
        _officesReady = false;
        _suppressRegion = true;
        RegionPicker.Load(null);
        _suppressRegion = false;
        _locationLookupDone = false;
        _missingLocationNotice = false;
        _overrideActive = false;
        _signedInLocation = "";
        OnPropertyChanged(nameof(AccountOfficeText));
        _loaded = null;
        HasEditor = false;
        IsDirty = false;
        _downloadCts?.Cancel();
        _catalog.Clear();
        _catalogIsAllLocations = false;
        _catalogCapturedAt = default;
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
        _columnFilterCts?.Cancel();
        _columnFilterCts = null;
        ColumnFiltersReady = Task.CompletedTask;
        IsFiltering = false;
        _suppressColumnFilterSchedule = true;
        SerialFilter = "";
        AssignedFilter = "";
        CommentsFilter = "";
        _selectedModels.Clear();
        _selectedLocations.Clear();
        _selectedStates.Clear();
        _selectedSubstates.Clear();
        ModelChoices.Clear();
        LocationChoices.Clear();
        StateChoices.Clear();
        SubstateChoices.Clear();
        TouchChoiceFilters();
        _suppressColumnFilterSchedule = false;
        _sortColumn = null;
        _sortDescending = false;
        NotifySortHeaders();
        SearchTipsOpen = false;
        OfficeSearchText = "";
        OfficeStatus = "";
        CatalogStatus = "";
        DownloadPercent = 0;
        IsDownloading = false;
        OnPropertyChanged(nameof(OfficeSelectionSummary));
        OnPropertyChanged(nameof(ShownCount));
    }

    /// <summary>
    /// Rows currently visible after office and column filters. Notifies when the list rebuilds.
    /// </summary>
    public int ShownCount => Items.Count;

    public string SerialHeader => SortHeader("Serial", HardwareSortColumn.Serial);
    public string ModelHeader => SortHeader("Model", HardwareSortColumn.Model);
    public string AssignedHeader => SortHeader("Assigned to", HardwareSortColumn.Assigned);
    public string LocationHeader => SortHeader("Location", HardwareSortColumn.Location);
    public string StateHeader => SortHeader("State", HardwareSortColumn.State);
    public string SubstateHeader => SortHeader("Substate", HardwareSortColumn.Substate);
    public string CommentsHeader => SortHeader("Comments", HardwareSortColumn.Comments);

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

            // Disk first: office seeds + saved catalog before any network so search works
            // while location lookups / choice lists / a background refresh are still running.
            if (!_officesReady)
                PrepareOfficesLocal();
            if (_catalog.Count == 0)
                LoadCatalogFromStore();

            var offices = SelectedOfficeNames();
            ApplyCatalogToRows(offices);
            PublishScope(offices);
            var hadCached = _catalog.Count > 0;

            await EnsureChoicesAsync();
            await EnsureReferenceLocationsAsync();

            offices = SelectedOfficeNames();
            ApplyCatalogToRows(offices);
            PublishScope(offices);

            if (!hadCached && _catalog.Count == 0)
            {
                // First open downloads the selected offices only (or all when none are checked).
                _refreshGate.Release();
                try
                {
                    await DownloadDefaultCatalogAsync();
                    ApplyCatalogToRows(SelectedOfficeNames());
                    PublishScope(SelectedOfficeNames());
                }
                finally
                {
                    await _refreshGate.WaitAsync();
                }
            }
            else if (hadCached && CatalogNeedsRefresh && !IsDownloading)
            {
                // Keep the saved list searchable; merge office-scoped results when the refresh finishes.
                _ = DownloadDefaultCatalogAsync();
            }
            else if (offices.Count > 0 && _loadedRows.Count == 0)
            {
                CatalogStatus = "No computers for the selected office in the saved list. Use Refresh this office or Refresh all.";
            }
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

    [RelayCommand]
    private Task RefreshOfficeCatalogAsync() => DownloadCatalogAsync(allLocations: false, merge: true);

    [RelayCommand]
    private Task RefreshAllCatalogAsync() => DownloadCatalogAsync(allLocations: true, merge: false);

    /// <summary>
    /// Launch / stale refresh: selected offices with merge. When no office is checked
    /// (no account location), fall back to all locations so the tab is still usable.
    /// </summary>
    private Task DownloadDefaultCatalogAsync()
    {
        if (!_officesReady)
            PrepareOfficesLocal();
        var offices = SelectedOfficeNames();
        if (offices.Count == 0)
            return DownloadCatalogAsync(allLocations: true, merge: false);
        return DownloadCatalogAsync(allLocations: false, merge: true);
    }

    private async Task DownloadCatalogAsync(bool allLocations, bool merge)
    {
        if (_client is null || IsDownloading)
            return;

        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();
        var token = _downloadCts.Token;
        IsDownloading = true;
        DownloadPercent = 0;
        ErrorMessage = "";

        // Prefer the last on-disk catalog immediately so search/filters work while the network run finishes.
        if (_catalog.Count == 0)
            LoadCatalogFromStore();
        var selectedBeforeDownload = SelectedOfficeNames();
        if (_catalog.Count > 0)
        {
            // Keep the current office selection on screen; the finished download refreshes rows afterward.
            ApplyCatalogToRows(selectedBeforeDownload);
            PublishScope(selectedBeforeDownload);
        }

        var usingSaved = _catalog.Count;
        CatalogStatus = usingSaved > 0
            ? (allLocations ? "Downloading all computers…" : "Downloading computers for the selected office…")
              + " Using "
              + usingSaved.ToString(System.Globalization.CultureInfo.InvariantCulture)
              + " saved on this PC."
            : (allLocations ? "Downloading all computers…" : "Downloading computers for the selected office…");

        try
        {
            if (!_officesReady)
                PrepareOfficesLocal();
            await EnsureReferenceLocationsAsync();

            IReadOnlyList<string> offices = allLocations ? [] : SelectedOfficeNames();
            IReadOnlyList<string> ids = allLocations ? [] : SelectedLocationSysIds();
            if (!allLocations && offices.Count == 0 && ids.Count == 0)
            {
                CatalogStatus = "Check an office, or use Refresh all.";
                return;
            }

            var previous = _catalog.ToDictionary(asset => asset.SysId, StringComparer.OrdinalIgnoreCase);
            var previousCount = previous.Count;
            var progress = new Progress<DownloadTick>(tick => DownloadPercent = tick.Percent);
            var downloaded = await _client.DownloadHardwareAsync(offices, ids, progress, token);
            var kept = downloaded.Assets
                .Where(asset => allLocations || HardwareCatalog.MatchesLocation(asset, offices))
                .ToArray();

            // An empty full refresh must not wipe a good prior cache (same lesson as ticket lists).
            if (kept.Length == 0 && previousCount > 0 && !(merge && !allLocations))
            {
                DownloadPercent = 100;
                CatalogStatus = "Download returned no computers; kept the "
                    + previousCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " saved on this PC.";
                return;
            }

            var newCount = 0;
            var updatedCount = 0;
            if (merge && !allLocations)
            {
                foreach (var asset in kept)
                {
                    if (!previous.TryGetValue(asset.SysId, out var old))
                    {
                        previous[asset.SysId] = asset;
                        newCount++;
                    }
                    else
                    {
                        previous[asset.SysId] = asset;
                        if (!SameAsset(old, asset))
                            updatedCount++;
                    }
                }

                _catalog.Clear();
                _catalog.AddRange(previous.Values.OrderBy(asset => asset.SerialNumber, StringComparer.OrdinalIgnoreCase));
                _catalogIsAllLocations = _catalogIsAllLocations || false;
            }
            else
            {
                foreach (var asset in kept)
                {
                    if (!previous.ContainsKey(asset.SysId))
                        newCount++;
                    else if (!SameAsset(previous[asset.SysId], asset))
                        updatedCount++;
                }

                _catalog.Clear();
                _catalog.AddRange(kept.OrderBy(asset => asset.SerialNumber, StringComparer.OrdinalIgnoreCase));
                _catalogIsAllLocations = allLocations;
            }

            SaveCatalogToStore(offices);
            var selected = SelectedOfficeNames();
            ApplyCatalogToRows(selected);
            PublishScope(selected);
            DownloadPercent = 100;

            if (downloaded.Truncated)
            {
                CatalogStatus = _catalog.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " saved; stopped at the "
                    + FormCatalogPolicy.MaxHardwareAssets.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " limit.";
            }
            else if (previousCount == 0)
            {
                CatalogStatus = "Downloaded "
                    + _catalog.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " computers.";
            }
            else
            {
                CatalogStatus = "Downloaded "
                    + kept.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " computers. "
                    + newCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " new, "
                    + updatedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " updated. "
                    + _catalog.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " saved on this PC.";
            }

            if (selected.Count > 0 && _loadedRows.Count == 0)
            {
                CatalogStatus = CatalogStatus
                    + " No computers for the selected office in the saved list. Use Refresh this office or Refresh all.";
            }
        }
        catch (OperationCanceledException)
        {
            CatalogStatus = "Download cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
            CatalogStatus = "Update finished with errors.";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private void LoadCatalogFromStore()
    {
        var snapshot = _catalogStore.Load(_cacheScope());
        _catalog.Clear();
        if (snapshot?.Items is null || snapshot.Items.Count == 0)
        {
            _catalogIsAllLocations = false;
            _catalogCapturedAt = default;
            CatalogStatus = "No computers saved on this PC yet.";
            return;
        }

        _catalog.AddRange(snapshot.Items);
        _catalogIsAllLocations = snapshot.AllLocations;
        _catalogCapturedAt = snapshot.CapturedAt;
        CatalogStatus = _catalog.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " computers saved"
            + (snapshot.CapturedAt == default
                ? "."
                : " as of " + snapshot.CapturedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) + ".");
    }

    private void SaveCatalogToStore(IReadOnlyList<string> offices)
    {
        _catalogCapturedAt = DateTimeOffset.UtcNow;
        _catalogStore.Save(_cacheScope(), new HardwareCatalogSnapshot
        {
            CapturedAt = _catalogCapturedAt,
            AllLocations = _catalogIsAllLocations,
            Offices = offices.ToList(),
            Items = [.. _catalog]
        });
    }

    private void ApplyCatalogToRows(IReadOnlyList<string> offices)
    {
        var keep = Selected?.SysId ?? _loaded?.SysId;
        _loadedRows.Clear();
        foreach (var asset in _catalog)
        {
            if (HardwareCatalog.MatchesLocation(asset, offices) && HardwareCatalog.MatchesSearch(asset, SearchText))
                _loadedRows.Add(asset);
        }

        ApplyColumnFilters(keep);
    }

    private static bool SameAsset(HardwareAsset left, HardwareAsset right) =>
        left.SerialNumber == right.SerialNumber
        && left.Model == right.Model
        && left.AssignedTo.SysId == right.AssignedTo.SysId
        && left.Location.SysId == right.Location.SysId
        && left.InstallStatus == right.InstallStatus
        && left.Substatus == right.Substatus
        && left.Comments == right.Comments;

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

    partial void OnSearchTextChanged(string value)
    {
        if (_client is null)
            return;
        ApplyCatalogToRows(SelectedOfficeNames());
    }

    partial void OnSerialFilterChanged(string value) => ScheduleColumnFilters();

    partial void OnAssignedFilterChanged(string value) => ScheduleColumnFilters();

    partial void OnCommentsFilterChanged(string value) => ScheduleColumnFilters();

    public const string RegionOfficesMissingMessage = "Choose region offices in Settings.";
    public const string RegionNoLocationsMessage = "No loaded locations match the region offices in Settings.";
    public const string RegionAppliedMessage = "Showing hardware in your region.";

    public string ModelFilterCountText => _selectedModels.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string LocationFilterCountText => _selectedLocations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string StateFilterCountText => _selectedStates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string SubstateFilterCountText => _selectedSubstates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public IReadOnlyList<string> SelectedModelFilters => Ordered(_selectedModels);
    public IReadOnlyList<string> SelectedLocationFilters => Ordered(_selectedLocations);
    public IReadOnlyList<string> SelectedStateFilters => Ordered(_selectedStates);
    public IReadOnlyList<string> SelectedSubstateFilters => Ordered(_selectedSubstates);

    public void SetModelFilter(IEnumerable<string>? values) => ReplaceSelection(_selectedModels, values);

    public void SetLocationFilter(IEnumerable<string>? values) => ReplaceSelection(_selectedLocations, values);

    public void SetStateFilter(IEnumerable<string>? values) => ReplaceSelection(_selectedStates, values);

    public void SetSubstateFilter(IEnumerable<string>? values) => ReplaceSelection(_selectedSubstates, values);

    [RelayCommand]
    public void ClearModelFilter() => SetModelFilter(null);

    [RelayCommand]
    public void ClearLocationFilter() => SetLocationFilter(null);

    [RelayCommand]
    public void ClearStateFilter() => SetStateFilter(null);

    [RelayCommand]
    public void ClearSubstateFilter() => SetSubstateFilter(null);

    /// <summary>
    /// Selects the loaded locations that belong to the offices saved on Settings.
    /// No saved offices leaves the location filter unchanged and says so.
    /// </summary>
    [RelayCommand]
    private void ApplyRegion()
    {
        var saved = _settings is null
            ? []
            : RegionOfficeMatch.NormalizeOffices(_settings.Load().RegionOffices);
        if (saved.Count == 0)
        {
            OfficeStatus = RegionOfficesMissingMessage;
            return;
        }

        var matches = RegionOfficeMatch.LocationsInRegion(LocationChoices, saved);
        SetLocationFilter(matches);
        OfficeStatus = matches.Count == 0 ? RegionNoLocationsMessage : RegionAppliedMessage;
    }

    private void ScheduleColumnFilters()
    {
        if (_suppressColumnFilterSchedule)
            return;

        IsFiltering = true;
        _columnFilterCts?.Cancel();
        var cts = new CancellationTokenSource();
        _columnFilterCts = cts;
        ColumnFiltersReady = ApplyColumnFiltersWhenQuietAsync(cts.Token);
    }

    private async Task ApplyColumnFiltersWhenQuietAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ColumnFilterDelay, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ApplyColumnFiltersCore(Selected?.SysId);
        if (!token.IsCancellationRequested)
            IsFiltering = false;
    }

    [RelayCommand]
    private async Task SearchAllLocationsAsync()
    {
        _missingLocationNotice = false;
        _suppressOffice = true;
        foreach (var office in Offices)
            office.IsSelected = false;
        _suppressOffice = false;
        OnPropertyChanged(nameof(OfficeSelectionSummary));
        // Show whatever is already on disk / in memory immediately; refresh in the background when needed.
        if (_catalog.Count == 0)
            LoadCatalogFromStore();
        ApplyCatalogToRows([]);
        PublishScope([]);
        if (_catalog.Count == 0 || !_catalogIsAllLocations)
            await DownloadCatalogAsync(allLocations: true, merge: false);
    }

    [RelayCommand]
    private async Task OverrideOfficeAsync()
    {
        var names = SelectedOfficeNames();
        _overrideActive = true;
        _missingLocationNotice = false;
        if (_settings is not null)
        {
            var settings = _settings.Load();
            settings.HardwareOfficeLocations = names;
            settings.HardwareOfficeOverride = true;
            _settings.Save(settings);
        }

        DefaultSaved?.Invoke(this, EventArgs.Empty);
        // Pull the override offices into the local catalog (merge) so the grid is not empty
        // when those cities were never part of a prior office-scoped download.
        await DownloadDefaultCatalogAsync();
    }

    [RelayCommand]
    private async Task ClearOverrideAsync()
    {
        _overrideActive = false;
        if (_settings is not null)
        {
            var settings = _settings.Load();
            settings.HardwareOfficeLocations = null;
            settings.HardwareOfficeOverride = false;
            _settings.Save(settings);
        }

        _suppressOffice = true;
        foreach (var office in Offices)
            office.IsSelected = false;
        if (!string.IsNullOrWhiteSpace(_signedInLocation))
        {
            _missingLocationNotice = false;
            EnsureOffice(_signedInLocation, true, HardwareOfficeLabelKind.Account);
        }
        else
            _missingLocationNotice = true;

        _suppressOffice = false;
        SyncRegionPicker();
        OnPropertyChanged(nameof(OfficeSelectionSummary));
        DefaultSaved?.Invoke(this, EventArgs.Empty);
        await DownloadDefaultCatalogAsync();
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
        var catalogIndex = _catalog.FindIndex(asset => asset.SysId == updated.SysId);
        if (catalogIndex >= 0)
            _catalog[catalogIndex] = updated;

        var index = _loadedRows.FindIndex(asset => asset.SysId == updated.SysId);
        if (index < 0)
        {
            ApplyCatalogToRows(SelectedOfficeNames());
            return;
        }

        _loadedRows[index] = updated;
        ApplyColumnFilters(updated.SysId);
        SaveCatalogToStore(SelectedOfficeNames());
    }

    /// <summary>
    /// Seeds office checkboxes from known cities, the signed-in account, and any override.
    /// Does not call ServiceNow — safe to run before splash downloads finish.
    /// </summary>
    private void PrepareOfficesLocal()
    {
        if (_officesReady)
            return;

        _suppressOffice = true;
        try
        {
            Offices.Clear();
            foreach (var name in HardwareCatalog.KnownOfficeNames)
                EnsureOffice(name, false, HardwareOfficeLabelKind.Seed);
            EnsureOffice(_signedInLocation, false, HardwareOfficeLabelKind.Account);

            if (ReadOverride(out var saved))
            {
                _overrideActive = true;
                _missingLocationNotice = false;
                foreach (var name in saved)
                    EnsureOffice(name, true, HardwareOfficeLabelKind.Seed);
            }
            else if (!string.IsNullOrWhiteSpace(_signedInLocation))
            {
                _overrideActive = false;
                _missingLocationNotice = false;
                EnsureOffice(_signedInLocation, true, HardwareOfficeLabelKind.Account);
            }
            else
            {
                _overrideActive = false;
                _missingLocationNotice = true;
            }
        }
        finally
        {
            _suppressOffice = false;
            _officesReady = true;
            SyncRegionPicker();
        }
    }

    private void SyncRegionPicker()
    {
        _suppressRegion = true;
        try
        {
            if (_overrideActive)
                RegionPicker.LoadMatched(SelectedOfficeNames());
            else
                RegionPicker.Load(null);
        }
        finally
        {
            _suppressRegion = false;
        }
    }

    /// <summary>
    /// The hardware-window region. Saved offices stay on the hardware override and do not change Settings.
    /// An empty selection clears that override and returns to the signed-in office.
    /// </summary>
    private async Task ApplyHardwareRegionAsync()
    {
        var offices = RegionPicker.SelectedOffices;
        if (offices.Count == 0)
        {
            await ClearOverrideAsync();
            return;
        }

        _suppressOffice = true;
        try
        {
            foreach (var office in Offices.ToArray())
                office.IsSelected = false;
            foreach (var name in offices)
                EnsureOffice(name, true, HardwareOfficeLabelKind.Seed);
        }
        finally
        {
            _suppressOffice = false;
        }

        _overrideActive = true;
        _missingLocationNotice = false;
        if (_settings is not null)
        {
            var settings = _settings.Load();
            settings.HardwareOfficeLocations = SelectedOfficeNames();
            settings.HardwareOfficeOverride = true;
            _settings.Save(settings);
        }

        DefaultSaved?.Invoke(this, EventArgs.Empty);
        var selected = SelectedOfficeNames();
        if (_catalog.Count == 0)
            LoadCatalogFromStore();
        ApplyCatalogToRows(selected);
        PublishScope(selected);
        await DownloadDefaultCatalogAsync();
    }

    private async Task EnsureReferenceLocationsAsync()
    {
        if (_locationLookupDone || _client is null)
            return;

        _locationLookupDone = true;
        await AddReferenceLocationsAsync();
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
                EnsureOffice(place.Display, false, HardwareOfficeLabelKind.Reference, place.SysId);
        }
    }

    /// <summary>
    /// A saved office list applies only after Override office. An older list without that flag
    /// is left unused so it cannot open the tab on an empty grid.
    /// </summary>
    private bool ReadOverride(out IReadOnlyList<string> offices)
    {
        offices = [];
        if (_settings is null)
            return false;

        var settings = _settings.Load();
        if (!settings.HardwareOfficeOverride)
            return false;

        offices = (settings.HardwareOfficeLocations ?? [])
            .Select(name => name?.Trim() ?? "")
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return true;
    }

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
    private void EnsureOffice(string? name, bool selected, HardwareOfficeLabelKind kind, string? sysId = null)
    {
        var trimmed = HardwareOfficeNames.Normalize(name);
        if (trimmed.Length == 0)
            return;

        var existing = Offices.FirstOrDefault(office => HardwareOfficeNames.SamePlace(office.Name, trimmed));
        if (existing is null)
        {
            var option = new HardwareOfficeOption(trimmed) { LabelKind = kind, SysId = sysId?.Trim() ?? "" };
            option.SelectionChanged += OnOfficeSelectionChanged;
            Offices.Add(option);
            if (selected)
                option.IsSelected = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(existing.SysId) && !string.IsNullOrWhiteSpace(sysId))
            existing.SysId = sysId.Trim();

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
        var offices = SelectedOfficeNames();
        if (_catalog.Count == 0)
            LoadCatalogFromStore();
        ApplyCatalogToRows(offices);
        PublishScope(offices);
        if (_catalog.Count == 0)
            OfficeLoad = DownloadDefaultCatalogAsync();
        else if (offices.Count > 0 && _loadedRows.Count == 0)
            CatalogStatus = "No computers for the selected office in the saved list. Use Refresh this office or Refresh all.";
    }

    private bool IsAccountSelection(IReadOnlyList<string> offices) =>
        offices.Count == 1 && HardwareOfficeNames.SamePlace(offices[0], _signedInLocation);

    private List<string> SelectedOfficeNames() =>
        Offices
            .Where(office => office.IsSelected)
            .Select(office => office.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private string[] SelectedLocationSysIds() =>
        Offices
            .Where(office => office.IsSelected && !string.IsNullOrWhiteSpace(office.SysId))
            .Select(office => office.SysId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void PublishScope(IReadOnlyList<string> offices)
    {
        if (_overrideActive)
            OfficeStatus = offices.Count == 0
                ? "Showing all locations."
                : "Showing override: " + string.Join(", ", offices) + ".";
        else if (offices.Count == 0 && _missingLocationNotice)
            OfficeStatus = "No location on the signed-in account. Showing all locations.";
        else if (offices.Count == 0)
            OfficeStatus = "Showing all locations.";
        else if (IsAccountSelection(offices))
            OfficeStatus = "Showing your office: " + _signedInLocation + ".";
        else
            OfficeStatus = "Showing " + string.Join(", ", offices) + ".";
        OnPropertyChanged(nameof(OfficeSelectionSummary));
    }

    private void ApplyColumnFilters(string? keepSysId)
    {
        _columnFilterCts?.Cancel();
        _columnFilterCts = null;
        ColumnFiltersReady = Task.CompletedTask;
        ApplyColumnFiltersCore(keepSysId);
        IsFiltering = false;
    }

    private void ApplyColumnFiltersCore(string? keepSysId)
    {
        if (RefreshChoiceLists())
            TouchChoiceFilters();

        _suppressSelection = true;
        Items.Clear();
        var matches = _loadedRows.Where(PassesColumnFilters).ToList();
        if (_sortColumn is HardwareSortColumn column && matches.Count > 1)
        {
            matches.Sort((left, right) =>
            {
                var compared = HardwareAssetSort.Compare(left, right, column, _sortDescending);
                return compared != 0
                    ? compared
                    : string.Compare(left.SerialNumber, right.SerialNumber, StringComparison.OrdinalIgnoreCase);
            });
        }

        foreach (var asset in matches)
            Items.Add(asset);

        Selected = string.IsNullOrEmpty(keepSysId)
            ? null
            : Items.FirstOrDefault(asset => asset.SysId == keepSysId);
        _suppressSelection = false;
        OnPropertyChanged(nameof(ShownCount));
    }

    [RelayCommand]
    private void SortBy(string? column)
    {
        if (!Enum.TryParse<HardwareSortColumn>(column, ignoreCase: true, out var parsed))
            return;
        if (_sortColumn != parsed)
        {
            _sortColumn = parsed;
            _sortDescending = false;
        }
        else
        {
            _sortDescending = !_sortDescending;
        }

        ApplySort();
    }

    private void ApplySort()
    {
        NotifySortHeaders();
        if (_sortColumn is not HardwareSortColumn column || Items.Count < 2)
            return;

        var keepSysId = Selected?.SysId;
        var sorted = Items.Select((asset, index) => (asset, index)).ToList();
        sorted.Sort((left, right) =>
        {
            var compared = HardwareAssetSort.Compare(left.asset, right.asset, column, _sortDescending);
            if (compared != 0)
                return compared;
            var serial = string.Compare(
                left.asset.SerialNumber,
                right.asset.SerialNumber,
                StringComparison.OrdinalIgnoreCase);
            return serial != 0 ? serial : left.index.CompareTo(right.index);
        });

        _suppressSelection = true;
        for (var index = 0; index < sorted.Count; index++)
        {
            var current = Items.IndexOf(sorted[index].asset);
            if (current != index)
                Items.Move(current, index);
        }

        Selected = string.IsNullOrEmpty(keepSysId)
            ? null
            : Items.FirstOrDefault(asset => asset.SysId == keepSysId);
        _suppressSelection = false;
    }

    private string SortHeader(string title, HardwareSortColumn column) =>
        _sortColumn == column ? title + (_sortDescending ? " ▼" : " ▲") : title;

    private void NotifySortHeaders()
    {
        OnPropertyChanged(nameof(SerialHeader));
        OnPropertyChanged(nameof(ModelHeader));
        OnPropertyChanged(nameof(AssignedHeader));
        OnPropertyChanged(nameof(LocationHeader));
        OnPropertyChanged(nameof(StateHeader));
        OnPropertyChanged(nameof(SubstateHeader));
        OnPropertyChanged(nameof(CommentsHeader));
    }

    private bool PassesColumnFilters(HardwareAsset asset) =>
        HardwareCatalog.MatchesColumn(asset.SerialNumber, SerialFilter)
        && HardwareColumnFilter.Matches(asset.Model, _selectedModels)
        && HardwareCatalog.MatchesColumn(asset.AssignedTo.Display, AssignedFilter)
        && HardwareColumnFilter.Matches(asset.Location.Display, _selectedLocations)
        && HardwareColumnFilter.Matches(asset.InstallStatusLabel, _selectedStates)
        && HardwareColumnFilter.Matches(asset.SubstatusLabel, _selectedSubstates)
        && HardwareCatalog.MatchesColumn(asset.Comments, CommentsFilter);

    private void ReplaceSelection(HashSet<string> target, IEnumerable<string>? values)
    {
        target.Clear();
        if (values is not null)
        {
            foreach (var value in values)
            {
                var text = (value ?? "").Trim();
                if (text.Length > 0)
                    target.Add(text);
            }
        }

        TouchChoiceFilters();
        ScheduleColumnFilters();
    }

    private void TouchChoiceFilters()
    {
        OnPropertyChanged(nameof(ModelFilterCountText));
        OnPropertyChanged(nameof(LocationFilterCountText));
        OnPropertyChanged(nameof(StateFilterCountText));
        OnPropertyChanged(nameof(SubstateFilterCountText));
        OnPropertyChanged(nameof(SelectedModelFilters));
        OnPropertyChanged(nameof(SelectedLocationFilters));
        OnPropertyChanged(nameof(SelectedStateFilters));
        OnPropertyChanged(nameof(SelectedSubstateFilters));
        ChoiceFilterRevision++;
    }

    private bool RefreshChoiceLists()
    {
        var changed = false;
        changed |= UpdateChoices(ModelChoices, _selectedModels, _loadedRows.Select(asset => asset.Model), preferred: null);
        changed |= UpdateChoices(LocationChoices, _selectedLocations, _loadedRows.Select(asset => asset.Location.Display), preferred: null);
        changed |= UpdateChoices(StateChoices, _selectedStates, _loadedRows.Select(asset => asset.InstallStatusLabel), HardwareColumnFilter.StateOrder);
        changed |= UpdateChoices(SubstateChoices, _selectedSubstates, _loadedRows.Select(asset => asset.SubstatusLabel), preferred: null);
        return changed;
    }

    private static bool UpdateChoices(
        ObservableCollection<string> target,
        HashSet<string> selected,
        IEnumerable<string?> values,
        IReadOnlyList<string>? preferred)
    {
        var next = HardwareColumnFilter.Distinct(values, preferred);
        var choicesChanged = target.Count != next.Count;
        if (!choicesChanged)
        {
            for (var i = 0; i < next.Count; i++)
            {
                if (!string.Equals(target[i], next[i], StringComparison.Ordinal))
                {
                    choicesChanged = true;
                    break;
                }
            }
        }

        if (choicesChanged)
        {
            target.Clear();
            foreach (var item in next)
                target.Add(item);
        }

        var allowed = new HashSet<string>(target, StringComparer.OrdinalIgnoreCase);
        var removed = selected.RemoveWhere(value => !allowed.Contains(value));
        return choicesChanged || removed > 0;
    }

    private static string[] Ordered(HashSet<string> values) =>
        values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();

    public string SearchTipsTitle => HardwareTextFilter.SearchTipsTitle;
    public string SearchTipsBody => HardwareTextFilter.SearchTipsBody;

    [RelayCommand]
    private void ShowSearchTips() => SearchTipsOpen = true;

    [RelayCommand]
    private void CloseSearchTips() => SearchTipsOpen = false;

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

    internal string SysId { get; set; } = "";

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
