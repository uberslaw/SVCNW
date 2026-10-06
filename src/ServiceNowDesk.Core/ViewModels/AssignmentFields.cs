using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public sealed class AssignmentFields : ObservableObject
{
    private readonly IRecentAssignmentGroupStore _recent;
    private IServiceNowClient? _client;
    private bool _applying;
    private int _version;
    private int _groupVersion;
    private string _groupId = "";
    private string _memberId = "";

    public AssignmentFields(IRecentAssignmentGroupStore? recentGroups = null)
    {
        _recent = recentGroups ?? new MemoryRecentAssignmentGroupStore();
        _recent.Changed += (_, _) =>
        {
            if (_applying || !GroupsLoaded)
                return;
            ReorderGroups();
        };
    }

    public ObservableCollection<Choice> Groups { get; } = [];
    public ObservableCollection<Choice> Members { get; } = [new Choice("", "Unassigned")];

    public string GroupId
    {
        get => _groupId;
        set => Assign(ref _groupId, value, nameof(GroupId), GroupChanged);
    }

    public string MemberId
    {
        get => _memberId;
        set => Assign(ref _memberId, value, nameof(MemberId), MemberChanged);
    }

    public Task WhenReady { get; private set; } = Task.CompletedTask;

    public bool GroupsLoaded { get; private set; }

    public string MemberHint => string.IsNullOrEmpty(GroupId)
        ? "Choose a group to list its members."
        : Members.Count <= 1 ? "No members are saved for this group yet." : "";

    public event EventHandler? Changed;

    public void Use(IServiceNowClient? client) => _client = client;

    public void RememberSelectedGroup()
    {
        if (_applying || string.IsNullOrEmpty(GroupId))
            return;

        var label = Groups.FirstOrDefault(choice => choice.Value.Equals(GroupId, StringComparison.OrdinalIgnoreCase))?.Label;
        _recent.Remember(GroupId, string.IsNullOrWhiteSpace(label) ? GroupId : label);
    }

    public async Task LoadGroupsAsync()
    {
        if (_client is null)
            return;

        var version = ++_groupVersion;
        IReadOnlyList<Choice> groups;
        try
        {
            groups = await _client.ListAssignmentGroupsAsync(CancellationToken.None);
        }
        catch
        {
            groups = [];
        }

        if (version != _groupVersion)
            return;

        var selected = GroupId;
        var selectedMember = MemberId;
        var selectedLabel = Groups.FirstOrDefault(choice => string.Equals(choice.Value, selected, StringComparison.OrdinalIgnoreCase))?.Label ?? selected;
        var memberLabel = Members.FirstOrDefault(choice => string.Equals(choice.Value, selectedMember, StringComparison.OrdinalIgnoreCase))?.Label ?? "";
        _applying = true;
        try
        {
            var available = groups.Where(choice => !string.IsNullOrEmpty(choice.Value)).ToList();
            if (!string.IsNullOrEmpty(selected) && available.All(choice => !choice.Value.Equals(selected, StringComparison.OrdinalIgnoreCase)))
                available.Add(new Choice(selected, string.IsNullOrWhiteSpace(selectedLabel) ? selected : selectedLabel));
            WriteGroups(available);
            GroupId = selected;
            MemberId = selectedMember;
            OnPropertyChanged(nameof(GroupId));
            OnPropertyChanged(nameof(MemberId));
            GroupsLoaded = true;
        }
        finally
        {
            _applying = false;
            OnPropertyChanged(nameof(MemberHint));
        }

        if (!string.IsNullOrEmpty(GroupId))
            await LoadMembersAsync(GroupId, MemberId, memberLabel, keepMissing: true);
    }

    public async Task ShowAsync(string? groupId, string? groupLabel, string? memberId, string? memberLabel)
    {
        _applying = true;
        try
        {
            var group = ResolveGroup(groupId ?? "");
            if (group.Length == 0)
                group = groupId ?? "";
            var member = memberId ?? "";
            Ensure(Groups, group, groupLabel ?? "");
            GroupId = group;
            MemberId = member;
            await LoadMembersAsync(group, member, memberLabel ?? "", keepMissing: true);
        }
        finally
        {
            _applying = false;
        }
    }

    public void ClearSelection()
    {
        _version++;
        _applying = true;
        try
        {
            GroupId = "";
            KeepBlankMember();
            MemberId = "";
            // Push the blank value again after the row is in the list. A combo writes null when its selection is missing.
            OnPropertyChanged(nameof(GroupId));
            OnPropertyChanged(nameof(MemberId));
        }
        finally
        {
            _applying = false;
            OnPropertyChanged(nameof(MemberHint));
        }
    }

    public void Clear()
    {
        GroupsLoaded = false;
        ClearSelection();
        Groups.Clear();
        Groups.Add(new Choice("", "Unassigned"));
    }

    private void GroupChanged(string value)
    {
        OnPropertyChanged(nameof(MemberHint));
        if (_applying)
            return;

        var resolved = ResolveGroup(value);
        if (!string.Equals(resolved, _groupId, StringComparison.Ordinal))
        {
            _groupId = resolved;
            OnPropertyChanged(nameof(GroupId));
        }

        RememberSelectedGroup();
        Changed?.Invoke(this, EventArgs.Empty);
        _ = LoadMembersAsync(resolved, "", "", keepMissing: false);
    }

    private void MemberChanged(string _)
    {
        if (_applying)
            return;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Assign(ref string field, string? value, string propertyName, Action<string> changed)
    {
        var next = value ?? "";
        if (string.Equals(field, next, StringComparison.Ordinal))
            return;
        field = next;
        changed(next);
        OnPropertyChanged(propertyName);
    }

    private void KeepBlankMember()
    {
        for (var index = Members.Count - 1; index >= 0; index--)
        {
            if (!string.IsNullOrEmpty(Members[index].Value))
                Members.RemoveAt(index);
        }

        if (!Members.Any(choice => string.IsNullOrEmpty(choice.Value)))
            Members.Insert(0, new Choice("", "Unassigned"));
    }

    private Task LoadMembersAsync(string groupId, string memberId, string memberLabel, bool keepMissing)
    {
        var version = ++_version;
        var task = LoadMembersCoreAsync(version, groupId, memberId, memberLabel, keepMissing);
        WhenReady = task;
        return task;
    }

    private async Task LoadMembersCoreAsync(int version, string groupId, string memberId, string memberLabel, bool keepMissing)
    {
        IReadOnlyList<Choice> members = [];
        if (_client is not null && !string.IsNullOrWhiteSpace(groupId))
        {
            try
            {
                members = await _client.ListGroupMembersAsync(groupId, CancellationToken.None);
            }
            catch
            {
                members = [];
            }
        }

        if (version != _version)
            return;

        _applying = true;
        try
        {
            Members.Clear();
            Members.Add(new Choice("", "Unassigned"));
            foreach (var member in members)
            {
                if (string.IsNullOrEmpty(member.Value) || Members.Any(choice => choice.Value == member.Value))
                    continue;
                Members.Add(member);
            }

            var keep = memberId;
            if (keep.Length > 0 && Members.All(choice => choice.Value != keep))
            {
                if (keepMissing)
                    Members.Add(new Choice(keep, string.IsNullOrWhiteSpace(memberLabel) ? keep : memberLabel));
                else
                    keep = "";
            }

            MemberId = keep;
            OnPropertyChanged(nameof(MemberId));
            OnPropertyChanged(nameof(MemberHint));
        }
        finally
        {
            _applying = false;
        }
    }

    private void ReorderGroups()
    {
        var selected = GroupId;
        var selectedMember = MemberId;
        var selectedLabel = Groups.FirstOrDefault(choice => choice.Value.Equals(selected, StringComparison.OrdinalIgnoreCase))?.Label ?? selected;
        var available = Groups.Where(choice => !string.IsNullOrEmpty(choice.Value)).ToList();
        if (!string.IsNullOrEmpty(selected) && available.All(choice => !choice.Value.Equals(selected, StringComparison.OrdinalIgnoreCase)))
            available.Add(new Choice(selected, string.IsNullOrWhiteSpace(selectedLabel) ? selected : selectedLabel));

        _applying = true;
        try
        {
            WriteGroups(available);
            GroupId = selected;
            MemberId = selectedMember;
            OnPropertyChanged(nameof(GroupId));
            OnPropertyChanged(nameof(MemberId));
        }
        finally
        {
            _applying = false;
        }
    }

    private void WriteGroups(IReadOnlyList<Choice> groups)
    {
        var available = new List<Choice>();
        foreach (var group in groups)
        {
            if (string.IsNullOrEmpty(group.Value) || available.Any(choice => choice.Value.Equals(group.Value, StringComparison.OrdinalIgnoreCase)))
                continue;
            available.Add(group);
        }

        var pinned = new List<Choice>();
        foreach (var saved in _recent.Load())
        {
            var match = available.FirstOrDefault(choice => choice.Value.Equals(saved.Value, StringComparison.OrdinalIgnoreCase));
            if (match is null || pinned.Count == RecentAssignmentGroups.Limit)
                continue;
            if (pinned.Any(choice => choice.Value.Equals(match.Value, StringComparison.OrdinalIgnoreCase)))
                continue;
            pinned.Add(match);
        }

        pinned.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));
        var pinnedIds = new HashSet<string>(pinned.Select(choice => choice.Value), StringComparer.OrdinalIgnoreCase);
        var rest = available
            .Where(choice => !pinnedIds.Contains(choice.Value))
            .OrderBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase);

        Groups.Clear();
        Groups.Add(new Choice("", "Unassigned"));
        foreach (var group in pinned)
            Groups.Add(group);
        foreach (var group in rest)
            Groups.Add(group);
    }

    private string ResolveGroup(string value)
    {
        var token = (value ?? "").Trim();
        if (token.Length == 0)
            return "";

        var byId = Groups.FirstOrDefault(choice =>
            !string.IsNullOrEmpty(choice.Value)
            && choice.Value.Equals(token, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
            return byId.Value;

        Choice? named = null;
        var matches = 0;
        foreach (var choice in Groups)
        {
            if (string.IsNullOrEmpty(choice.Value))
                continue;
            if (!choice.Label.Trim().Equals(token, StringComparison.OrdinalIgnoreCase))
                continue;
            named = choice;
            matches++;
        }

        return matches == 1 && named is not null ? named.Value : token;
    }

    private static void Ensure(ObservableCollection<Choice> target, string value, string label)
    {
        if (string.IsNullOrEmpty(value) || target.Any(choice => choice.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
            return;
        target.Add(new Choice(value, string.IsNullOrWhiteSpace(label) ? value : label));
    }
}
