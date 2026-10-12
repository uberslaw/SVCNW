using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Client;

namespace ServiceNowDesk.ViewModels;

/// <summary>
/// Settings tree for "my region". The All offices tick on a region or portion checks every office under it.
/// Open and closed sections stay on this screen for the session. Only office labels are stored.
/// </summary>
public sealed partial class RegionSettingsViewModel : ObservableObject
{
    private bool _updating;

    public RegionSettingsViewModel()
    {
        _updating = true;
        foreach (var region in RegionOfficeCatalog.Regions)
            Groups.Add(Build(region));
        RefreshGroups();
        _updating = false;
    }

    public ObservableCollection<RegionOfficeNode> Groups { get; } = [];

    public event EventHandler? Changed;

    public IReadOnlyList<string> SelectedOffices =>
        EnumerateOffices().Where(office => office.IsChecked == true).Select(office => office.Label).ToArray();

    public void Load(IEnumerable<string>? offices)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (offices is not null)
        {
            foreach (var office in offices)
            {
                var name = (office ?? "").Trim();
                if (name.Length > 0)
                    selected.Add(name);
            }
        }

        _updating = true;
        try
        {
            foreach (var office in EnumerateOffices())
                office.IsChecked = selected.Contains(office.Label);
            RefreshGroups();
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>
    /// Checks catalog offices that belong to <paramref name="saved"/>, including a trailing "Office".
    /// York does not check New York.
    /// </summary>
    public void LoadMatched(IEnumerable<string>? saved)
    {
        var names = RegionOfficeMatch.NormalizeOffices(saved);
        _updating = true;
        try
        {
            foreach (var office in EnumerateOffices())
            {
                office.IsChecked = names.Any(name =>
                    string.Equals(office.Label, name, StringComparison.OrdinalIgnoreCase)
                    || RegionOfficeMatch.LocationMatchesOffice(office.Label, name)
                    || RegionOfficeMatch.LocationMatchesOffice(name, office.Label));
            }

            RefreshGroups();
        }
        finally
        {
            _updating = false;
        }
    }

    public RegionOfficeNode? Find(string label)
    {
        foreach (var node in EnumerateAll())
        {
            if (string.Equals(node.Label, label, StringComparison.OrdinalIgnoreCase))
                return node;
        }

        return null;
    }

    public void SetChecked(string label, bool value)
    {
        var node = Find(label) ?? throw new ArgumentException($"Unknown region office '{label}'.", nameof(label));
        if (node.IsOffice)
        {
            node.IsChecked = value;
            return;
        }

        var changed = false;
        _updating = true;
        try
        {
            foreach (var office in node.Offices())
            {
                if (office.IsChecked == value)
                    continue;
                office.IsChecked = value;
                changed = true;
            }

            if (changed)
                RefreshGroups();
        }
        finally
        {
            _updating = false;
        }

        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void OnOfficeEdited()
    {
        if (_updating)
            return;

        _updating = true;
        try
        {
            RefreshGroups();
        }
        finally
        {
            _updating = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private RegionOfficeNode Build(RegionOfficeGroup group)
    {
        var node = new RegionOfficeNode(this, group.Label, isOffice: false);
        foreach (var office in group.Offices)
            node.Children.Add(new RegionOfficeNode(this, office, isOffice: true));
        foreach (var child in group.Groups)
            node.Children.Add(Build(child));
        return node;
    }

    private void RefreshGroups()
    {
        foreach (var group in EnumerateAll().Where(node => !node.IsOffice))
        {
            var offices = group.Offices().ToArray();
            var on = offices.Count(office => office.IsChecked == true);
            bool? state = false;
            if (offices.Length > 0 && on > 0)
                state = on == offices.Length ? true : null;

            if (group.IsChecked != state)
                group.IsChecked = state;
            group.SetSelectedOfficeCount(on);
        }
    }

    private IEnumerable<RegionOfficeNode> EnumerateAll()
    {
        foreach (var group in Groups)
        {
            yield return group;
            foreach (var node in group.Descendants())
                yield return node;
        }
    }

    private IEnumerable<RegionOfficeNode> EnumerateOffices() =>
        EnumerateAll().Where(node => node.IsOffice);
}

public partial class RegionOfficeNode : ObservableObject
{
    private readonly RegionSettingsViewModel _owner;

    public RegionOfficeNode(RegionSettingsViewModel owner, string label, bool isOffice)
    {
        _owner = owner;
        Label = label;
        IsOffice = isOffice;
        if (isOffice)
            IsChecked = false;
    }

    public string Label { get; }

    public bool IsOffice { get; }

    public ObservableCollection<RegionOfficeNode> Children { get; } = [];

    public bool HasChildren => Children.Count > 0;

    public bool ChildrenAreOffices => Children.Count > 0 && Children.All(child => child.IsOffice);

    public IEnumerable<RegionOfficeNode> OfficeChildren => Children.Where(child => child.IsOffice);

    public IEnumerable<RegionOfficeNode> PortionChildren => Children.Where(child => !child.IsOffice);

    public bool HasOfficeChildren => OfficeChildren.Any();

    public bool HasPortions => PortionChildren.Any();

    /// <summary>Session-only. Settings reload does not open or close a section.</summary>
    [ObservableProperty] private bool isExpanded;

    public int SelectedOfficeCount { get; private set; }

    public string SelectionSummary =>
        SelectedOfficeCount == 0 ? "" : $"{SelectedOfficeCount} selected";

    [ObservableProperty] private bool? isChecked;

    internal void SetSelectedOfficeCount(int count)
    {
        if (SelectedOfficeCount == count)
            return;
        SelectedOfficeCount = count;
        OnPropertyChanged(nameof(SelectedOfficeCount));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    public IEnumerable<RegionOfficeNode> Offices()
    {
        if (IsOffice)
        {
            yield return this;
            yield break;
        }

        foreach (var child in Children)
        {
            foreach (var office in child.Offices())
                yield return office;
        }
    }

    public IEnumerable<RegionOfficeNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var nested in child.Descendants())
                yield return nested;
        }
    }

    public void ToggleFromUser()
    {
        if (IsOffice)
            return;
        _owner.SetChecked(Label, IsChecked != true);
    }

    partial void OnIsCheckedChanged(bool? value)
    {
        if (!IsOffice)
            return;
        _ = value;
        _owner.OnOfficeEdited();
    }
}
