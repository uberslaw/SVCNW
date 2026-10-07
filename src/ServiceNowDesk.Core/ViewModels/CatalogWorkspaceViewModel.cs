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
    private GenericRequestVariables _genericVariables = GenericRequestVariables.Fallback;
    private Task? _prepare;
    private bool _genericPersonEdited;
    private bool _applyingGenericPerson;

    public CatalogWorkspaceViewModel()
    {
        RequestedFor = new ReferenceFieldModel(SearchUsersAsync, match: MatchUsersAsync);
        GenericRequestedFor = new ReferenceFieldModel(SearchUsersAsync, match: MatchUsersAsync);
        GenericRequestedFor.Changed += (_, _) =>
        {
            if (!_applyingGenericPerson)
                _genericPersonEdited = true;
        };
    }

    public ReferenceFieldModel RequestedFor { get; }
    public ReferenceFieldModel GenericRequestedFor { get; }
    public string GenericSummary => GenericRequestItem.Summary;
    public string GenericWarning => GenericRequestItem.Warning;
    public ObservableCollection<CatalogItemSummary> Items { get; } = [];
    public ObservableCollection<CatalogVariableInput> Variables { get; } = [];

    [ObservableProperty] private CatalogItemSummary? selectedItem;
    [ObservableProperty] private int quantity = 1;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string message = "Search the catalog, fill the variables, and order it for the caller.";
    [ObservableProperty] private string errorMessage = "";
    [ObservableProperty] private string genericTitle = "";
    [ObservableProperty] private string genericDescription = "";
    [ObservableProperty] private string genericMessage = "";
    [ObservableProperty] private string genericError = "";

    public event EventHandler<CatalogOrderResult>? RequestOrdered;

    public void Attach(IServiceNowClient? client)
    {
        var previous = _client;
        _client = client;
        _genericVariables = GenericRequestVariables.Fallback;
        _prepare = null;
        if (ReferenceEquals(previous, client) || _genericPersonEdited)
            return;

        _applyingGenericPerson = true;
        GenericRequestedFor.Clear();
        _applyingGenericPerson = false;
    }

    public void RememberSignedInUser(CurrentUser? user)
    {
        if (user is null || string.IsNullOrWhiteSpace(user.SysId) || _genericPersonEdited)
            return;
        if (!string.IsNullOrWhiteSpace(GenericRequestedFor.Text))
            return;

        _applyingGenericPerson = true;
        GenericRequestedFor.Set(user.SysId, user.Name);
        _applyingGenericPerson = false;
    }

    public Task PrepareGenericRequestAsync()
    {
        _prepare ??= PrepareGenericRequestCoreAsync();
        return _prepare;
    }

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
            Message = result.Numbers.Length == 0
                ? "Catalog item ordered."
                : "Ordered " + result.Numbers + ".";
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

    [RelayCommand]
    private async Task SubmitGenericRequestAsync()
    {
        if (_client is null || IsBusy)
            return;

        GenericError = "";
        await PrepareGenericRequestAsync();

        await GenericRequestedFor.AcceptExactUserAsync();
        if (string.IsNullOrEmpty(GenericRequestedFor.SysId))
        {
            GenericError = string.IsNullOrWhiteSpace(GenericRequestedFor.Text)
                ? "Choose who this request is for."
                : "Choose the requested-for person from the list.";
            return;
        }

        var title = GenericTitle.Trim();
        var description = GenericDescription.Trim();
        if (title.Length == 0)
        {
            GenericError = "Enter a request title.";
            return;
        }

        if (description.Length == 0)
        {
            GenericError = "Enter a request description.";
            return;
        }

        try
        {
            IsBusy = true;
            GenericMessage = "";
            var values = _genericVariables.ToPayload(GenericRequestedFor.SysId, title, description);
            var result = await _client.OrderCatalogItemAsync(
                GenericRequestItem.SysId,
                1,
                GenericRequestedFor.SysId,
                values,
                CancellationToken.None);
            result = await FindRequestedItemAsync(result);
            GenericTitle = "";
            GenericDescription = "";
            GenericMessage = result.Numbers.Length == 0 ? "Request submitted." : "Submitted " + result.Numbers + ".";
            RequestOrdered?.Invoke(this, result);
        }
        catch (Exception ex)
        {
            GenericError = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedItemChanged(CatalogItemSummary? value) => _ = LoadVariablesAsync(value);

    private async Task PrepareGenericRequestCoreAsync()
    {
        var client = _client;
        if (client is null)
            return;

        try
        {
            var user = await client.GetCurrentUserAsync(CancellationToken.None);
            if (ReferenceEquals(_client, client) && !_genericPersonEdited && string.IsNullOrWhiteSpace(GenericRequestedFor.Text))
            {
                _applyingGenericPerson = true;
                GenericRequestedFor.Set(user.SysId, user.Name);
                _applyingGenericPerson = false;
            }
        }
        catch
        {
            // The signed-in person stays blank. The field can still be searched.
        }

        try
        {
            var definitions = await client.GetCatalogVariablesAsync(GenericRequestItem.SysId, CancellationToken.None);
            if (ReferenceEquals(_client, client))
                _genericVariables = GenericRequestVariables.Resolve(definitions);
        }
        catch
        {
            if (ReferenceEquals(_client, client))
                _genericVariables = GenericRequestVariables.Fallback;
        }
    }

    private async Task<CatalogOrderResult> FindRequestedItemAsync(CatalogOrderResult result)
    {
        if (_client is null || !string.IsNullOrWhiteSpace(result.RequestedItemSysId))
            return result;

        try
        {
            if (!string.IsNullOrWhiteSpace(result.RequestSysId))
            {
                var page = await _client.SearchRequestedItemsAsync(new TicketQuery
                {
                    ParentRequestId = result.RequestSysId,
                    Activity = ActivityFilter.Any,
                    Assignment = AssignmentScope.Any,
                    Limit = 5,
                    ExtraClause = "ORDERBYDESCsys_created_on"
                }, CancellationToken.None);
                var item = page.Items.FirstOrDefault();
                if (item is not null)
                {
                    return result with
                    {
                        RequestedItemSysId = item.SysId,
                        RequestedItemNumber = string.IsNullOrWhiteSpace(result.RequestedItemNumber) ? item.Number : result.RequestedItemNumber
                    };
                }
            }

            if (!string.IsNullOrWhiteSpace(result.RequestedItemNumber))
            {
                var page = await _client.SearchRequestedItemsAsync(new TicketQuery
                {
                    Text = result.RequestedItemNumber,
                    Activity = ActivityFilter.Any,
                    Assignment = AssignmentScope.Any,
                    Limit = 5
                }, CancellationToken.None);
                var item = page.Items.FirstOrDefault(row =>
                    row.Number.Equals(result.RequestedItemNumber, StringComparison.OrdinalIgnoreCase));
                if (item is not null)
                    return result with { RequestedItemSysId = item.SysId };
            }
        }
        catch
        {
            // The order succeeded. Show the numbers already in hand.
        }

        return result;
    }

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

    private Task<IReadOnlyList<ReferenceSuggestion>> MatchUsersAsync(string text, CancellationToken cancellationToken) =>
        _client is null
            ? Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([])
            : _client.MatchUsersAsync(text, cancellationToken);
}
