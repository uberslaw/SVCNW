using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

public partial class CatalogVariableInput : ObservableObject
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public bool Mandatory { get; init; }
    public IReadOnlyList<Choice> Choices { get; init; } = [];
    public bool HasChoices => Choices.Count > 0;
    public string DisplayLabel => Mandatory ? Label + " *" : Label;

    [ObservableProperty] private string value = "";
}

public partial class CatalogWorkspaceViewModel : ObservableObject
{
    private IServiceNowClient? _client;

    public CatalogWorkspaceViewModel()
    {
        RequestedFor = new ReferenceFieldModel(SearchUsersAsync);
    }

    public ReferenceFieldModel RequestedFor { get; }
    public ObservableCollection<CatalogItemSummary> Items { get; } = [];
    public ObservableCollection<CatalogVariableInput> Variables { get; } = [];

    [ObservableProperty] private CatalogItemSummary? selectedItem;
    [ObservableProperty] private int quantity = 1;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string message = "Search the catalog, fill the variables, and order it for the caller.";
    [ObservableProperty] private string errorMessage = "";

    public event EventHandler<CatalogOrderResult>? RequestOrdered;

    public void Attach(IServiceNowClient? client) => _client = client;

    public async Task RunAsync(IServiceNowClient? client, string? text)
    {
        _client = client;
        if (client is null)
            return;

        var trimmed = (text ?? "").Trim();
        if (trimmed.Length < 2)
        {
            Items.Clear();
            Message = "Type at least 2 characters to find a catalog item.";
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = "";
            var items = await client.SearchCatalogItemsAsync(trimmed, CancellationToken.None);
            var keep = SelectedItem?.SysId;
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);
            SelectedItem = Items.FirstOrDefault(item => item.SysId == keep);
            Message = Items.Count == 0 ? "No catalog items matched." : Items.Count + " catalog items.";
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OrderAsync()
    {
        if (_client is null || SelectedItem is null || IsBusy)
            return;
        if (Quantity < 1)
            Quantity = 1;
        await RequestedFor.AcceptExactUserAsync();
        if (!string.IsNullOrWhiteSpace(RequestedFor.Text) && string.IsNullOrEmpty(RequestedFor.SysId))
        {
            ErrorMessage = "Choose the requested-for person from the list, or clear the field.";
            return;
        }

        foreach (var variable in Variables)
        {
            if (variable.Mandatory && string.IsNullOrWhiteSpace(variable.Value))
            {
                ErrorMessage = "Enter " + variable.Label + ".";
                return;
            }
        }

        try
        {
            IsBusy = true;
            ErrorMessage = "";
            var values = Variables.ToDictionary(variable => variable.Name, variable => variable.Value.Trim());
            var result = await _client.OrderCatalogItemAsync(SelectedItem.SysId, Quantity, FieldDiff.NullIfEmpty(RequestedFor.SysId), values, CancellationToken.None);
            Message = string.IsNullOrWhiteSpace(result.RequestNumber)
                ? "Catalog item ordered."
                : "Ordered " + result.RequestNumber + ".";
            RequestOrdered?.Invoke(this, result);
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedItemChanged(CatalogItemSummary? value) => _ = LoadVariablesAsync(value);

    private async Task LoadVariablesAsync(CatalogItemSummary? item)
    {
        Variables.Clear();
        if (_client is null || item is null)
            return;

        try
        {
            var definitions = await _client.GetCatalogVariablesAsync(item.SysId, CancellationToken.None);
            foreach (var definition in definitions)
            {
                Variables.Add(new CatalogVariableInput
                {
                    Name = definition.Name,
                    Label = definition.Label,
                    Mandatory = definition.Mandatory,
                    Choices = definition.Choices
                });
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
    }

    private Task<IReadOnlyList<ReferenceSuggestion>> SearchUsersAsync(string text, CancellationToken cancellationToken) =>
        _client is null
            ? Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([])
            : _client.SearchUsersAsync(text, cancellationToken);
}
