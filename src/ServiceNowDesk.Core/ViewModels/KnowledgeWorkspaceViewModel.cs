using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Client;
using ServiceNowDesk.Mapping;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

public sealed partial class KnowledgeWorkspaceViewModel : ObservableObject
{
    private readonly List<KnowledgeListRow> _all = [];
    private IServiceNowClient? _client;
    private bool _suppressSelection;
    private string _openSysId = "";

    [ObservableProperty] private bool hasArticle;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string number = "";
    [ObservableProperty] private string title = "";
    [ObservableProperty] private string meta = "";
    [ObservableProperty] private string body = "";
    [ObservableProperty] private string errorMessage = "";
    [ObservableProperty] private string filterText = "";
    [ObservableProperty] private string listNote = "";
    [ObservableProperty] private KnowledgeListRow? selectedArticle;

    public ObservableCollection<KnowledgeListRow> Articles { get; } = [];

    public bool HasArticles => _all.Count > 0;

    public bool ShowPlaceholder => !HasArticle && !IsLoading;

    public string ListCaption
    {
        get
        {
            if (_all.Count == 0)
                return "";
            if (string.IsNullOrWhiteSpace(FilterText) || Articles.Count == _all.Count)
                return _all.Count == 1 ? "1 article" : _all.Count + " articles";
            return Articles.Count + " of " + _all.Count;
        }
    }

    public void Attach(IServiceNowClient? client) => _client = client;

    public void ShowArticles(IEnumerable<KnowledgeListRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _all.Clear();
        _all.AddRange(rows.Where(row => !string.IsNullOrWhiteSpace(row.SysId)));
        Publish();
    }

    public async Task OpenAsync(IServiceNowClient? client, string sysId)
    {
        if (client is not null)
            _client = client;
        if (_client is null || string.IsNullOrWhiteSpace(sysId))
            return;

        try
        {
            IsLoading = true;
            ErrorMessage = "";
            var article = await _client.GetKnowledgeAsync(sysId, CancellationToken.None);
            Number = article.Number;
            Title = article.ShortDescription;
            var details = new[]
            {
                string.IsNullOrWhiteSpace(article.WorkflowStateLabel) ? article.WorkflowState : article.WorkflowStateLabel,
                article.Topic,
                string.IsNullOrWhiteSpace(article.Category) ? article.KnowledgeBase : article.Category,
                article.Author.Display,
                string.IsNullOrWhiteSpace(article.UpdatedAtDisplay) ? "" : "Updated " + article.UpdatedAtDisplay
            };
            Meta = string.Join(" · ", details.Where(part => !string.IsNullOrWhiteSpace(part)));
            Body = HtmlText.ToReadable(article.Text);
            HasArticle = true;
            _openSysId = article.SysId;
            SelectWithoutOpening(article.SysId);
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Clear()
    {
        _suppressSelection = true;
        _client = null;
        _all.Clear();
        Articles.Clear();
        SelectedArticle = null;
        FilterText = "";
        _openSysId = "";
        _suppressSelection = false;
        HasArticle = false;
        IsLoading = false;
        Number = "";
        Title = "";
        Meta = "";
        Body = "";
        ErrorMessage = "";
        ListNote = "";
        OnPropertyChanged(nameof(HasArticles));
        OnPropertyChanged(nameof(ListCaption));
    }

    public void NoteListFailure(string message)
    {
        ListNote = string.IsNullOrWhiteSpace(message) ? "Could not refresh knowledge articles." : message.Trim();
    }

    partial void OnHasArticleChanged(bool value) => OnPropertyChanged(nameof(ShowPlaceholder));

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowPlaceholder));

    partial void OnFilterTextChanged(string value)
    {
        if (_suppressSelection)
            return;
        Publish();
    }

    partial void OnSelectedArticleChanged(KnowledgeListRow? value)
    {
        if (_suppressSelection || value is null || _client is null)
            return;
        if (HasArticle && string.Equals(_openSysId, value.SysId, StringComparison.OrdinalIgnoreCase))
            return;
        _ = OpenAsync(_client, value.SysId);
    }

    private void Publish()
    {
        var next = Filtered().ToArray();
        var keep = SelectedArticle?.SysId;
        if (string.IsNullOrEmpty(keep))
            keep = _openSysId;
        _suppressSelection = true;
        SyncArticles(next);
        SelectedArticle = string.IsNullOrEmpty(keep)
            ? null
            : Articles.FirstOrDefault(row => string.Equals(row.SysId, keep, StringComparison.OrdinalIgnoreCase));
        _suppressSelection = false;
        ListNote = _all.Count == 0
            ? "No knowledge articles saved on this PC yet."
            : Articles.Count == 0 ? "No articles match that filter." : "";
        OnPropertyChanged(nameof(HasArticles));
        OnPropertyChanged(nameof(ListCaption));
    }

    private IEnumerable<KnowledgeListRow> Filtered()
    {
        var term = FilterText.Trim();
        if (term.Length == 0)
            return _all;
        return _all.Where(row => Matches(row, term));
    }

    private static bool Matches(KnowledgeListRow row, string term) =>
        row.Number.Contains(term, StringComparison.OrdinalIgnoreCase)
        || row.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
        || row.Meta.Contains(term, StringComparison.OrdinalIgnoreCase)
        || row.StateLabel.Contains(term, StringComparison.OrdinalIgnoreCase);

    private void SelectWithoutOpening(string sysId)
    {
        var match = Articles.FirstOrDefault(row => string.Equals(row.SysId, sysId, StringComparison.OrdinalIgnoreCase));
        if (match is null || ReferenceEquals(SelectedArticle, match))
            return;
        _suppressSelection = true;
        SelectedArticle = match;
        _suppressSelection = false;
    }

    private void SyncArticles(IReadOnlyList<KnowledgeListRow> next)
    {
        var incoming = new HashSet<string>(next.Select(row => row.SysId), StringComparer.OrdinalIgnoreCase);
        for (var i = Articles.Count - 1; i >= 0; i--)
        {
            if (!incoming.Contains(Articles[i].SysId))
                Articles.RemoveAt(i);
        }

        for (var i = 0; i < next.Count; i++)
        {
            var row = next[i];
            var existing = -1;
            for (var j = 0; j < Articles.Count; j++)
            {
                if (string.Equals(Articles[j].SysId, row.SysId, StringComparison.OrdinalIgnoreCase))
                {
                    existing = j;
                    break;
                }
            }

            if (existing < 0)
            {
                Articles.Insert(Math.Min(i, Articles.Count), row);
                continue;
            }

            if (!SameRow(Articles[existing], row))
                Articles[existing] = row;
            if (existing != i && i < Articles.Count)
                Articles.Move(existing, i);
        }
    }

    private static bool SameRow(KnowledgeListRow left, KnowledgeListRow right) =>
        string.Equals(left.Number, right.Number, StringComparison.Ordinal)
        && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
        && string.Equals(left.StateLabel, right.StateLabel, StringComparison.Ordinal)
        && string.Equals(left.Meta, right.Meta, StringComparison.Ordinal)
        && string.Equals(left.When, right.When, StringComparison.Ordinal);
}

public sealed class KnowledgeListRow
{
    public required string SysId { get; init; }
    public required string Number { get; init; }
    public required string Title { get; init; }
    public string StateLabel { get; init; } = "";
    public string Meta { get; init; } = "";
    public string When { get; init; } = "";

    public static KnowledgeListRow FromArticle(KnowledgeArticle article) => new()
    {
        SysId = article.SysId,
        Number = article.Number,
        Title = article.ShortDescription,
        StateLabel = string.IsNullOrWhiteSpace(article.WorkflowStateLabel) ? article.WorkflowState : article.WorkflowStateLabel,
        Meta = Join(article.Topic, string.IsNullOrWhiteSpace(article.Category) ? article.KnowledgeBase : article.Category, article.Author.Display),
        When = article.UpdatedAtDisplay
    };

    public static KnowledgeListRow FromCached(CachedTicketRow row) => new()
    {
        SysId = row.SysId,
        Number = row.Number,
        Title = row.Title,
        StateLabel = row.StateLabel,
        Meta = row.Meta,
        When = row.When
    };

    public CachedTicketRow ToCached() => new()
    {
        SysId = SysId,
        Number = Number,
        Title = Title,
        StateLabel = StateLabel,
        Tone = "open",
        Meta = Meta,
        When = When
    };

    private static string Join(params string[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}
