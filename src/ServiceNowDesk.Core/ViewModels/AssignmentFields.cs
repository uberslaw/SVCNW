using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

public sealed partial class AssignmentFields : ObservableObject
{
    private IServiceNowClient? _client;
    private bool _applying;
    private int _version;

    public ObservableCollection<Choice> Groups { get; } = [];
    public ObservableCollection<Choice> Members { get; } = [new Choice("", "Unassigned")];

    [ObservableProperty] private string groupId = "";
    [ObservableProperty] private string memberId = "";

    public Task WhenReady { get; private set; } = Task.CompletedTask;

    public string MemberHint => GroupId.Length == 0
        ? "Choose a group to list its members."
        : Members.Count <= 1 ? "No members are saved for this group yet." : "";

    public event EventHandler? Changed;

    public void Use(IServiceNowClient? client) => _client = client;

    public async Task LoadGroupsAsync()
    {
        if (_client is null)
            return;

        IReadOnlyList<Choice> groups;
        try
        {
            groups = await _client.ListAssignmentGroupsAsync(CancellationToken.None);
        }
        catch
        {
            groups = [];
        }

        var selected = GroupId;
        var selectedLabel = Groups.FirstOrDefault(choice => choice.Value == selected)?.Label ?? selected;
        Groups.Clear();
        Groups.Add(new Choice("", "Unassigned"));
        foreach (var group in groups.OrderBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Value.Length == 0 || Groups.Any(choice => choice.Value == group.Value))
                continue;
            Groups.Add(group);
        }

        Ensure(Groups, selected, selectedLabel);
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
        _applying = true;
        GroupId = "";
        MemberId = "";
        Members.Clear();
        Members.Add(new Choice("", "Unassigned"));
        _applying = false;
        OnPropertyChanged(nameof(MemberHint));
    }

    public void Clear()
    {
        ClearSelection();
        Groups.Clear();
        Groups.Add(new Choice("", "Unassigned"));
    }

    partial void OnGroupIdChanged(string value)
    {
        OnPropertyChanged(nameof(MemberHint));
        if (_applying)
            return;
        Changed?.Invoke(this, EventArgs.Empty);
        _ = LoadMembersAsync(value, "", "", keepMissing: false);
    }

    partial void OnMemberIdChanged(string value)
    {
        if (_applying)
            return;
        Changed?.Invoke(this, EventArgs.Empty);
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
                if (member.Value.Length == 0 || Members.Any(choice => choice.Value == member.Value))
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
