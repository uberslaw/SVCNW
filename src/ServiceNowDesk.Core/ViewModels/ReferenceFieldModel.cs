using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

public sealed partial class ReferenceFieldModel : ObservableObject
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<ReferenceSuggestion>>> _search;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<ReferenceSuggestion>>> _match;
    private readonly TimeSpan _delay;
    private CancellationTokenSource? _searchCts;
    private ReferenceSuggestion? _chosen;
    private bool _suppress;

    public ReferenceFieldModel(
        Func<string, CancellationToken, Task<IReadOnlyList<ReferenceSuggestion>>> search,
        TimeSpan? delay = null,
        Func<string, CancellationToken, Task<IReadOnlyList<ReferenceSuggestion>>>? match = null)
    {
        _search = search;
        _match = match ?? search;
        _delay = delay ?? TimeSpan.FromMilliseconds(180);
        Suggestions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSuggestions));
    }

    public ObservableCollection<ReferenceSuggestion> Suggestions { get; } = [];

    [ObservableProperty] private string text = "";
    [ObservableProperty] private string sysId = "";
    [ObservableProperty] private int highlightedIndex = -1;

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>True when a sys_user (or other reference) sys_id is bound to the current text.</summary>
    public bool IsMatched => !string.IsNullOrEmpty(SysId);

    public event EventHandler? Changed;

    public ReferenceSuggestion? Highlighted =>
        HighlightedIndex >= 0 && HighlightedIndex < Suggestions.Count ? Suggestions[HighlightedIndex] : null;

    partial void OnSysIdChanged(string value) => OnPropertyChanged(nameof(IsMatched));

    partial void OnTextChanged(string value)
    {
        if (_suppress)
            return;

        if (!string.IsNullOrEmpty(SysId) && !TextMatchesChosen(value))
            SysId = "";
        if (string.IsNullOrEmpty(SysId))
            _chosen = null;
        Changed?.Invoke(this, EventArgs.Empty);
        _ = SearchAsync(value);
    }

    private bool TextMatchesChosen(string? value)
    {
        if (_chosen is null)
            return false;
        var typed = (value ?? "").Trim();
        if (typed.Length == 0)
            return false;
        return Same(_chosen.Display, typed) || Same(_chosen.UserName, typed) || Same(_chosen.Email, typed);
    }

    private static bool Same(string? candidate, string typed) =>
        string.Equals((candidate ?? "").Trim(), typed, StringComparison.OrdinalIgnoreCase);

    public async Task<bool> AcceptExactUserAsync()
    {
        if (!string.IsNullOrEmpty(SysId))
            return true;

        var typed = (Text ?? "").Trim();
        if (typed.Length == 0)
            return true;

        _searchCts?.Cancel();
        IReadOnlyList<ReferenceSuggestion> matches;
        try
        {
            matches = await _match(typed, CancellationToken.None);
        }
        catch
        {
            return false;
        }

        var exact = new List<ReferenceSuggestion>();
        foreach (var match in matches)
        {
            if (string.IsNullOrWhiteSpace(match.SysId) || !IsExactUser(match, typed))
                continue;
            if (exact.Any(item => item.SysId.Equals(match.SysId, StringComparison.OrdinalIgnoreCase)))
                continue;
            exact.Add(match);
        }

        if (exact.Count != 1)
            return false;

        Choose(exact[0]);
        return true;
    }

    private static bool IsExactUser(ReferenceSuggestion match, string typed) =>
        string.Equals((match.Display ?? "").Trim(), typed, StringComparison.OrdinalIgnoreCase)
        || string.Equals((match.UserName ?? "").Trim(), typed, StringComparison.OrdinalIgnoreCase)
        || string.Equals((match.Email ?? "").Trim(), typed, StringComparison.OrdinalIgnoreCase);

    public void Choose(ReferenceSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        _searchCts?.Cancel();
        _suppress = true;
        _chosen = suggestion;
        SysId = suggestion.SysId;
        Text = suggestion.Display;
        Suggestions.Clear();
        HighlightedIndex = -1;
        _suppress = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Set(string? sysId, string? display)
    {
        _searchCts?.Cancel();
        _suppress = true;
        SysId = sysId ?? "";
        Text = display ?? "";
        _chosen = string.IsNullOrEmpty(SysId) ? null : new ReferenceSuggestion(SysId, Text, "");
        Suggestions.Clear();
        HighlightedIndex = -1;
        _suppress = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear() => Set("", "");

    public void Dismiss()
    {
        Suggestions.Clear();
        HighlightedIndex = -1;
    }

    private async Task SearchAsync(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        try
        {
            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay, token);

            if (value.Trim().Length < 2)
            {
                Suggestions.Clear();
                HighlightedIndex = -1;
                return;
            }

            var matches = await _search(value, token);
            if (token.IsCancellationRequested)
                return;

            Suggestions.Clear();
            foreach (var match in matches)
                Suggestions.Add(match);
            HighlightedIndex = Suggestions.Count > 0 ? 0 : -1;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            Suggestions.Clear();
            HighlightedIndex = -1;
        }
    }
}

public sealed class TicketRow : IHighlightRow
{
    private string _highlightHex = "";

    public required string SysId { get; init; }
    public required string Number { get; init; }
    public required string Title { get; init; }
    public required string StateLabel { get; init; }
    public required string Tone { get; init; }
    public required string Meta { get; init; }
    public required string When { get; init; }
    public string Badge { get; init; } = "";
    public bool Unassigned { get; init; }
    public string StateValue { get; init; } = "";
    public string SortKey { get; init; } = "";

    /// <summary>INC, RITM, or IMS on the combined list. Empty on a single-table list.</summary>
    public string Kind { get; init; } = "";

    /// <summary>Record type for the combined list. Single-table rows leave this unset.</summary>
    public DeskSection Source { get; init; }

    public string HighlightHex
    {
        get => _highlightHex;
        set
        {
            var next = value ?? "";
            if (string.Equals(_highlightHex, next, StringComparison.Ordinal))
                return;
            _highlightHex = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightHex)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static TicketRow FromIncident(IncidentRecord record) => new()
    {
        SysId = record.SysId,
        Number = record.Number,
        Title = record.ShortDescription,
        StateLabel = record.StateLabel,
        Tone = Client.StateTone.ForIncident(record.State),
        Meta = Join(record.Caller.Display, record.AssignmentGroup.Display),
        When = record.UpdatedAtDisplay,
        Badge = BadgeFor(record.Priority),
        Unassigned = record.AssignedTo.IsEmpty,
        StateValue = record.State,
        SortKey = record.UpdatedAtValue
    };

    public static TicketRow FromRequest(RequestRecord record) => new()
    {
        SysId = record.SysId,
        Number = record.Number,
        Title = record.ShortDescription,
        StateLabel = record.RequestStateLabel,
        Tone = Client.StateTone.ForRequest(record.RequestState),
        Meta = Join(record.RequestedFor.Display, record.StageLabel),
        When = record.UpdatedAtDisplay,
        Badge = BadgeFor(record.Priority),
        StateValue = record.RequestState,
        SortKey = record.UpdatedAtValue
    };

    public static TicketRow FromInteraction(InteractionRecord record) => new()
    {
        SysId = record.SysId,
        Number = record.Number,
        Title = record.ShortDescription,
        StateLabel = record.StateLabel,
        Tone = Client.StateTone.ForInteraction(record.State),
        Meta = Join(record.OpenedFor.Display, record.AssignmentGroup.Display),
        When = record.UpdatedAtDisplay,
        Unassigned = record.AssignedTo.IsEmpty,
        StateValue = record.State,
        SortKey = record.UpdatedAtValue
    };

    public static TicketRow FromItem(RequestedItemRecord record) => new()
    {
        SysId = record.SysId,
        Number = record.Number,
        Title = record.ShortDescription,
        StateLabel = record.StateLabel,
        Tone = Client.StateTone.ForItem(record.State),
        Meta = Join(record.CatalogItem.Display, record.AssignmentGroup.Display),
        When = record.UpdatedAtDisplay,
        Badge = BadgeFor(record.Priority),
        Unassigned = record.AssignedTo.IsEmpty,
        StateValue = record.State,
        SortKey = record.UpdatedAtValue
    };

    private static string BadgeFor(string priority) =>
        priority is "1" or "2" or "3" or "4" or "5" ? "P" + priority : "";

    private static string Join(params string[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}

public static class FieldDiff
{
    public static string? Changed(string? current, string? original) =>
        string.Equals(current ?? "", original ?? "", StringComparison.Ordinal) ? null : current ?? "";

    public static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class WorkspaceMessages
{
    public static string Describe(Exception exception) => exception switch
    {
        ServiceNowException or ArgumentException or InvalidOperationException => exception.Message,
        _ => "Something went wrong while talking to ServiceNow. " + exception.Message
    };
}
