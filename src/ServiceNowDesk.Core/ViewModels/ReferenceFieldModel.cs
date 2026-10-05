using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

public sealed partial class ReferenceFieldModel : ObservableObject
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<ReferenceSuggestion>>> _search;
    private readonly TimeSpan _delay;
    private CancellationTokenSource? _searchCts;
    private bool _suppress;

    public ReferenceFieldModel(
        Func<string, CancellationToken, Task<IReadOnlyList<ReferenceSuggestion>>> search,
        TimeSpan? delay = null)
    {
        _search = search;
        _delay = delay ?? TimeSpan.FromMilliseconds(180);
        Suggestions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSuggestions));
    }

    public ObservableCollection<ReferenceSuggestion> Suggestions { get; } = [];

    [ObservableProperty] private string text = "";
    [ObservableProperty] private string sysId = "";
    [ObservableProperty] private int highlightedIndex = -1;

    public bool HasSuggestions => Suggestions.Count > 0;

    public event EventHandler? Changed;

    public ReferenceSuggestion? Highlighted =>
        HighlightedIndex >= 0 && HighlightedIndex < Suggestions.Count ? Suggestions[HighlightedIndex] : null;

    partial void OnTextChanged(string value)
    {
        if (_suppress)
            return;

        if (SysId.Length > 0)
            SysId = "";
        Changed?.Invoke(this, EventArgs.Empty);
        _ = SearchAsync(value);
    }

    public void Choose(ReferenceSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        _searchCts?.Cancel();
        _suppress = true;
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

public sealed class TicketRow
{
    public required string SysId { get; init; }
    public required string Number { get; init; }
    public required string Title { get; init; }
    public required string StateLabel { get; init; }
    public required string Tone { get; init; }
    public required string Meta { get; init; }
    public required string When { get; init; }
    public string Badge { get; init; } = "";

    public static TicketRow FromIncident(IncidentRecord record) => new()
    {
        SysId = record.SysId,
        Number = record.Number,
        Title = record.ShortDescription,
        StateLabel = record.StateLabel,
        Tone = Client.StateTone.ForIncident(record.State),
        Meta = Join(record.Caller.Display, record.AssignmentGroup.Display),
        When = record.UpdatedAtDisplay,
        Badge = BadgeFor(record.Priority)
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
        Badge = BadgeFor(record.Priority)
    };

    public static TicketRow FromInteraction(InteractionRecord record) => new()
    {
        SysId = record.SysId,
        Number = record.Number,
        Title = record.ShortDescription,
        StateLabel = record.StateLabel,
        Tone = Client.StateTone.ForInteraction(record.State),
        Meta = Join(record.OpenedFor.Display, record.AssignmentGroup.Display),
        When = record.UpdatedAtDisplay
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
        Badge = BadgeFor(record.Priority)
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
