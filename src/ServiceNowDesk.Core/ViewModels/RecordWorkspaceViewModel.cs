using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Query;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public abstract partial class RecordWorkspaceViewModel : ObservableObject
{
    private const string LiveInstanceMessage = "Connect to a live instance to open this record in the browser.";
    private CancellationTokenSource? _openCts;
    private TicketRow? _boundRow;
    private bool _suppressSelection;
    private int _loadVersion;
    private int _openVersion;

    protected RecordWorkspaceViewModel(IDesktopServices desktop, string tableName, string recordLabel, bool allowCreate, IReadOnlyList<PresetOption> presets, DeskSection section, bool attachments = false)
    {
        Desktop = desktop;
        TableName = tableName;
        RecordLabel = recordLabel;
        AllowCreate = allowCreate;
        Presets = presets;
        Preset = presets[0];
        Section = section;
        ResolveChoiceLabel = "Outcome";
        SupportsAttachments = attachments;
        Journal.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasJournal));
    }

    protected IDesktopServices Desktop { get; }
    protected IServiceNowClient? Client { get; private set; }
    protected bool Applying { get; set; }
    protected string? EditorSysId { get; set; }

    public string TableName { get; }
    public string RecordLabel { get; }
    public DeskSection Section { get; }
    public bool AllowCreate { get; }
    public bool SupportsAttachments { get; }
    public ObservableCollection<AttachmentSummary> Attachments { get; } = [];
    public IReadOnlyList<PresetOption> Presets { get; }
    public ObservableCollection<TicketRow> Items { get; } = [];

    public Action<TicketRow>? PrepareRow { get; set; }
    public event EventHandler<SavedTicketFields>? RecordSaved;
    public ObservableCollection<JournalEntry> Journal { get; } = [];
    public ObservableCollection<Choice> ResolveChoices { get; } = [];

    [ObservableProperty] private TicketRow? selected;
    [ObservableProperty] private PresetOption? preset;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string searchHint = "";
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isEditorBusy;
    [ObservableProperty] private bool isNew;
    [ObservableProperty] private bool hasEditor;
    [ObservableProperty] private bool hasLoaded;
    [ObservableProperty] private bool isReady;
    [ObservableProperty] private bool showUnsavedBanner;
    [ObservableProperty] private bool showResolvePanel;
    [ObservableProperty] private string number = "";
    [ObservableProperty] private string stateLabel = "";
    [ObservableProperty] private string priorityLabel = "";
    [ObservableProperty] private string openedAt = "";
    [ObservableProperty] private string shortDescription = "";
    [ObservableProperty] private string description = "";
    [ObservableProperty] private string journalText = "";
    [ObservableProperty] private JournalKind journalKind = JournalKind.WorkNotes;
    [ObservableProperty] private bool journalIsCustomerVisible;
    [ObservableProperty] private string editorMessage = "";
    [ObservableProperty] private string errorMessage = "";
    [ObservableProperty] private int totalCount;
    [ObservableProperty] private string resolveChoiceLabel = "";
    [ObservableProperty] private string resolveCode = "";
    [ObservableProperty] private string resolveNotes = "";
    [ObservableProperty] private string attachmentNote = "";

    public bool HasJournalText => !string.IsNullOrWhiteSpace(JournalText);

    public bool HasJournal => Journal.Count > 0;

    public void Attach(IServiceNowClient client)
    {
        Client = client;
        IsReady = true;
    }

    public void Detach()
    {
        _openCts?.Cancel();
        Client = null;
        IsReady = false;
        HasLoaded = false;
        Applying = true;
        _suppressSelection = true;
        Items.Clear();
        Journal.Clear();
        Selected = null;
        _boundRow = null;
        EditorSysId = null;
        Number = "";
        StateLabel = "";
        PriorityLabel = "";
        OpenedAt = "";
        ShortDescription = "";
        Description = "";
        JournalText = "";
        IsNew = false;
        HasEditor = false;
        IsDirty = false;
        ShowResolvePanel = false;
        ShowUnsavedBanner = false;
        ErrorMessage = "";
        EditorMessage = "";
        TotalCount = 0;
        Attachments.Clear();
        AttachmentNote = "";
        OnDetached();
        _suppressSelection = false;
        Applying = false;
    }

    public async Task OpenFromSearchAsync(string sysId)
    {
        if (IsDirty)
        {
            ShowUnsavedBanner = true;
            EditorMessage = "Save or discard unsaved changes before opening another record.";
            return;
        }

        await LoadEditorAsync(sysId, CancellationToken.None);
    }

    public virtual Task EnsureChoicesAsync() => Task.CompletedTask;

    public void ShowCachedRows(IReadOnlyList<TicketRow> rows, int totalCount)
    {
        var visible = rows.Where(ShowsOnThisList).ToArray();
        _suppressSelection = true;
        Items.Clear();
        foreach (var row in visible)
        {
            PrepareRow?.Invoke(row);
            Items.Add(row);
        }
        TotalCount = visible.Length == rows.Count ? totalCount : visible.Length;
        Selected = null;
        _boundRow = null;
        _suppressSelection = false;
        ErrorMessage = "";
        HasLoaded = true;
    }

    public async Task<bool> ReloadAsync()
    {
        ErrorMessage = "";
        await RefreshAsync();
        return HasLoaded && string.IsNullOrEmpty(ErrorMessage);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Client is null)
            return;

        var version = ++_loadVersion;
        try
        {
            IsLoading = true;
            var trimmed = SearchText.Trim();
            SearchHint = trimmed.Length is > 0 and < 2 && !EncodedQuery.IsNumberQuery(trimmed)
                ? "Type at least 2 characters to search."
                : "";
            var openAtStart = _openVersion;
            var editorAtStart = EditorSysId;
            var page = await FetchPageAsync(BuildQuery(), CancellationToken.None);
            if (version != _loadVersion)
                return;

            var openedDuringRefresh = openAtStart != _openVersion;
            var openStillApplying = openedDuringRefresh && EditorSysId == editorAtStart;
            var keep = openStillApplying ? null : EditorSysId;
            var visible = page.Items.Where(ShowsOnThisList).ToArray();
            _suppressSelection = true;
            Items.Clear();
            foreach (var row in visible)
            {
                PrepareRow?.Invoke(row);
                Items.Add(row);
            }
            TotalCount = visible.Length == page.Items.Count
                ? page.TotalCount ?? visible.Length
                : visible.Length;
            var match = keep is null ? null : Items.FirstOrDefault(row => row.SysId == keep);
            if (match is not null)
            {
                Selected = match;
                _boundRow = match;
            }

            _suppressSelection = false;
            HasLoaded = true;
        }
        catch (Exception ex)
        {
            if (version == _loadVersion)
                ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            if (version == _loadVersion)
                IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void NewRecord()
    {
        if (!CanCreate())
            return;
        if (IsDirty)
        {
            ShowUnsavedBanner = true;
            EditorMessage = "Save or discard unsaved changes first.";
            return;
        }

        BeginNew();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (Client is null || IsEditorBusy)
            return;
        await ResolveReferencesAsync(CancellationToken.None);
        if (!TryValidate(out var message))
        {
            ErrorMessage = message;
            return;
        }

        try
        {
            IsEditorBusy = true;
            ErrorMessage = "";
            Applying = true;
            if (IsNew)
                await SaveNewAsync(CancellationToken.None);
            else
                await SaveExistingAsync(CancellationToken.None);
            await PostPendingJournalAsync(CancellationToken.None);
            IsNew = false;
            ShowUnsavedBanner = false;
            EditorMessage = "Saved " + Number + ".";
            var assignee = SavedAssignee();
            RecordSaved?.Invoke(this, new SavedTicketFields(
                EditorSysId ?? "",
                ShortDescription ?? "",
                StateLabel ?? "",
                assignee?.Name,
                assignee?.Id));
            await RefreshAttachmentsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            Applying = false;
            IsEditorBusy = false;
            RecalculateDirty();
        }
    }

    [RelayCommand]
    private void Discard()
    {
        Applying = true;
        try
        {
            JournalText = "";
            ShowResolvePanel = false;
            ShowUnsavedBanner = false;
            ErrorMessage = "";
            Restore();
            EditorMessage = "Changes discarded.";
        }
        finally
        {
            Applying = false;
            RecalculateDirty();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPostJournal))]
    private async Task PostJournalAsync()
    {
        if (Client is null || IsEditorBusy)
            return;

        try
        {
            IsEditorBusy = true;
            ErrorMessage = "";
            await PostPendingJournalAsync(CancellationToken.None);
            EditorMessage = JournalKind == JournalKind.Comments
                ? "Customer comment posted on " + Number + "."
                : "Work note posted on " + Number + ".";
            ShowUnsavedBanner = false;
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            IsEditorBusy = false;
            RecalculateDirty();
        }
    }

    [RelayCommand]
    protected void BeginResolve()
    {
        if (IsNew || string.IsNullOrEmpty(EditorSysId))
        {
            EditorMessage = "Save the record before resolving it.";
            return;
        }

        ShowResolvePanel = true;
        if (string.IsNullOrWhiteSpace(ResolveCode) && ResolveChoices.Count > 0)
            ResolveCode = ResolveChoices[0].Value;
        EditorMessage = "";
    }

    [RelayCommand]
    private void CancelResolve() => ShowResolvePanel = false;

    [RelayCommand(CanExecute = nameof(CanConfirmResolve))]
    private async Task ConfirmResolveAsync()
    {
        if (Client is null || string.IsNullOrEmpty(EditorSysId))
            return;
        if (string.IsNullOrWhiteSpace(ResolveCode) || string.IsNullOrWhiteSpace(ResolveNotes))
        {
            ErrorMessage = "Choose an outcome and enter notes.";
            return;
        }

        try
        {
            IsEditorBusy = true;
            ErrorMessage = "";
            Applying = true;
            if (IsDirty)
            {
                if (!TryValidate(out var message))
                {
                    ErrorMessage = message;
                    return;
                }

                await SaveExistingAsync(CancellationToken.None);
                await PostPendingJournalAsync(CancellationToken.None);
            }

            await ResolveRecordAsync(CancellationToken.None);
            ShowResolvePanel = false;
            ResolveNotes = "";
            ShowUnsavedBanner = false;
            EditorMessage = "Updated " + Number + ".";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            Applying = false;
            IsEditorBusy = false;
            RecalculateDirty();
        }
    }

    [RelayCommand]
    private void CopyNumber()
    {
        if (Number.Length == 0)
            return;
        Desktop.CopyText(Number);
        EditorMessage = "Copied " + Number + ".";
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        if (Client?.InstanceUri is null || string.IsNullOrEmpty(EditorSysId))
        {
            EditorMessage = LiveInstanceMessage;
            return;
        }

        Desktop.OpenUrl(ServiceNowLinks.Record(Client.InstanceUri, TableName, EditorSysId));
    }

    [RelayCommand]
    private void OpenAttachment(AttachmentSummary? attachment)
    {
        if (attachment is null || Client is null)
            return;
        if (IsNew || string.IsNullOrEmpty(EditorSysId))
        {
            AttachmentNote = "Save the record before it can have attachments.";
            return;
        }

        var url = ResolveAttachmentUrl(attachment);
        if (url is null)
        {
            EditorMessage = LiveInstanceMessage;
            AttachmentNote = LiveInstanceMessage;
            return;
        }

        Desktop.OpenUrl(url);
    }

    private string? ResolveAttachmentUrl(AttachmentSummary attachment)
    {
        var given = attachment.Url?.Trim() ?? "";
        if (given.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || given.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return given;

        if (Client?.InstanceUri is null)
            return null;

        if (given.Length > 0)
        {
            var authority = Client.InstanceUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            return authority + (given.StartsWith('/') ? given : "/" + given);
        }

        return ServiceNowLinks.Attachment(Client.InstanceUri, attachment.SysId);
    }

    [RelayCommand]
    private void ApplyPreset(PresetOption? option)
    {
        if (option is null || option == Preset)
            return;
        Preset = option;
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = "";

    protected void Touch() => RecalculateDirty();

    protected void BeginNew()
    {
        Applying = true;
        _suppressSelection = true;
        try
        {
            Selected = null;
            _boundRow = null;
            EditorSysId = null;
            Number = "";
            StateLabel = "New";
            PriorityLabel = "";
            OpenedAt = "";
            JournalText = "";
            Journal.Clear();
            ShowResolvePanel = false;
            ShowUnsavedBanner = false;
            ErrorMessage = "";
            EditorMessage = "";
            IsNew = true;
            HasEditor = true;
            OnStartNew();
            if (SupportsAttachments)
            {
                Attachments.Clear();
                AttachmentNote = "Save the record before it can have attachments.";
            }
        }
        finally
        {
            _suppressSelection = false;
            Applying = false;
            RecalculateDirty();
        }
    }

    protected async Task FillChoicesAsync(
        ObservableCollection<Choice> target,
        string table,
        string element,
        IReadOnlyList<Choice> fallback,
        string? dependent = null,
        bool includeBlank = false,
        string blankLabel = "")
    {
        if (Client is null)
            return;

        IReadOnlyList<Choice> choices;
        try
        {
            choices = await Client.GetChoicesAsync(table, element, dependent, CancellationToken.None);
        }
        catch
        {
            choices = fallback;
        }

        if (choices is null || choices.Count == 0)
            choices = fallback;

        target.Clear();
        if (includeBlank)
            target.Add(new Choice("", blankLabel));
        foreach (var choice in choices)
        {
            if (choice?.Value is null)
                continue;
            target.Add(choice);
        }
    }

    protected static void EnsureChoice(ObservableCollection<Choice> target, string value, string label)
    {
        if (string.IsNullOrEmpty(value) || target.Any(choice => choice.Value == value))
            return;
        target.Add(new Choice(value, string.IsNullOrWhiteSpace(label) ? value : label));
    }

    protected virtual Task ResolveReferencesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected static bool ReferenceIsChosen(ReferenceFieldModel field)
    {
        var text = field.Text ?? "";
        var id = field.SysId ?? "";
        return string.IsNullOrWhiteSpace(text) == (id.Length == 0);
    }

    protected void UpsertRow(TicketRow row)
    {
        if (!ShowsOnThisList(row))
        {
            RemoveFromList(row.SysId);
            return;
        }

        var index = -1;
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i].SysId == row.SysId)
            {
                index = i;
                break;
            }
        }

        PrepareRow?.Invoke(row);
        _suppressSelection = true;
        if (index < 0)
            Items.Insert(0, row);
        else
            Items[index] = row;
        Selected = Items.First(item => item.SysId == row.SysId);
        _boundRow = Selected;
        _suppressSelection = false;
    }

    /// <summary>
    /// Open presets hide resolved, closed, and cancelled rows that still come back from ServiceNow.
    /// On hold stays. Closed and any-activity lists keep every row.
    /// </summary>
    protected bool ShowsOnThisList(TicketRow row)
    {
        if (Preset?.Activity != ActivityFilter.Open)
            return true;
        return AlertClassifier.IsStillOpen(Section, row.StateValue, row.StateLabel);
    }

    private void RemoveFromList(string sysId)
    {
        var index = -1;
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i].SysId == sysId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return;

        _suppressSelection = true;
        Items.RemoveAt(index);
        if (Selected?.SysId == sysId)
            Selected = null;
        _suppressSelection = false;
    }

    protected TicketQuery BuildQuery()
    {
        var trimmed = SearchText.Trim();
        string? text = null;
        if (trimmed.Length >= 2 || EncodedQuery.IsNumberQuery(trimmed))
            text = trimmed;

        return new TicketQuery
        {
            Text = text,
            Assignment = Preset?.Assignment ?? AssignmentScope.Any,
            Activity = Preset?.Activity ?? ActivityFilter.Open,
            AssignmentClause = Preset?.AssignmentClause,
            Limit = 50
        };
    }

    protected abstract Task<PagedResult<TicketRow>> FetchPageAsync(TicketQuery query, CancellationToken cancellationToken);
    protected abstract Task LoadRecordAsync(string sysId, CancellationToken cancellationToken);
    protected abstract bool ComputeDirty();
    protected abstract void OnStartNew();
    protected abstract void Restore();
    protected abstract Task SaveNewAsync(CancellationToken cancellationToken);
    protected abstract Task SaveExistingAsync(CancellationToken cancellationToken);

    /// <summary>Assignee name and id after a save. Null when this record has no assignee field.</summary>
    protected virtual (string Name, string Id)? SavedAssignee() => null;
    protected abstract Task ResolveRecordAsync(CancellationToken cancellationToken);
    protected virtual bool TryValidate(out string message)
    {
        message = "";
        return true;
    }

    protected virtual void OnDetached()
    {
    }

    private async Task RefreshAttachmentsAsync(CancellationToken cancellationToken)
    {
        if (!SupportsAttachments)
            return;

        if (IsNew || string.IsNullOrEmpty(EditorSysId) || Client is null)
        {
            Attachments.Clear();
            AttachmentNote = "Save the record before it can have attachments.";
            return;
        }

        var files = await Client.ListAttachmentsAsync(TableName, EditorSysId, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            return;

        Attachments.Clear();
        foreach (var file in files)
            Attachments.Add(file);
        if (Attachments.Count == 0)
            AttachmentNote = "No attachments.";
        else if (Client.InstanceUri is null)
            AttachmentNote = LiveInstanceMessage;
        else
            AttachmentNote = "";
    }

    protected void ReplaceJournal(IEnumerable<JournalEntry> notes)
    {
        Journal.Clear();
        foreach (var note in notes)
            Journal.Add(note);
    }

    protected async Task PostPendingJournalAsync(CancellationToken cancellationToken)
    {
        if (Client is null || string.IsNullOrWhiteSpace(JournalText) || string.IsNullOrEmpty(EditorSysId))
            return;

        var text = JournalText.Trim();
        var kind = JournalKind;
        await Client.AddJournalAsync(TableName, EditorSysId, kind, text, cancellationToken);
        JournalText = "";
        var notes = await Client.GetJournalAsync(TableName, EditorSysId, cancellationToken);
        ReplaceJournal(notes);
    }

    private async Task LoadEditorAsync(string sysId, CancellationToken cancellationToken)
    {
        if (Client is null || string.IsNullOrWhiteSpace(sysId))
            return;

        var version = ++_openVersion;
        try
        {
            IsEditorBusy = true;
            ErrorMessage = "";
            Applying = true;
            await LoadRecordAsync(sysId, cancellationToken);
            if (version != _openVersion)
                return;

            EditorSysId = sysId;
            IsNew = false;
            HasEditor = true;
            ShowResolvePanel = false;
            ShowUnsavedBanner = false;
            Applying = false;
            var notes = await Client.GetJournalAsync(TableName, sysId, cancellationToken);
            if (version != _openVersion)
                return;

            ReplaceJournal(notes);
            await RefreshAttachmentsAsync(cancellationToken);
            if (version != _openVersion)
                return;
            _boundRow = Items.FirstOrDefault(row => row.SysId == sysId);
            _suppressSelection = true;
            if (_boundRow is not null)
                Selected = _boundRow;
            _suppressSelection = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version == _openVersion)
                ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            if (version == _openVersion)
            {
                Applying = false;
                IsEditorBusy = false;
                RecalculateDirty();
            }
        }
    }

    private void QueueOpen(string sysId)
    {
        _openCts?.Cancel();
        _openCts = new CancellationTokenSource();
        var token = _openCts.Token;
        _ = OpenAfterDelayAsync(sysId, token);
    }

    private async Task OpenAfterDelayAsync(string sysId, CancellationToken token)
    {
        try
        {
            await Task.Delay(80, token);
            await LoadEditorAsync(sysId, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RecalculateDirty()
    {
        if (Applying)
            return;
        IsDirty = ComputeDirty();
    }

    private bool CanCreate() => AllowCreate && IsReady && Client is not null;

    private bool CanSave() => IsReady && !IsEditorBusy && IsDirty;

    private bool CanPostJournal() =>
        IsReady && !IsEditorBusy && !IsNew && !string.IsNullOrWhiteSpace(JournalText) && !string.IsNullOrEmpty(EditorSysId);

    private bool CanConfirmResolve() =>
        IsReady && !IsEditorBusy && HasEditor && !IsNew && !string.IsNullOrWhiteSpace(ResolveCode) && !string.IsNullOrWhiteSpace(ResolveNotes);

    private void NotifyCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        PostJournalCommand.NotifyCanExecuteChanged();
        ConfirmResolveCommand.NotifyCanExecuteChanged();
        NewRecordCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedChanged(TicketRow? value)
    {
        if (_suppressSelection || value is null)
            return;
        if (!IsNew && value.SysId == EditorSysId)
            return;
        if (IsDirty)
        {
            ShowUnsavedBanner = true;
            EditorMessage = "Save or discard unsaved changes before opening another record.";
            _suppressSelection = true;
            Selected = _boundRow;
            _suppressSelection = false;
            return;
        }

        ShowUnsavedBanner = false;
        QueueOpen(value.SysId);
    }

    partial void OnPresetChanged(PresetOption? value)
    {
        if (Client is null || Applying || value is null)
            return;
        _ = RefreshAsync();
    }

    partial void OnShortDescriptionChanged(string value) => Touch();
    partial void OnDescriptionChanged(string value) => Touch();

    partial void OnJournalTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasJournalText));
        if (!Applying)
            RecalculateDirty();
        PostJournalCommand.NotifyCanExecuteChanged();
    }

    partial void OnJournalKindChanged(JournalKind value) => JournalIsCustomerVisible = value == JournalKind.Comments;

    partial void OnIsDirtyChanged(bool value) => NotifyCommands();
    partial void OnIsEditorBusyChanged(bool value) => NotifyCommands();
    partial void OnIsReadyChanged(bool value) => NotifyCommands();
    partial void OnIsNewChanged(bool value) => NotifyCommands();
    partial void OnResolveCodeChanged(string value) => ConfirmResolveCommand.NotifyCanExecuteChanged();
    partial void OnResolveNotesChanged(string value) => ConfirmResolveCommand.NotifyCanExecuteChanged();
    partial void OnHasEditorChanged(bool value) => NotifyCommands();
}

public sealed record SavedTicketFields(string SysId, string ShortDescription, string StateLabel, string? AssigneeName, string? AssigneeId);
