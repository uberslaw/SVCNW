using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Query;

namespace ServiceNowDesk.ViewModels;

/// <summary>
/// One list of open incidents, request items, and walk-up interactions.
/// Each table is queried on its own with the same assignment and office clause, at most
/// <see cref="PageLimit"/> rows, so the page does not load every ticket in the instance.
/// </summary>
public partial class MixWorkspaceViewModel : ObservableObject
{
    public const int PageLimit = 50;

    private IServiceNowClient? _client;
    private bool _limitOffices;
    private IReadOnlyList<string> _officeCities = [];
    private bool _useLeadTeam;
    private IReadOnlyList<string> _teamMemberIds = [];
    private int _loadVersion;
    private bool _suppressSelection;
    private TicketRow? _bound;

    public MixWorkspaceViewModel()
    {
        Preset = Presets.Single(preset => preset.Assignment == AssignmentScope.Mine);
    }

    /// <summary>
    /// Optional host hook: when it returns true, <see cref="RefreshAsync"/> skips the network.
    /// Used after splash preload so My Tickets can paint from the desk list caches.
    /// </summary>
    public Func<Task<bool>>? TryLoadFromHostCacheAsync { get; set; }

    public IReadOnlyList<PresetOption> Presets { get; } = PresetCatalog.Mix;
    public ObservableCollection<TicketRow> Items { get; } = [];
    public Action<TicketRow>? PrepareRow { get; set; }
    public IReadOnlyList<string> OfficeCities => _officeCities;

    public IReadOnlyList<string> TeamMemberIds => _teamMemberIds;

    [ObservableProperty] private TicketRow? selected;
    [ObservableProperty] private PresetOption preset;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private RecordWorkspaceViewModel? editor;
    [ObservableProperty] private string editorKey = "";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool hasLoaded;
    [ObservableProperty] private int totalCount;
    [ObservableProperty] private string errorMessage = "";

    public bool HasMixEditor => Editor is not null || EditorKey.Length > 0;

    public bool ShowIncidentEditor => EditorKey == nameof(DeskSection.Incidents);

    public bool ShowRequestedItemEditor => EditorKey == nameof(DeskSection.RequestedItems);

    public bool ShowWalkUpEditor => EditorKey == nameof(DeskSection.WalkUps);

    public int OpenEditorCount =>
        (ShowIncidentEditor ? 1 : 0) + (ShowRequestedItemEditor ? 1 : 0) + (ShowWalkUpEditor ? 1 : 0);

    public bool ShowEmptyPrompt => OpenEditorCount == 0;

    public string EmptyPrompt => ShowEmptyPrompt ? "Select a ticket." : "";

    public event EventHandler<TicketRow>? OpenRequested;

    public void UseOfficeCities(IReadOnlyList<string>? cities)
    {
        _limitOffices = true;
        _officeCities = cities ?? [];
    }

    /// <summary>
    /// Walk-up My Team in the mix uses the same Leads people as the Walk-ups page.
    /// Incidents and request items keep the assignment-group My Team query.
    /// </summary>
    public void UseTeamMembers(IReadOnlyList<string>? memberIds)
    {
        _useLeadTeam = true;
        _teamMemberIds = (memberIds ?? [])
            .Select(id => id?.Trim() ?? "")
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Attach(IServiceNowClient client) => _client = client;

    public void Detach()
    {
        _client = null;
        _suppressSelection = true;
        Items.Clear();
        Selected = null;
        _bound = null;
        ClearEditor();
        HasLoaded = false;
        TotalCount = 0;
        ErrorMessage = "";
        _suppressSelection = false;
    }

    public void ShowEditor(RecordWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Editor = workspace;
        EditorKey = workspace.Section.ToString();
    }

    public void ClearEditor()
    {
        Editor = null;
        EditorKey = "";
    }

    public void RememberOpened(TicketRow row) => _bound = row;

    public void RevertSelection()
    {
        _suppressSelection = true;
        Selected = _bound;
        _suppressSelection = false;
    }

    [RelayCommand]
    private async Task ApplyPreset(PresetOption? option)
    {
        if (option is null)
            return;
        if (option != Preset)
            Preset = option;
        await RefreshAsync().ConfigureAwait(true);
    }

    public void ShowCachedRows(IReadOnlyList<TicketRow> rows)
    {
        _suppressSelection = true;
        Items.Clear();
        foreach (var row in rows)
        {
            PrepareRow?.Invoke(row);
            Items.Add(row);
        }

        TotalCount = rows.Count;
        Selected = null;
        _bound = null;
        ErrorMessage = "";
        HasLoaded = true;
        IsLoading = false;
        _suppressSelection = false;
    }

    public async Task RefreshAsync()
    {
        if (_client is null)
            return;

        if (TryLoadFromHostCacheAsync is not null && await TryLoadFromHostCacheAsync().ConfigureAwait(true))
            return;

        var version = ++_loadVersion;
        try
        {
            IsLoading = true;
            ErrorMessage = "";
            var query = BuildQuery();
            var walkQuery = BuildWalkUpQuery();
            var errors = new List<string>();
            var incidents = await LoadTableAsync(version, "Incidents", errors, async () =>
            {
                var page = await _client.SearchIncidentsAsync(query, CancellationToken.None).ConfigureAwait(true);
                return page.Items.Select(record => Tag(TicketRow.FromIncident(record), "INC", DeskSection.Incidents)).ToArray();
            }).ConfigureAwait(true);
            var items = await LoadTableAsync(version, "Request items", errors, async () =>
            {
                var page = await _client.SearchRequestedItemsAsync(query, CancellationToken.None).ConfigureAwait(true);
                return page.Items.Select(record => Tag(TicketRow.FromItem(record), "RITM", DeskSection.RequestedItems)).ToArray();
            }).ConfigureAwait(true);
            TicketRow[] walks;
            if (_useLeadTeam
                && Preset.Assignment == AssignmentScope.MyGroups
                && _teamMemberIds.Count == 0)
            {
                walks = [];
                errors.Add("Walk-ups: " + WalkUpTeam.EmptyPrompt);
            }
            else
            {
                walks = await LoadTableAsync(version, "Walk-ups", errors, async () =>
                {
                    var page = await _client.SearchInteractionsAsync(walkQuery, CancellationToken.None).ConfigureAwait(true);
                    return page.Items.Select(record => Tag(TicketRow.FromInteraction(record), "IMS", DeskSection.WalkUps)).ToArray();
                }).ConfigureAwait(true);
            }
            if (version != _loadVersion)
                return;

            var rows = incidents
                .Concat(items)
                .Concat(walks)
                .OrderByDescending(row => row.SortKey, StringComparer.Ordinal)
                .ThenBy(row => row.Number, StringComparer.Ordinal)
                .ToArray();

            var keepId = _bound?.SysId;
            var keepSource = _bound?.Source;
            _suppressSelection = true;
            Items.Clear();
            foreach (var row in rows)
            {
                PrepareRow?.Invoke(row);
                Items.Add(row);
            }

            TotalCount = rows.Length;
            Selected = Items.FirstOrDefault(row => row.SysId == keepId && row.Source == keepSource);
            _bound = Selected;
            _suppressSelection = false;
            ErrorMessage = string.Join(Environment.NewLine, errors);
            HasLoaded = errors.Count == 0;
        }
        catch (Exception ex)
        {
            if (version == _loadVersion)
                ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            if (version == _loadVersion)
                IsLoading = false;
        }
    }

    private async Task<TicketRow[]> LoadTableAsync(
        int version,
        string label,
        List<string> errors,
        Func<Task<TicketRow[]>> load)
    {
        try
        {
            return await load().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _loadVersion)
                errors.Add(label + ": " + WorkspaceMessages.Describe(ex));
            return [];
        }
    }

    private TicketQuery BuildQuery()
    {
        var trimmed = SearchText.Trim();
        string? text = null;
        if (trimmed.Length >= 2 || EncodedQuery.IsNumberQuery(trimmed))
            text = trimmed;

        IReadOnlyList<string>? offices = null;
        // My Tickets uses the same watched-office list as My Team / Unassigned.
        if (_limitOffices && Preset.Assignment is AssignmentScope.Mine or AssignmentScope.MyGroups or AssignmentScope.Unassigned)
            offices = _officeCities;

        return new TicketQuery
        {
            Text = text,
            Assignment = Preset.Assignment,
            Activity = Preset.Activity,
            OfficeLocations = offices,
            Limit = PageLimit
        };
    }

    /// <summary>
    /// Walk-up My Team matches the Walk-ups page: selected Leads people, no office limit.
    /// Unassigned stays on the office queue like the other tables.
    /// </summary>
    private TicketQuery BuildWalkUpQuery()
    {
        var baseQuery = BuildQuery();
        if (!_useLeadTeam || Preset.Assignment != AssignmentScope.MyGroups)
            return baseQuery;

        return baseQuery with
        {
            TeamMemberIds = _teamMemberIds,
            OfficeLocations = null
        };
    }

    private static TicketRow Tag(TicketRow row, string kind, DeskSection source) => new()
    {
        SysId = row.SysId,
        Number = row.Number,
        Title = row.Title,
        StateLabel = row.StateLabel,
        Tone = row.Tone,
        Meta = row.Meta,
        When = row.When,
        Badge = row.Badge,
        Unassigned = row.Unassigned,
        StateValue = row.StateValue,
        SortKey = row.SortKey,
        Kind = kind,
        Source = source
    };

    partial void OnEditorChanged(RecordWorkspaceViewModel? value)
    {
        OnPropertyChanged(nameof(HasMixEditor));
        OnPropertyChanged(nameof(ShowEmptyPrompt));
        OnPropertyChanged(nameof(EmptyPrompt));
        OnPropertyChanged(nameof(OpenEditorCount));
    }

    partial void OnEditorKeyChanged(string value)
    {
        OnPropertyChanged(nameof(HasMixEditor));
        OnPropertyChanged(nameof(ShowIncidentEditor));
        OnPropertyChanged(nameof(ShowRequestedItemEditor));
        OnPropertyChanged(nameof(ShowWalkUpEditor));
        OnPropertyChanged(nameof(OpenEditorCount));
        OnPropertyChanged(nameof(ShowEmptyPrompt));
        OnPropertyChanged(nameof(EmptyPrompt));
    }

    partial void OnSelectedChanged(TicketRow? value)
    {
        if (_suppressSelection || value is null)
            return;
        if (_bound is not null && _bound.SysId == value.SysId && _bound.Source == value.Source)
            return;
        OpenRequested?.Invoke(this, value);
    }
}
