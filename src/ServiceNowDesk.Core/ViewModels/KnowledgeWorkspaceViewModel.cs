using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Client;
using ServiceNowDesk.Mapping;

namespace ServiceNowDesk.ViewModels;

public sealed partial class KnowledgeWorkspaceViewModel : ObservableObject
{
    [ObservableProperty] private bool hasArticle;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string number = "";
    [ObservableProperty] private string title = "";
    [ObservableProperty] private string meta = "";
    [ObservableProperty] private string body = "";
    [ObservableProperty] private string errorMessage = "";

    public bool ShowPlaceholder => !HasArticle && !IsLoading;

    public async Task OpenAsync(IServiceNowClient? client, string sysId)
    {
        if (client is null || string.IsNullOrWhiteSpace(sysId))
            return;

        try
        {
            IsLoading = true;
            ErrorMessage = "";
            var article = await client.GetKnowledgeAsync(sysId, CancellationToken.None);
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
        HasArticle = false;
        IsLoading = false;
        Number = "";
        Title = "";
        Meta = "";
        Body = "";
        ErrorMessage = "";
    }

    partial void OnHasArticleChanged(bool value) => OnPropertyChanged(nameof(ShowPlaceholder));

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowPlaceholder));
}
