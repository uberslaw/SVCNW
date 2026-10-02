using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

public sealed class AssignmentFields : ObservableObject
{
    private IServiceNowClient? _client;
    private bool _applying;
    private int _version;
    private int _groupVersion;
    private string _groupId = "";
    private string _memberId = "";

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

    public string MemberHint => string.IsNullOrEmpty(GroupId)
        ? "Choose a group to list its members."
        : Members.Count <= 1 ? "No members are saved for this group yet." : "";

    public event EventHandler? Changed;

    public void Use(IServiceNowClient? client) => _client = client;

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
        var selectedLabel = Groups.FirstOrDefault(choice => choice.Value == selected)?.Label ?? selected;
        _applying = true;
        try
        {
            Groups.Clear();
            Groups.Add(new Choice("", "Unassigned"));
            foreach (var group in groups.OrderBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(group.Value) || Groups.Any(choice => choice.Value == group.Value))
                    continue;
                Groups.Add(group);
            }

            Ensure(Groups, selected, selectedLabel);
            GroupId = selected;
            MemberId = selectedMember;
            OnPropertyChanged(nameof(GroupId));
            OnPropertyChanged(nameof(MemberId));
        }
        finally
        {
            _applying = false;
            OnPropertyChanged(nameof(MemberHint));
        }
    }

    public async Task ShowAsync(string? groupId, string? groupLabel, string? memberId, string? memberLabel)
    {
        _applying = true;
        try
        {
            var group = groupId ?? "";
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
        ClearSelection();
        Groups.Clear();
        Groups.Add(new Choice("", "Unassigned"));
    }

    private void GroupChanged(string value)
    {
        OnPropertyChanged(nameof(MemberHint));
        if (_applying)
            return;
        Changed?.Invoke(this, EventArgs.Empty);
        _ = LoadMembersAsync(value, "", "", keepMissing: false);
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

    private static void Ensure(ObservableCollection<Choice> target, string value, string label)
    {
        if (string.IsNullOrEmpty(value) || target.Any(choice => choice.Value == value))
            return;
        target.Add(new Choice(value, string.IsNullOrWhiteSpace(label) ? value : label));
    }
}
