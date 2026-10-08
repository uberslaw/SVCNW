using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

/// <summary>
/// A dropdown of predownloaded reference rows. The visible text is the name. The stored value is the sys_id.
/// The combo only ever holds one page of matches, so opening it does not build a row for every cached name.
/// </summary>
public sealed class ReferenceChoiceField : ObservableObject
{
    public static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(150);

    private readonly Func<IServiceNowClient, CancellationToken, Task<IReadOnlyList<Choice>>> _load;
    private readonly bool _searchRemote;
    private readonly object _gate = new();
    private IServiceNowClient? _client;
    private CancellationTokenSource? _filterDelay;
    private int _applyDepth;
    private int _filterVersion;
    private int _matchCount;
    private int _commitGate;
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

    public int MatchCount => _matchCount;

    /// <summary>
    /// Shown under the search box when the page is shorter than the match list.
    /// </summary>
    public string MatchSummary
    {
        get
        {
            var shown = ShownCount();
            if (_matchCount > shown)
                return shown.ToString(CultureInfo.InvariantCulture) + " of " + _matchCount.ToString(CultureInfo.InvariantCulture);
            if (_filter.Trim().Length == 0)
                return "";
            if (_matchCount == 0)
                return "No matches";
            return _matchCount == 1
                ? "1 match"
                : _matchCount.ToString(CultureInfo.InvariantCulture) + " matches";
        }
    }

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
            OnPropertyChanged(nameof(MatchSummary));
            var version = NextVersion();
            _filterDelay?.Cancel();
            var cts = new CancellationTokenSource();
            _filterDelay = cts;
            WhenReady = PublishFilterAsync(version, next, cts.Token);
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

        var load = _load(_client, CancellationToken.None);
        IReadOnlyList<Choice> rows;
        try
        {
            rows = load.IsCompletedSuccessfully ? load.Result : await load.ConfigureAwait(true);
        }
        catch
        {
            rows = [];
        }

        var selected = _id;
        var label = VisibleLabel(selected);
        List<Choice> all;
        if (rows.Count > ReferenceNameMatcher.PageSize)
            all = await Task.Run(() => Prepare(rows, selected, label)).ConfigureAwait(true);
        else
            all = Prepare(rows, selected, label);

        // Keep names found by CommitAsync remote search; a catalog reload must not drop them.
        KeepSearchHits(all, _all);
        _all = all;
        if (all.Count > ReferenceNameMatcher.PageSize)
            await PublishOffThreadAsync().ConfigureAwait(true);
        else
            PublishNow();
    }

    public void Show(string? id, string? label)
    {
        BeginApply();
        try
        {
            var value = id ?? "";
            Remember(value, label ?? "");
            PublishNow();
            AssignId(value);
        }
        finally
        {
            EndApply();
        }
    }

    public void Clear()
    {
        BeginApply();
        try
        {
            _all = [];
            _filter = "";
            OnPropertyChanged(nameof(Filter));
            PublishNow();
            AssignId("");
        }
        finally
        {
            EndApply();
        }
    }

    /// <summary>
    /// Resolves the typed text from the cache. A name that is not in the cache is looked up once.
    /// Typing never calls ServiceNow.
    /// </summary>
    public async Task CommitAsync()
    {
        if (Interlocked.Exchange(ref _commitGate, 1) == 1)
            return;

        try
        {
            await CommitCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Exchange(ref _commitGate, 0);
        }
    }

    private async Task CommitCoreAsync()
    {
        var typed = _filter.Trim();
        if (typed.Length == 0)
            return;

        var exact = FindExact(typed);
        if (exact is not null)
        {
            Id = exact.Value;
            return;
        }

        if (CacheHasMatch(typed))
            return;

        if (!_searchRemote || _client is null)
            return;
        if (typed.Length < 2 || typed.Contains('*') || typed.Contains('?') || typed.Contains('+'))
            return;

        IReadOnlyList<ReferenceSuggestion> hits;
        try
        {
            hits = await _client.SearchConfigurationItemsAsync(typed, CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            return;
        }

        var added = false;
        foreach (var hit in hits)
        {
            var name = hit.Display.Trim();
            if (hit.SysId.Length == 0 || name.Length == 0 || name.Equals(hit.SysId, StringComparison.OrdinalIgnoreCase))
                continue;
            Remember(hit.SysId, name);
            added = true;
        }

        if (!added)
            return;

        PublishNow();
    }

    private async Task PublishFilterAsync(int version, string filter, CancellationToken token)
    {
        try
        {
            if (FilterDelay > TimeSpan.Zero)
                await Task.Delay(FilterDelay, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (version != Volatile.Read(ref _filterVersion))
            return;

        var source = _all.ToArray();
        var selected = _id;
        ChoicePage page;
        try
        {
            page = await Task.Run(
                () => ReferenceNameMatcher.TakePage(source, filter, ReferenceNameMatcher.PageSize, selected),
                token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        WritePage(page, version);
    }

    private void PublishNow()
    {
        _filterDelay?.Cancel();
        var version = NextVersion();
        var page = ReferenceNameMatcher.TakePage(_all, _filter, ReferenceNameMatcher.PageSize, _id);
        WritePage(page, version);
    }

    private async Task PublishOffThreadAsync()
    {
        _filterDelay?.Cancel();
        var version = NextVersion();
        var source = _all.ToArray();
        var filter = _filter;
        var selected = _id;
        var page = await Task.Run(
            () => ReferenceNameMatcher.TakePage(source, filter, ReferenceNameMatcher.PageSize, selected)).ConfigureAwait(true);
        WritePage(page, version);
    }

    private void WritePage(ChoicePage page, int version)
    {
        lock (_gate)
        {
            if (version != _filterVersion)
                return;

            BeginApply();
            try
            {
                var selected = _id;
                var next = new List<Choice>(page.Rows.Count + 1) { new("", "None") };
                foreach (var row in page.Rows)
                {
                    if (string.IsNullOrEmpty(row.Value))
                        continue;
                    if (next.Any(existing => existing.Value.Equals(row.Value, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    next.Add(row);
                }

                if (!SameChoices(next))
                {
                    Choices.Clear();
                    foreach (var choice in next)
                        Choices.Add(choice);
                }

                _matchCount = page.MatchCount;
                OnPropertyChanged(nameof(MatchCount));
                OnPropertyChanged(nameof(MatchSummary));
                AssignId(selected);
                OnPropertyChanged(nameof(Id));
                OnPropertyChanged(nameof(SelectedLabel));
            }
            finally
            {
                EndApply();
            }
        }
    }

    private bool CacheHasMatch(string typed)
    {
        foreach (var choice in _all)
        {
            if (ReferenceNameMatcher.Matches(choice.Label, typed))
                return true;
        }

        return false;
    }

    private Choice? FindExact(string typed)
    {
        foreach (var choice in _all)
        {
            if (choice.Label.Equals(typed, StringComparison.OrdinalIgnoreCase))
                return choice;
        }

        return null;
    }

    private static List<Choice> Prepare(IReadOnlyList<Choice> rows, string selected, string label)
    {
        var all = CopyRows(rows);
        RememberInto(all, selected, label);
        return all;
    }

    private static void KeepSearchHits(List<Choice> next, IReadOnlyList<Choice> previous)
    {
        foreach (var choice in previous)
        {
            if (string.IsNullOrEmpty(choice.Value))
                continue;
            RememberInto(next, choice.Value, choice.Label);
        }
    }

    private static List<Choice> CopyRows(IReadOnlyList<Choice> rows)
    {
        var all = new List<Choice>(rows.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in rows)
        {
            if (string.IsNullOrEmpty(choice.Value) || !seen.Add(choice.Value))
                continue;
            all.Add(choice);
        }

        return all;
    }

    private static void RememberInto(List<Choice> all, string id, string label)
    {
        if (string.IsNullOrEmpty(id))
            return;
        var name = string.IsNullOrWhiteSpace(label) || label.Equals(id, StringComparison.OrdinalIgnoreCase) ? "" : label.Trim();
        var existing = all.FirstOrDefault(choice => choice.Value.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            if (name.Length > 0)
                all.Add(new Choice(id, name));
            return;
        }

        if (name.Length == 0 || existing.Label.Equals(name, StringComparison.Ordinal))
            return;
        all.Remove(existing);
        all.Add(new Choice(id, name));
    }

    private int ShownCount()
    {
        var shown = 0;
        foreach (var choice in Choices)
        {
            if (!string.IsNullOrEmpty(choice.Value))
                shown++;
        }

        return shown;
    }

    private bool SameChoices(List<Choice> next)
    {
        if (Choices.Count != next.Count)
            return false;
        for (var index = 0; index < next.Count; index++)
        {
            if (!Choices[index].Value.Equals(next[index].Value, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Choices[index].Label, next[index].Label, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private void Remember(string id, string label) => RememberInto(_all, id, label);

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

    private int NextVersion()
    {
        lock (_gate)
            return ++_filterVersion;
    }

    private void BeginApply() => _applyDepth++;

    private void EndApply() => _applyDepth = Math.Max(0, _applyDepth - 1);
}
