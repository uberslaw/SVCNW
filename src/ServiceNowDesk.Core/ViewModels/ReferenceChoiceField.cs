using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

/// <summary>
/// A dropdown of predownloaded reference rows. The visible text is the name. The stored value is the sys_id.
/// </summary>
public sealed class ReferenceChoiceField : ObservableObject
{
    private readonly Func<IServiceNowClient, CancellationToken, Task<IReadOnlyList<Choice>>> _load;
    private readonly bool _searchRemote;
    private IServiceNowClient? _client;
    private int _applyDepth;
    private int _filterVersion;
    private string _id = "";
    private string _filter = "";
    private List<Choice> _all = [];

    public ReferenceChoiceField(Func<IServiceNowClient, CancellationToken, Task<IReadOnlyList<Choice>>> load, bool searchRemote = false)
    {
        _load = load;
        _searchRemote = searchRemote;
        Choices.Add(new Choice("", "None"));
    }

    public ObservableCollection<Choice> Choices { get; } = [];

    public string Id
    {
        get => _id;
        set => AssignId(value);
    }

    public string Filter
    {
        get => _filter;
        set
        {
            var next = value ?? "";
            if (string.Equals(_filter, next, StringComparison.Ordinal))
                return;
            _filter = next;
            OnPropertyChanged(nameof(Filter));
            WhenReady = ApplyFilterAsync();
        }
    }

    /// <summary>
    /// The name shown for the current selection. Empty when nothing is chosen. Never the sys_id.
    /// </summary>
    public string SelectedLabel => VisibleLabel(_id);

    public Task WhenReady { get; private set; } = Task.CompletedTask;

    public event EventHandler? Changed;

    public void Use(IServiceNowClient? client) => _client = client;

    public async Task LoadAsync()
    {
        if (_client is null)
            return;

        IReadOnlyList<Choice> rows;
        try
        {
            rows = await _load(_client, CancellationToken.None);
        }
        catch
        {
            rows = [];
        }

        var selected = _id;
        var label = VisibleLabel(selected);
        _all = rows.Where(choice => !string.IsNullOrEmpty(choice.Value)).ToList();
        Remember(selected, label);
        WriteChoices(_filter.Trim());
    }

    public void Show(string? id, string? label)
    {
        BeginApply();
        try
        {
            var value = id ?? "";
            Remember(value, label ?? "");
            WriteChoices(_filter.Trim());
            AssignId(value);
        }
        finally
        {
            EndApply();
        }
    }

    public void Clear()
    {
        _filterVersion++;
        BeginApply();
        try
        {
            _all = [];
            _filter = "";
            WriteChoices("");
            AssignId("");
            OnPropertyChanged(nameof(Filter));
        }
        finally
        {
            EndApply();
        }
    }

    private async Task ApplyFilterAsync()
    {
        var version = ++_filterVersion;
        var filter = _filter.Trim();
        if (_searchRemote && _client is not null && filter.Length >= 2)
        {
            try
            {
                var hits = await _client.SearchConfigurationItemsAsync(filter, CancellationToken.None);
                if (version != _filterVersion)
                    return;
                foreach (var hit in hits)
                {
                    var name = hit.Display.Trim();
                    if (hit.SysId.Length == 0 || name.Length == 0 || name.Equals(hit.SysId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    Remember(hit.SysId, name);
                }
            }
            catch
            {
                if (version != _filterVersion)
                    return;
            }
        }

        if (version != _filterVersion)
            return;
        WriteChoices(filter);
    }

    private void WriteChoices(string filter)
    {
        var selected = _id;
        var selectedLabel = VisibleLabel(selected);
        BeginApply();
        try
        {
            Choices.Clear();
            Choices.Add(new Choice("", "None"));
            foreach (var choice in _all.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(choice.Value))
                    continue;
                var matches = filter.Length == 0 || choice.Label.Contains(filter, StringComparison.OrdinalIgnoreCase);
                var keepSelected = choice.Value.Equals(selected, StringComparison.OrdinalIgnoreCase);
                if (!matches && !keepSelected)
                    continue;
                if (Choices.Any(existing => existing.Value.Equals(choice.Value, StringComparison.OrdinalIgnoreCase)))
                    continue;
                Choices.Add(choice);
            }

            Remember(selected, selectedLabel);
            if (!string.IsNullOrEmpty(selected) && Choices.All(choice => !choice.Value.Equals(selected, StringComparison.OrdinalIgnoreCase)))
            {
                var name = string.IsNullOrWhiteSpace(selectedLabel) || selectedLabel.Equals(selected, StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : selectedLabel;
                Choices.Add(new Choice(selected, name));
            }

            AssignId(selected);
            OnPropertyChanged(nameof(Id));
            OnPropertyChanged(nameof(SelectedLabel));
        }
        finally
        {
            EndApply();
        }
    }

    private void Remember(string id, string label)
    {
        if (string.IsNullOrEmpty(id))
            return;
        var name = string.IsNullOrWhiteSpace(label) || label.Equals(id, StringComparison.OrdinalIgnoreCase) ? "" : label.Trim();
        var existing = _all.FirstOrDefault(choice => choice.Value.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            if (name.Length > 0)
                _all.Add(new Choice(id, name));
            return;
        }

        if (name.Length == 0 || existing.Label.Equals(name, StringComparison.Ordinal))
            return;
        _all.Remove(existing);
        _all.Add(new Choice(id, name));
    }

    private string VisibleLabel(string id)
    {
        if (string.IsNullOrEmpty(id))
            return "";
        var match = _all.FirstOrDefault(choice => choice.Value.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? Choices.FirstOrDefault(choice => choice.Value.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return "";
        var label = match.ToString() ?? "";
        return label.Equals(id, StringComparison.OrdinalIgnoreCase) ? "" : label;
    }

    private void AssignId(string? value)
    {
        var next = value ?? "";
        if (string.Equals(_id, next, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(SelectedLabel));
            return;
        }

        _id = next;
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(SelectedLabel));
        if (_applyDepth == 0)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void BeginApply() => _applyDepth++;

    private void EndApply() => _applyDepth = Math.Max(0, _applyDepth - 1);
}
