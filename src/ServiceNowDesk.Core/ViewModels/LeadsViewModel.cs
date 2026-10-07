using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

public partial class LeadsViewModel : ObservableObject
{
    private LeadBoard _board = LeadBoard.Empty;
    private bool _mute;

    public LeadsViewModel()
    {
        Board = new NotificationWorkspaceViewModel(
            [
                AlertKind.SlaBreaching,
                AlertKind.OnHoldPastFollowUp,
                AlertKind.UpdatedByCaller,
                AlertKind.ReturnedWithNotes,
                AlertKind.Unattended
            ],
            alwaysShowAssignee: true);
        WorkEffort.ScaleChanged += (_, _) =>
        {
            if (Area == LeadArea.WorkEffort)
                WorkEffortRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public NotificationWorkspaceViewModel Board { get; }

    public WorkEffortViewModel WorkEffort { get; } = new();

    public ObservableCollection<LeadMemberModel> Members { get; } = [];

    public event EventHandler? TeamChanged;

    public event EventHandler? WorkEffortRequested;

    [ObservableProperty] private LeadArea area = LeadArea.Team;
    [ObservableProperty] private string groupName = NotificationPreferences.DefaultGroupName;
    [ObservableProperty] private string rosterNote = "";

    public bool ShowRoster => Area == LeadArea.Team;

    public bool ShowQueues => Area != LeadArea.WorkEffort;

    public bool ShowWorkEffort => Area == LeadArea.WorkEffort;

    public string TeamPrompt => Area != LeadArea.Team
        ? ""
        : Members.Count == 0
            ? ""
            : Members.Any(member => member.IsSelected)
                ? ""
                : "Tick the people on your team. Their queues stay empty until you do.";

    public IReadOnlyList<string> SelectedMemberIds =>
        Members.Where(member => member.IsSelected).Select(member => member.SysId).ToArray();

    public void Show(LeadBoard board)
    {
        _board = board ?? LeadBoard.Empty;
        Board.Show(_board.For(Area));
    }

    public void SetGroupName(string? name) =>
        GroupName = string.IsNullOrWhiteSpace(name) ? NotificationPreferences.DefaultGroupName : name.Trim();

    public void SetRoster(IEnumerable<Choice> members, IReadOnlyCollection<string>? selectedIds)
    {
        var selected = new HashSet<string>(
            (selectedIds ?? []).Select(id => id?.Trim() ?? "").Where(id => id.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        _mute = true;
        Members.Clear();
        foreach (var member in (members ?? []).OrderBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase))
        {
            var id = member.Value?.Trim() ?? "";
            if (id.Length == 0 || Members.Any(existing => existing.SysId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                continue;
            var name = string.IsNullOrWhiteSpace(member.Label) ? id : member.Label.Trim();
            Members.Add(new LeadMemberModel(id, name, selected.Contains(id), OnMemberChanged));
        }

        _mute = false;
        OnPropertyChanged(nameof(TeamPrompt));
    }

    public void Clear()
    {
        _mute = true;
        Members.Clear();
        _mute = false;
        RosterNote = "";
        Show(LeadBoard.Empty);
        WorkEffort.Clear();
        OnPropertyChanged(nameof(TeamPrompt));
    }

    partial void OnAreaChanged(LeadArea value)
    {
        OnPropertyChanged(nameof(ShowRoster));
        OnPropertyChanged(nameof(ShowQueues));
        OnPropertyChanged(nameof(ShowWorkEffort));
        OnPropertyChanged(nameof(TeamPrompt));
        if (value == LeadArea.WorkEffort)
            WorkEffortRequested?.Invoke(this, EventArgs.Empty);
        else
            Board.Show(_board.For(value));
    }

    private void OnMemberChanged()
    {
        if (_mute)
            return;
        OnPropertyChanged(nameof(TeamPrompt));
        TeamChanged?.Invoke(this, EventArgs.Empty);
    }
}

public partial class LeadMemberModel : ObservableObject
{
    private readonly Action _changed;

    public LeadMemberModel(string sysId, string name, bool selected, Action changed)
    {
        _changed = changed;
        SysId = sysId;
        Name = name;
        IsSelected = selected;
    }

    public string SysId { get; }

    public string Name { get; }

    [ObservableProperty] private bool isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        _ = value;
        _changed();
    }
}
