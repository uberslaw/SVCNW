using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.ViewModels;

public partial class LeadsViewModel : ObservableObject
{
    private LeadBoard _board = LeadBoard.Empty;
    private bool _mute;
    private IReadOnlyList<WorkEffortPerson> _lockedTeam = [];

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

    public ObservableCollection<string> SavedNames { get; } = [];

    public ObservableCollection<string> LockedNames { get; } = [];

    public event EventHandler? TeamChanged;

    /// <summary>Raised when Save team should be written to the local settings file.</summary>
    public event EventHandler? TeamPersisted;

    public event EventHandler? WorkEffortRequested;

    [ObservableProperty] private LeadArea area = LeadArea.Team;
    [ObservableProperty] private string groupName = NotificationPreferences.DefaultGroupName;
    [ObservableProperty] private string rosterNote = "";
    [ObservableProperty] private bool teamSaved;
    [ObservableProperty] private bool editingTeam;
    [ObservableProperty] private bool teamLocked;
    [ObservableProperty] private string teamSavedNote = "";
    [ObservableProperty] private string lockedTeamNote = "";

    public bool ShowRoster => Area == LeadArea.Team;

    public bool ShowQueues => Area != LeadArea.WorkEffort;

    public bool ShowWorkEffort => Area == LeadArea.WorkEffort;

    public bool ShowCheckboxes => ShowRoster && !TeamLocked && (!TeamSaved || EditingTeam);

    public bool ShowSavedNames => ShowRoster && !TeamLocked && TeamSaved && !EditingTeam;

    public bool ShowSaveTeam => ShowCheckboxes;

    public bool ShowEditTeam => ShowSavedNames;

    public bool ShowLockedTeam => ShowRoster && TeamLocked;

    public IReadOnlyList<string> LockedMemberIds =>
        _lockedTeam.Select(person => person.SysId).ToArray();

    public string TeamPrompt => Area != LeadArea.Team || TeamLocked || (TeamSaved && !EditingTeam)
        ? ""
        : Members.Count == 0
            ? ""
            : Members.Any(member => member.IsSelected)
                ? ""
                : "Tick the people on your team. Their queues stay empty until you do.";

    public IReadOnlyList<string> SelectedMemberIds =>
        Members.Where(member => member.IsSelected).Select(member => member.SysId).ToArray();

    /// <summary>
    /// People ticked on My team. When the roster has not been drawn yet, the saved ticks are the team.
    /// An empty roster with no saved ticks is an undefined team.
    /// A locked team is the city roster, including when that roster is empty.
    /// </summary>
    public IReadOnlyList<WorkEffortPerson> DefinedTeam(IEnumerable<string>? savedIds = null)
    {
        if (TeamLocked)
            return WorkEffortTeam.Normalize(_lockedTeam);

        if (Members.Count > 0)
        {
            return WorkEffortTeam.Normalize(
                Members.Where(member => member.IsSelected)
                    .Select(member => new WorkEffortPerson(member.SysId, member.Name, "")));
        }

        if (savedIds is null)
            return [];
        return WorkEffortTeam.Normalize(savedIds.Select(id => new WorkEffortPerson(id ?? "", id ?? "", "")));
    }

    public void ApplyTeamState(bool saved, bool locked)
    {
        var wasLocked = TeamLocked;
        TeamLocked = locked;
        TeamSaved = saved;
        EditingTeam = false;
        if (locked)
        {
            if (!wasLocked)
            {
                _lockedTeam = [];
                LockedNames.Clear();
            }

            TeamSavedNote = "";
        }
        else
        {
            if (wasLocked)
            {
                _lockedTeam = [];
                LockedNames.Clear();
                LockedTeamNote = "";
                _mute = true;
                Members.Clear();
                _mute = false;
            }

            TeamSavedNote = saved ? "Team saved." : "";
        }

        RaiseTeamChrome();
    }

    public void UseLockedRoster(IReadOnlyList<LockedLeadPerson>? people, string? note)
    {
        TeamLocked = true;
        EditingTeam = false;
        TeamSavedNote = "";
        _lockedTeam = (people ?? [])
            .Where(person => person is not null && !string.IsNullOrWhiteSpace(person.SysId))
            .Select(person => new WorkEffortPerson(person.SysId.Trim(), (person.Name ?? "").Trim(), ""))
            .ToArray();
        _mute = true;
        Members.Clear();
        LockedNames.Clear();
        foreach (var person in _lockedTeam.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var name = person.DisplayName;
            Members.Add(new LeadMemberModel(person.SysId, name, true, OnMemberChanged));
            LockedNames.Add(name);
        }

        _mute = false;
        LockedTeamNote = note ?? "";
        RaiseTeamChrome();
    }

    [RelayCommand]
    public void SaveTeam()
    {
        if (TeamLocked)
            return;

        TeamSavedNote = "Team saved.";
        EditingTeam = false;
        TeamSaved = true;
        RaiseTeamChrome();
        TeamPersisted?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void EditTeam()
    {
        if (TeamLocked || !TeamSaved)
            return;

        EditingTeam = true;
        RaiseTeamChrome();
    }

    public void Show(LeadBoard board)
    {
        _board = board ?? LeadBoard.Empty;
        Board.Show(_board.For(Area));
    }

    public void SetGroupName(string? name) =>
        GroupName = string.IsNullOrWhiteSpace(name) ? NotificationPreferences.DefaultGroupName : name.Trim();

    public void SetRoster(IEnumerable<Choice> members, IReadOnlyCollection<string>? selectedIds)
    {
        if (TeamLocked)
            return;

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
        RaiseTeamChrome();
    }

    public void Clear()
    {
        _mute = true;
        Members.Clear();
        _mute = false;
        _lockedTeam = [];
        LockedNames.Clear();
        SavedNames.Clear();
        RosterNote = "";
        LockedTeamNote = "";
        Show(LeadBoard.Empty);
        WorkEffort.Clear();
        RaiseTeamChrome();
    }

    partial void OnAreaChanged(LeadArea value)
    {
        _ = value;
        RaiseTeamChrome();
        if (Area == LeadArea.WorkEffort)
            WorkEffortRequested?.Invoke(this, EventArgs.Empty);
        else
            Board.Show(_board.For(value));
    }

    partial void OnTeamSavedChanged(bool value)
    {
        _ = value;
        RaiseTeamChrome();
    }

    partial void OnEditingTeamChanged(bool value)
    {
        _ = value;
        RaiseTeamChrome();
    }

    partial void OnTeamLockedChanged(bool value)
    {
        _ = value;
        RaiseTeamChrome();
    }

    partial void OnTeamSavedNoteChanged(string value)
    {
        _ = value;
    }

    partial void OnLockedTeamNoteChanged(string value)
    {
        _ = value;
    }

    private void OnMemberChanged()
    {
        if (_mute || TeamLocked)
            return;
        RaiseTeamChrome();
        TeamChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseTeamChrome()
    {
        OnPropertyChanged(nameof(ShowRoster));
        OnPropertyChanged(nameof(ShowQueues));
        OnPropertyChanged(nameof(ShowWorkEffort));
        OnPropertyChanged(nameof(ShowCheckboxes));
        OnPropertyChanged(nameof(ShowSavedNames));
        OnPropertyChanged(nameof(ShowSaveTeam));
        OnPropertyChanged(nameof(ShowEditTeam));
        OnPropertyChanged(nameof(ShowLockedTeam));
        OnPropertyChanged(nameof(TeamPrompt));
        RebuildSavedNames();
    }

    private void RebuildSavedNames()
    {
        if (!ShowSavedNames)
        {
            if (SavedNames.Count > 0)
                SavedNames.Clear();
            return;
        }

        var names = Members.Where(member => member.IsSelected).Select(member => member.Name).ToArray();
        if (names.SequenceEqual(SavedNames))
            return;
        SavedNames.Clear();
        foreach (var name in names)
            SavedNames.Add(name);
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
