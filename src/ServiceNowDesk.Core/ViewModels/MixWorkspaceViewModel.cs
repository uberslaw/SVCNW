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
    private int _loadVersion;
    private bool _suppressSelection;
    private TicketRow? _bound;

    public MixWorkspaceViewModel()
    {
        Preset = Presets.Single(preset => preset.Assignment == AssignmentScope.MyGroups);
    }

    public IReadOnlyList<PresetOption> Presets { get; } = PresetCatalog.Mix;
    public ObservableCollection<TicketRow> Items { get; } = [];
    public Action<TicketRow>? PrepareRow { get; set; }
    public RecordWorkspaceViewModel? Editor { get; private set; }
    public IReadOnlyList<string> OfficeCities => _officeCities;

    [ObservableProperty] private TicketRow? selected;
    [ObservableProperty] private PresetOption preset;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string editorKey = "";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool hasLoaded;
    [ObservableProperty] private int totalCount;
    [ObservableProperty] private string errorMessage = "";

    public bool HasMixEditor => EditorKey.Length > 0;

    public event EventHandler<TicketRow>? OpenRequested;

    public void UseOfficeCities(IReadOnlyList<string>? cities)
    {
        _limitOffices = true;
        _officeCities = cities ?? [];
    }

    public void Attach(IServiceNowClient client) => _client = client;

    public void Detach()
    {
        _client = null;
        _suppressSelection = true;
        Items.Clear();
        Selected = null;
        _bound = null;
        Editor = null;
        EditorKey = "";
        HasLoaded = false;
        TotalCount = 0;
        ErrorMessage = "";
        _suppressSelection = false;
    }

    public void ShowEditor(RecordWorkspaceViewModel workspace)
    {
        Editor = workspace;
        EditorKey = workspace.Section.ToString();
    }

    public void RememberOpened(TicketRow row) => _bound = row;

    public void RevertSelection()
    {
        _suppressSelection = true;
        Selected = _bound;
        _suppressSelection = false;
    }

    [RelayCommand]
    private void ApplyPreset(PresetOption? option)
    {
        if (option is null || option == Preset)
            return;
        Preset = option;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_client is null)
            return;

        var version = ++_loadVersion;
        try
        {
            IsLoading = true;
            ErrorMessage = "";
            var query = BuildQuery();
            var incidents = await _client.SearchIncidentsAsync(query, CancellationToken.None).ConfigureAwait(true);
            var items = await _client.SearchRequestedItemsAsync(query, CancellationToken.None).ConfigureAwait(true);
            var walks = await _client.SearchInteractionsAsync(query, CancellationToken.None).ConfigureAwait(true);
            if (version != _loadVersion)
                return;

            var rows = incidents.Items.Select(record => Tag(TicketRow.FromIncident(record), "INC", DeskSection.Incidents))
                .Concat(items.Items.Select(record => Tag(TicketRow.FromItem(record), "RITM", DeskSection.RequestedItems)))
                .Concat(walks.Items.Select(record => Tag(TicketRow.FromInteraction(record), "IMS", DeskSection.WalkUps)))
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
            HasLoaded = true;
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

    private TicketQuery BuildQuery()
    {
        var trimmed = SearchText.Trim();
        string? text = null;
        if (trimmed.Length >= 2 || EncodedQuery.IsNumberQuery(trimmed))
            text = trimmed;

        IReadOnlyList<string>? offices = null;
        if (_limitOffices && Preset.Assignment is AssignmentScope.MyGroups or AssignmentScope.Unassigned)
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

    partial void OnEditorKeyChanged(string value) => OnPropertyChanged(nameof(HasMixEditor));

    partial void OnSelectedChanged(TicketRow? value)
    {
        if (_suppressSelection || value is null)
            return;
        if (_bound is not null && _bound.SysId == value.SysId && _bound.Source == value.Source)
            return;
        OpenRequested?.Invoke(this, value);
    }
}
