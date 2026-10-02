using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ServiceNowDesk.Mapping;
using ServiceNowDesk.Models;
using ServiceNowDesk.Query;

namespace ServiceNowDesk.Client;

public sealed class ServiceNowClient : IServiceNowClient
{
    private const string IncidentFields = "sys_id,number,short_description,description,state,priority,impact,urgency,category,subcategory,contact_type,caller_id,assigned_to,assignment_group,opened_at,sys_updated_on,active,close_code,close_notes,hold_reason";
    private const string RequestFields = "sys_id,number,short_description,description,request_state,requested_for,opened_by,opened_at,due_date,priority,special_instructions,approval,stage,active,sys_updated_on";
    private const string ItemFields = "sys_id,number,short_description,description,state,stage,request,cat_item,quantity,assigned_to,assignment_group,opened_at,sys_updated_on,active,priority,close_notes";
    private const string KnowledgeFields = "sys_id,number,short_description,text,topic,workflow_state,kb_category,kb_knowledge_base,author,sys_updated_on,published";

    private readonly HttpClient _http;
    private readonly ServiceNowAuthMode _authMode;
    private readonly IFormCatalogStore? _catalog;
    private readonly FormCatalogSnapshot _snapshot = new();
    private readonly List<ApiActivity> _activity = [];
    private readonly object _activityGate = new();
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, IReadOnlyList<Choice>> _choices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<CatalogVariableDefinition>> _catalogForms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Choice>> _membersByGroup = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _groupsWithMemberList = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _persistGate = new();
    private List<Choice> _groups = [];
    private string[]? _groupIds;
    private Task? _directoryRefresh;

    private ServiceNowClient(HttpClient http, ServiceNowSession session, IFormCatalogStore? formCatalog)
    {
        _http = http;
        _authMode = session.AuthMode;
        _catalog = formCatalog;
        InstanceUri = session.InstanceUri;
        if (formCatalog is not null)
            ApplyCatalog(formCatalog.Load(session.InstanceUri));
    }

    public Uri? InstanceUri { get; }

    public IReadOnlyList<ApiActivity> RecentActivity
    {
        get
        {
            lock (_activityGate)
                return _activity.ToArray();
        }
    }

    public bool HasCachedChoices
    {
        get
        {
            lock (_cacheGate)
                return _snapshot.Choices.Any(list => list.Choices is { Count: > 0 });
        }
    }

    public bool FormCatalogIsStale
    {
        get
        {
            lock (_cacheGate)
                return FormCatalogPolicy.IsStale(_snapshot.CapturedAt, DateTimeOffset.UtcNow);
        }
    }

    public bool AssignmentDirectoryIsStale
    {
        get
        {
            lock (_cacheGate)
            {
                return !_snapshot.DirectoryComplete
                    || FormCatalogPolicy.IsStale(_snapshot.DirectoryCapturedAt, DateTimeOffset.UtcNow);
            }
        }
    }

    public static ServiceNowClient Create(ServiceNowSession session, HttpMessageHandler? handler = null, IFormCatalogStore? formCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var inner = handler ?? new HttpClientHandler();
        if (session.AuthMode == ServiceNowAuthMode.BrowserSession && inner is HttpClientHandler httpHandler)
            httpHandler.UseCookies = false;

        var auth = new ServiceNowAuthHandler(session)
        {
            InnerHandler = inner
        };
        var http = new HttpClient(auth)
        {
            BaseAddress = session.InstanceUri,
            Timeout = TimeSpan.FromSeconds(45)
        };
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ServiceNowDesk/1.0");
        return new ServiceNowClient(http, session, formCatalog);
    }

    public void Dispose() => _http.Dispose();

    public async Task<CurrentUser> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        var result = await GetListAsync(
            "sys_user",
            "sys_id,name,user_name,email",
            "user_name=javascript:gs.getUserName()",
            1,
            0,
            cancellationToken).ConfigureAwait(false);
        using (result)
        {
            var array = RequireArray(result.Document);
            if (array.GetArrayLength() == 0)
                throw new ServiceNowException(404, "Signed in, but ServiceNow did not return a user for this account.", null);

            var row = array[0];
            var name = SnowField.Read(row, "name").Display;
            var userName = SnowField.Read(row, "user_name").Display;
            return new CurrentUser(
                SnowField.Read(row, "sys_id").Value,
                string.IsNullOrWhiteSpace(name) ? userName : name,
                userName,
                SnowField.Read(row, "email").Display);
        }
    }

    public Task<PagedResult<IncidentRecord>> SearchIncidentsAsync(TicketQuery query, CancellationToken cancellationToken) =>
        SearchAsync("incident", IncidentFields, query, RecordMapper.Incident, cancellationToken);

    public Task<IncidentRecord> GetIncidentAsync(string sysId, CancellationToken cancellationToken) =>
        GetOneAsync("incident", sysId, IncidentFields, RecordMapper.Incident, cancellationToken);

    public Task<IncidentRecord> CreateIncidentAsync(IncidentChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (string.IsNullOrWhiteSpace(changes.ShortDescription))
            throw new ArgumentException("Enter a short description.");
        return WriteAsync(HttpMethod.Post, "incident", null, ChangeJson.FromIncident(changes), IncidentFields, RecordMapper.Incident, cancellationToken);
    }

    public Task<IncidentRecord> UpdateIncidentAsync(string sysId, IncidentChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (!changes.HasChanges)
            throw new InvalidOperationException("There is nothing to update.");
        return WriteAsync(HttpMethod.Patch, "incident", sysId, ChangeJson.FromIncident(changes), IncidentFields, RecordMapper.Incident, cancellationToken);
    }

    public Task<IncidentRecord> ResolveIncidentAsync(string sysId, string closeCode, string closeNotes, string resolvedState, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(closeCode))
            throw new ArgumentException("Choose a close code.");
        if (string.IsNullOrWhiteSpace(closeNotes))
            throw new ArgumentException("Enter close notes before resolving.");

        var state = string.IsNullOrWhiteSpace(resolvedState) ? "6" : resolvedState.Trim();
        var json = ChangeJson.Serialize(new Dictionary<string, string?>
        {
            ["state"] = state,
            ["close_code"] = closeCode.Trim(),
            ["close_notes"] = closeNotes.Trim()
        });
        return WriteAsync(HttpMethod.Patch, "incident", sysId, json, IncidentFields, RecordMapper.Incident, cancellationToken);
    }

    public Task<PagedResult<RequestRecord>> SearchRequestsAsync(TicketQuery query, CancellationToken cancellationToken) =>
        SearchAsync("sc_request", RequestFields, query, RecordMapper.Request, cancellationToken);

    public Task<RequestRecord> GetRequestAsync(string sysId, CancellationToken cancellationToken) =>
        GetOneAsync("sc_request", sysId, RequestFields, RecordMapper.Request, cancellationToken);

    public Task<RequestRecord> CreateRequestAsync(RequestChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (string.IsNullOrWhiteSpace(changes.ShortDescription))
            throw new ArgumentException("Enter a short description.");
        return WriteAsync(HttpMethod.Post, "sc_request", null, ChangeJson.FromRequest(changes), RequestFields, RecordMapper.Request, cancellationToken);
    }

    public Task<RequestRecord> UpdateRequestAsync(string sysId, RequestChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (!changes.HasChanges)
            throw new InvalidOperationException("There is nothing to update.");
        return WriteAsync(HttpMethod.Patch, "sc_request", sysId, ChangeJson.FromRequest(changes), RequestFields, RecordMapper.Request, cancellationToken);
    }

    public Task<RequestRecord> ResolveRequestAsync(string sysId, string requestState, string notes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestState))
            throw new ArgumentException("Choose how to close the request.");
        if (string.IsNullOrWhiteSpace(notes))
            throw new ArgumentException("Enter notes before closing the request.");

        var json = ChangeJson.Serialize(new Dictionary<string, string?>
        {
            ["request_state"] = requestState.Trim(),
            ["close_notes"] = notes.Trim(),
            ["work_notes"] = notes.Trim()
        });
        return WriteAsync(HttpMethod.Patch, "sc_request", sysId, json, RequestFields, RecordMapper.Request, cancellationToken);
    }

    public Task<PagedResult<RequestedItemRecord>> SearchRequestedItemsAsync(TicketQuery query, CancellationToken cancellationToken) =>
        SearchAsync("sc_req_item", ItemFields, query, RecordMapper.RequestedItem, cancellationToken);

    public Task<RequestedItemRecord> GetRequestedItemAsync(string sysId, CancellationToken cancellationToken) =>
        GetOneAsync("sc_req_item", sysId, ItemFields, RecordMapper.RequestedItem, cancellationToken);

    public Task<RequestedItemRecord> UpdateRequestedItemAsync(string sysId, RequestedItemChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (!changes.HasChanges)
            throw new InvalidOperationException("There is nothing to update.");
        return WriteAsync(HttpMethod.Patch, "sc_req_item", sysId, ChangeJson.FromRequestedItem(changes), ItemFields, RecordMapper.RequestedItem, cancellationToken);
    }

    public Task<RequestedItemRecord> ResolveRequestedItemAsync(string sysId, string state, string closeNotes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state))
            throw new ArgumentException("Choose how to close the requested item.");
        if (string.IsNullOrWhiteSpace(closeNotes))
            throw new ArgumentException("Enter close notes before closing the requested item.");

        var json = ChangeJson.Serialize(new Dictionary<string, string?>
        {
            ["state"] = state.Trim(),
            ["close_notes"] = closeNotes.Trim(),
            ["work_notes"] = closeNotes.Trim()
        });
        return WriteAsync(HttpMethod.Patch, "sc_req_item", sysId, json, ItemFields, RecordMapper.RequestedItem, cancellationToken);
    }

    public Task<PagedResult<KnowledgeArticle>> SearchKnowledgeAsync(TicketQuery query, CancellationToken cancellationToken) =>
        SearchAsync("kb_knowledge", KnowledgeFields, query, RecordMapper.Knowledge, cancellationToken);

    public Task<KnowledgeArticle> GetKnowledgeAsync(string sysId, CancellationToken cancellationToken) =>
        GetOneAsync("kb_knowledge", sysId, KnowledgeFields, RecordMapper.Knowledge, cancellationToken);

    public async Task AddJournalAsync(string table, string sysId, JournalKind kind, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Enter a note before posting.");

        var field = kind == JournalKind.Comments ? "comments" : "work_notes";
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { [field] = text.Trim() });
        var result = await SendAsync(
            HttpMethod.Patch,
            ItemUrl(table, sysId, "sys_id"),
            json,
            cancellationToken).ConfigureAwait(false);
        result.Dispose();
    }

    public async Task<IReadOnlyList<JournalEntry>> GetJournalAsync(string sysId, CancellationToken cancellationToken)
    {
        var id = EncodedQuery.SafeToken(sysId, "record id");
        var query = "element_id=" + id + "^ORDERBYDESCsys_created_on";
        var result = await GetListAsync(
            "sys_journal_field",
            "sys_id,element,value,sys_created_on,sys_created_by",
            query,
            40,
            0,
            cancellationToken).ConfigureAwait(false);
        using (result)
        {
            return RequireArray(result.Document).EnumerateArray().Select(RecordMapper.Journal).ToArray();
        }
    }

    public async Task<IReadOnlyList<Choice>> GetChoicesAsync(string table, string element, string? dependentValue, CancellationToken cancellationToken)
    {
        var key = ChoiceKey(table, element, dependentValue);
        lock (_cacheGate)
        {
            if (_choices.TryGetValue(key, out var cached))
                return cached;
        }

        var choices = await FetchChoiceListAsync(table, element, dependentValue, cancellationToken).ConfigureAwait(false);
        RememberChoices(table, element, dependentValue, choices, persist: true);
        return choices;
    }

    public async Task RefreshFormCatalogAsync(CancellationToken cancellationToken)
    {
        var saved = 0;
        ServiceNowException? last = null;
        try
        {
            (saved, last) = await DownloadChoiceCatalogAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (AssignmentDirectoryIsStale)
                {
                    await RefreshAssignmentDirectoryAsync(cancellationToken).ConfigureAwait(false);
                    lock (_cacheGate)
                    {
                        if (_groups.Count > 0)
                            saved++;
                    }
                }
            }
            catch (ServiceNowException ex) when (ex.StatusCode is not 401 and not 403)
            {
                last = ex;
            }

            if (saved == 0 && last is not null)
                throw last;

            lock (_cacheGate)
                _snapshot.CapturedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            PersistCatalog();
        }
    }

    public async Task RefreshChoiceCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (saved, last) = await DownloadChoiceCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (saved == 0 && last is not null)
                throw last;

            lock (_cacheGate)
                _snapshot.CapturedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            PersistCatalog();
        }
    }

    public Task RefreshAssignmentDirectoryAsync(CancellationToken cancellationToken)
    {
        lock (_cacheGate)
        {
            if (_directoryRefresh is { IsCompleted: false })
                return _directoryRefresh;

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _directoryRefresh = completion.Task;
            _ = FinishAssignmentDirectoryAsync(completion, cancellationToken);
            return completion.Task;
        }
    }

    public async Task<bool> RefreshAssignmentDirectoryIfStaleAsync(CancellationToken cancellationToken)
    {
        if (!AssignmentDirectoryIsStale)
            return false;

        await RefreshAssignmentDirectoryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<IReadOnlyList<ReferenceSuggestion>> SearchUsersAsync(string text, CancellationToken cancellationToken)
    {
        var term = EncodedQuery.Sanitize(text);
        if (term.Length < 2)
            return Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([]);

        var query = "active=true^nameLIKE" + term + "^ORactive=true^user_nameLIKE" + term + "^ORactive=true^emailLIKE" + term;
        return SearchReferencesAsync("sys_user", "sys_id,name,user_name,email", query, true, cancellationToken);
    }

    public async Task<IReadOnlyList<Choice>> ListAssignmentGroupsAsync(CancellationToken cancellationToken)
    {
        lock (_cacheGate)
        {
            if (_groups.Count > 0 || _directoryRefresh is { IsCompleted: false })
                return _groups.ToArray();
        }

        var groups = await FetchGroupsAsync(cancellationToken).ConfigureAwait(false);
        lock (_cacheGate)
        {
            _groups = groups;
            _snapshot.Groups = groups.Select(group => new CachedAssignmentGroup { SysId = group.Value, Name = group.Label }).ToList();
        }

        PersistCatalog();
        return groups;
    }

    public async Task<IReadOnlyList<Choice>> ListGroupMembersAsync(string groupSysId, CancellationToken cancellationToken)
    {
        var id = EncodedQuery.SafeToken(groupSysId, "group id");
        lock (_cacheGate)
        {
            if (_membersByGroup.TryGetValue(id, out var cached))
                return cached.ToArray();
            if (_groupsWithMemberList.Contains(id))
                return [];
        }

        var members = await FetchMembersAsync("group=" + EncodedQuery.Sanitize(id) + "^user.active=true", 200, cancellationToken).ConfigureAwait(false);
        var choices = members
            .Select(member => new Choice(member.UserSysId, member.Name))
            .OrderBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        RememberGroupMembers(id, choices);
        return choices;
    }

    public Task<IReadOnlyList<ReferenceSuggestion>> SearchGroupsAsync(string text, CancellationToken cancellationToken)
    {
        var term = EncodedQuery.Sanitize(text);
        if (term.Length < 2)
            return Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([]);

        var query = "active=true^nameLIKE" + term;
        return SearchReferencesAsync("sys_user_group", "sys_id,name,description", query, false, cancellationToken);
    }

    public async Task<IReadOnlyList<CatalogItemSummary>> SearchCatalogItemsAsync(string text, CancellationToken cancellationToken)
    {
        var term = EncodedQuery.Sanitize(text);
        if (term.Length < 2)
            return [];

        var url = "api/sn_sc/servicecatalog/items?sysparm_limit=20&sysparm_text=" + Uri.EscapeDataString(term);
        var result = await SendAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
        using (result)
        {
            if (!TryGetResult(result.Document, out var payload))
                return [];

            var items = new List<CatalogItemSummary>();
            var rows = payload.ValueKind == JsonValueKind.Array ? payload.EnumerateArray() : new[] { payload }.AsEnumerable();
            foreach (var row in rows)
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;
                var sysId = SnowField.Read(row, "sys_id").Value;
                var name = SnowField.Read(row, "name").Display;
                if (sysId.Length == 0 || name.Length == 0)
                    continue;
                items.Add(new CatalogItemSummary(sysId, name, SnowField.Read(row, "short_description").Display));
            }

            return items;
        }
    }

    public async Task<IReadOnlyList<CatalogVariableDefinition>> GetCatalogVariablesAsync(string itemSysId, CancellationToken cancellationToken)
    {
        var id = EncodedQuery.SafeToken(itemSysId, "catalog item id");
        lock (_cacheGate)
        {
            if (_catalogForms.TryGetValue(id, out var cached))
                return cached;
        }

        var definitions = await FetchCatalogVariablesAsync(id, cancellationToken).ConfigureAwait(false);
        RememberCatalog(id, definitions, persist: true);
        return definitions;
    }

    private async Task<IReadOnlyList<CatalogVariableDefinition>> FetchCatalogVariablesAsync(string id, CancellationToken cancellationToken)
    {
        var result = await SendAsync(HttpMethod.Get, "api/sn_sc/servicecatalog/items/" + Uri.EscapeDataString(id), null, cancellationToken).ConfigureAwait(false);
        using (result)
        {
            if (!TryGetResult(result.Document, out var payload) || payload.ValueKind != JsonValueKind.Object)
                return [];
            if (!payload.TryGetProperty("variables", out var variables) || variables.ValueKind != JsonValueKind.Array)
                return [];

            var definitions = new List<CatalogVariableDefinition>();
            foreach (var variable in variables.EnumerateArray())
            {
                var name = SnowField.Read(variable, "name").Value;
                if (name.Length == 0)
                    name = SnowField.Read(variable, "name").Display;
                var type = SnowField.Read(variable, "type").Value;
                if (type.Length == 0)
                    type = SnowField.Read(variable, "friendly_type").Display;
                if (name.Length == 0 || IsLayoutVariable(type))
                    continue;

                var label = SnowField.Read(variable, "label").Display;
                if (label.Length == 0)
                    label = name;
                var choices = ReadVariableChoices(variable);
                definitions.Add(new CatalogVariableDefinition(name, label, IsMandatory(variable), choices));
            }

            return definitions;
        }
    }

    public async Task<CatalogOrderResult> OrderCatalogItemAsync(
        string itemSysId,
        int quantity,
        string? requestedForSysId,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        var id = EncodedQuery.SafeToken(itemSysId, "catalog item id");
        var cleanVariables = new Dictionary<string, string>();
        foreach (var pair in variables)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;
            cleanVariables[pair.Key.Trim()] = pair.Value ?? "";
        }

        var payload = new Dictionary<string, object>
        {
            ["sysparm_quantity"] = Math.Clamp(quantity, 1, 50).ToString(CultureInfo.InvariantCulture),
            ["variables"] = cleanVariables
        };
        if (!string.IsNullOrWhiteSpace(requestedForSysId))
            payload["sysparm_requested_for"] = requestedForSysId.Trim();

        var result = await SendAsync(
            HttpMethod.Post,
            "api/sn_sc/servicecatalog/items/" + Uri.EscapeDataString(id) + "/order_now",
            JsonSerializer.Serialize(payload),
            cancellationToken).ConfigureAwait(false);
        using (result)
        {
            if (!TryGetResult(result.Document, out var body) || body.ValueKind != JsonValueKind.Object)
                throw new ServiceNowException(200, "ServiceNow ordered the item but did not return the request number.", null);

            var sysId = FirstValue(body, "request_id", "sys_id");
            var number = FirstValue(body, "request_number", "number");
            if (body.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.Object)
            {
                if (sysId.Length == 0)
                    sysId = FirstValue(request, "sys_id");
                if (number.Length == 0)
                    number = FirstValue(request, "number");
            }

            return new CatalogOrderResult(sysId, number);
        }
    }

    private async Task<PagedResult<T>> SearchAsync<T>(
        string table,
        string fields,
        TicketQuery query,
        Func<JsonElement, T> map,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var clause = await BuildClauseAsync(query, cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(query.Limit, 1, 100);
        var offset = Math.Max(0, query.Offset);
        var result = await GetListAsync(table, fields, clause, limit, offset, cancellationToken).ConfigureAwait(false);
        using (result)
        {
            var items = RequireArray(result.Document).EnumerateArray().Select(map).ToArray();
            return new PagedResult<T>(items, result.TotalCount);
        }
    }

    private async Task<string> BuildClauseAsync(TicketQuery query, CancellationToken cancellationToken)
    {
        var assignment = query.AssignmentClause;
        if (string.IsNullOrWhiteSpace(assignment))
        {
            assignment = query.Assignment switch
            {
                AssignmentScope.Mine => "assigned_to=javascript:gs.getUserID()",
                AssignmentScope.Unassigned => "assigned_toISEMPTY",
                AssignmentScope.MyGroups => await MyGroupsClauseAsync(cancellationToken).ConfigureAwait(false),
                _ => ""
            };
        }

        var extra = string.IsNullOrWhiteSpace(query.ParentRequestId)
            ? ""
            : "request=" + EncodedQuery.SafeToken(query.ParentRequestId, "request id");
        return EncodedQuery.Build(
            EncodedQuery.TextSearch(query.Text),
            assignment,
            EncodedQuery.ActivityClause(query.Activity),
            extra);
    }

    private async Task<string> MyGroupsClauseAsync(CancellationToken cancellationToken)
    {
        if (_groupIds is null)
        {
            var result = await GetListAsync(
                "sys_user_grmember",
                "group",
                "user=javascript:gs.getUserID()",
                50,
                0,
                cancellationToken).ConfigureAwait(false);
            using (result)
            {
                var ids = new List<string>();
                foreach (var row in RequireArray(result.Document).EnumerateArray())
                {
                    var group = SnowField.Read(row, "group").Value;
                    if (group.Length == 0)
                        continue;
                    ids.Add(EncodedQuery.SafeToken(group, "group id"));
                }

                _groupIds = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        if (_groupIds.Length == 0)
            return "sys_id=NO_GROUP_MEMBERSHIP";

        return "assignment_groupIN" + string.Join(",", _groupIds);
    }

    private async Task<T> GetOneAsync<T>(string table, string sysId, string fields, Func<JsonElement, T> map, CancellationToken cancellationToken)
    {
        var result = await SendAsync(HttpMethod.Get, ItemUrl(table, sysId, fields), null, cancellationToken).ConfigureAwait(false);
        using (result)
            return map(RequireObject(result.Document));
    }

    private async Task<T> WriteAsync<T>(
        HttpMethod method,
        string table,
        string? sysId,
        string json,
        string fields,
        Func<JsonElement, T> map,
        CancellationToken cancellationToken)
    {
        var url = string.IsNullOrEmpty(sysId)
            ? "api/now/table/" + table + "?sysparm_display_value=all&sysparm_exclude_reference_link=true&sysparm_fields=" + Uri.EscapeDataString(fields)
            : ItemUrl(table, sysId, fields);
        var result = await SendAsync(method, url, json, cancellationToken).ConfigureAwait(false);
        using (result)
            return map(RequireObject(result.Document));
    }

    private async Task<IReadOnlyList<Choice>> FetchChoicesAsync(string query, CancellationToken cancellationToken)
    {
        var result = await GetListAsync("sys_choice", "value,label,sequence", query, 200, 0, cancellationToken).ConfigureAwait(false);
        using (result)
        {
            var choices = new List<Choice>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in RequireArray(result.Document).EnumerateArray())
            {
                var value = SnowField.Read(row, "value").Value;
                if (value.Length == 0 || !seen.Add(value))
                    continue;
                var label = SnowField.Read(row, "label").Display;
                choices.Add(new Choice(value, label.Length == 0 ? value : label));
            }

            return choices;
        }
    }

    private async Task<IReadOnlyList<ReferenceSuggestion>> SearchReferencesAsync(
        string table,
        string fields,
        string query,
        bool user,
        CancellationToken cancellationToken)
    {
        var result = await GetListAsync(table, fields, query + "^ORDERBYname", 15, 0, cancellationToken).ConfigureAwait(false);
        using (result)
        {
            var suggestions = new List<ReferenceSuggestion>();
            foreach (var row in RequireArray(result.Document).EnumerateArray())
            {
                var sysId = SnowField.Read(row, "sys_id").Value;
                var name = SnowField.Read(row, "name").Display;
                if (sysId.Length == 0 || name.Length == 0)
                    continue;
                var detail = user
                    ? JoinDetail(SnowField.Read(row, "user_name").Display, SnowField.Read(row, "email").Display)
                    : SnowField.Read(row, "description").Display;
                suggestions.Add(new ReferenceSuggestion(sysId, name, detail));
            }

            return suggestions;
        }
    }

    private async Task<ApiPayload> GetListAsync(string table, string fields, string query, int limit, int offset, CancellationToken cancellationToken)
    {
        var url = "api/now/table/" + table
            + "?sysparm_display_value=all&sysparm_exclude_reference_link=true"
            + "&sysparm_fields=" + Uri.EscapeDataString(fields)
            + "&sysparm_limit=" + limit
            + "&sysparm_offset=" + offset
            + "&sysparm_query=" + Uri.EscapeDataString(query);
        return await SendAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApiPayload> SendAsync(HttpMethod method, string relativeUrl, string? json, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUrl);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var watch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Record(method.Method, relativeUrl, 0, watch.ElapsedMilliseconds);
            throw new ServiceNowException(0, "The ServiceNow instance did not respond in time.", null);
        }
        catch (HttpRequestException exception)
        {
            Record(method.Method, relativeUrl, 0, watch.ElapsedMilliseconds);
            throw new ServiceNowException(0, "Could not reach the ServiceNow instance. Check the URL and your network.", exception.Message);
        }

        using (response)
        {
            Record(method.Method, relativeUrl, (int)response.StatusCode, watch.ElapsedMilliseconds);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw DescribeFailure((int)response.StatusCode, body);

            if (string.IsNullOrWhiteSpace(body))
                return new ApiPayload(JsonDocument.Parse("""{"result":{}}"""), ReadTotal(response));

            if (body.TrimStart().StartsWith('<'))
            {
                throw new ServiceNowException(
                    (int)response.StatusCode,
                    "The instance returned a web page instead of API data. Check the URL, and confirm the Table API is enabled for this user.",
                    null);
            }

            try
            {
                var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("status", out var status)
                    && status.ValueKind == JsonValueKind.String
                    && string.Equals(status.GetString(), "failure", StringComparison.OrdinalIgnoreCase))
                {
                    var error = ServiceNowException.FromResponse((int)response.StatusCode, body);
                    document.Dispose();
                    throw error;
                }

                return new ApiPayload(document, ReadTotal(response));
            }
            catch (JsonException)
            {
                throw new ServiceNowException((int)response.StatusCode, "ServiceNow returned a response that was not JSON.", null);
            }
        }
    }

    private ServiceNowException DescribeFailure(int statusCode, string body)
    {
        var error = ServiceNowException.FromResponse(statusCode, body);
        if (_authMode == ServiceNowAuthMode.Basic
            && statusCode == 401
            && error.Message.Contains("Auth information", StringComparison.OrdinalIgnoreCase))
        {
            return new ServiceNowException(
                401,
                "ServiceNow refused the user name and password. This instance expects company single sign-on. Choose Browser sign-in (SSO).",
                error.Detail);
        }

        if (_authMode == ServiceNowAuthMode.BrowserSession && statusCode is 401 or 403)
        {
            return new ServiceNowException(
                statusCode,
                "The browser sign-in expired or was rejected. Open Connection and sign in with the browser again.",
                error.Detail);
        }

        return error;
    }

    private async Task<IReadOnlyList<Choice>> FetchChoiceListAsync(string table, string element, string? dependentValue, CancellationToken cancellationToken)
    {
        var filter = "name=" + EncodedQuery.Sanitize(table) + "^element=" + EncodedQuery.Sanitize(element) + "^inactive=false";
        if (!string.IsNullOrWhiteSpace(dependentValue))
            filter += "^dependent_value=" + EncodedQuery.Sanitize(dependentValue);

        var choices = await FetchChoicesAsync(filter + "^language=en^ORDERBYsequence", cancellationToken).ConfigureAwait(false);
        if (choices.Count == 0)
            choices = await FetchChoicesAsync(filter + "^ORDERBYsequence", cancellationToken).ConfigureAwait(false);
        return choices;
    }

    private void ApplyCatalog(FormCatalogSnapshot? snapshot)
    {
        if (snapshot is null)
            return;

        lock (_cacheGate)
        {
            _snapshot.CapturedAt = snapshot.CapturedAt;
            _snapshot.DirectoryCapturedAt = snapshot.DirectoryCapturedAt;
            _snapshot.Choices = snapshot.Choices ?? [];
            _snapshot.CatalogItems = snapshot.CatalogItems ?? [];
            _snapshot.DirectoryComplete = snapshot.DirectoryComplete;
            _snapshot.Groups = snapshot.Groups ?? [];
            _snapshot.Members = snapshot.Members ?? [];
            _groups = _snapshot.Groups
                .Where(group => !string.IsNullOrWhiteSpace(group.SysId) && !string.IsNullOrWhiteSpace(group.Name))
                .Select(group => new Choice(group.SysId, group.Name))
                .ToList();
            _membersByGroup.Clear();
            _groupsWithMemberList.Clear();
            foreach (var member in _snapshot.Members)
            {
                if (string.IsNullOrWhiteSpace(member.GroupSysId) || string.IsNullOrWhiteSpace(member.UserSysId))
                    continue;
                if (!_membersByGroup.TryGetValue(member.GroupSysId, out var list))
                {
                    list = [];
                    _membersByGroup[member.GroupSysId] = list;
                }

                if (list.Any(choice => choice.Value.Equals(member.UserSysId, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var name = string.IsNullOrWhiteSpace(member.Name) ? member.UserSysId : member.Name;
                list.Add(new Choice(member.UserSysId, name));
            }

            foreach (var list in _membersByGroup.Values)
                list.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));

            if (_snapshot.DirectoryComplete)
            {
                foreach (var group in _groups)
                    _groupsWithMemberList.Add(group.Value);
            }
            foreach (var list in _snapshot.Choices)
            {
                if (list.Choices is not { Count: > 0 } || string.IsNullOrWhiteSpace(list.Table) || string.IsNullOrWhiteSpace(list.Element))
                    continue;
                _choices[ChoiceKey(list.Table, list.Element, list.DependentValue)] = list.Choices;
            }

            foreach (var item in _snapshot.CatalogItems)
            {
                if (string.IsNullOrWhiteSpace(item.SysId) || item.Variables is not { Count: > 0 })
                    continue;
                _catalogForms[item.SysId] = item.Variables;
            }
        }
    }

    private bool RememberChoices(string table, string element, string? dependentValue, IReadOnlyList<Choice> choices, bool persist)
    {
        if (choices.Count == 0)
            return false;

        var dependent = dependentValue ?? "";
        lock (_cacheGate)
        {
            _choices[ChoiceKey(table, element, dependent)] = choices;
            var list = _snapshot.Choices.FirstOrDefault(item =>
                item.Table.Equals(table, StringComparison.OrdinalIgnoreCase)
                && item.Element.Equals(element, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.DependentValue ?? "", dependent, StringComparison.Ordinal));
            if (list is null)
            {
                list = new CachedChoiceList { Table = table, Element = element, DependentValue = dependent };
                _snapshot.Choices.Add(list);
            }

            list.Choices = choices.ToList();
        }

        if (persist)
            PersistCatalog();
        return true;
    }

    private bool RememberCatalog(string itemSysId, IReadOnlyList<CatalogVariableDefinition> variables, bool persist)
    {
        if (variables.Count == 0)
            return false;

        lock (_cacheGate)
        {
            _catalogForms[itemSysId] = variables;
            var item = _snapshot.CatalogItems.FirstOrDefault(entry => entry.SysId.Equals(itemSysId, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = new CachedCatalogForm { SysId = itemSysId };
                _snapshot.CatalogItems.Add(item);
            }

            item.CapturedAt = DateTimeOffset.UtcNow;
            item.Variables = variables.ToList();
        }

        if (persist)
            PersistCatalog();
        return true;
    }

    private void PersistCatalog()
    {
        if (_catalog is null || InstanceUri is null)
            return;

        lock (_persistGate)
        {
            FormCatalogSnapshot copy;
            lock (_cacheGate)
            {
                copy = new FormCatalogSnapshot
                {
                    CapturedAt = _snapshot.CapturedAt,
                    DirectoryCapturedAt = _snapshot.DirectoryCapturedAt,
                    Choices = _snapshot.Choices.Select(list => new CachedChoiceList
                    {
                        Table = list.Table,
                        Element = list.Element,
                        DependentValue = list.DependentValue,
                        Choices = list.Choices.Select(choice => new Choice(choice.Value, choice.Label)).ToList()
                    }).ToList(),
                    CatalogItems = _snapshot.CatalogItems.Select(item => new CachedCatalogForm
                    {
                        SysId = item.SysId,
                        CapturedAt = item.CapturedAt,
                        Variables = item.Variables.Select(variable => new CatalogVariableDefinition(
                            variable.Name,
                            variable.Label,
                            variable.Mandatory,
                            variable.Choices.Select(choice => new Choice(choice.Value, choice.Label)).ToArray())).ToList()
                    }).ToList(),
                    DirectoryComplete = _snapshot.DirectoryComplete,
                    Groups = _snapshot.Groups.Select(group => new CachedAssignmentGroup { SysId = group.SysId, Name = group.Name }).ToList(),
                    Members = _snapshot.Members.Select(member => new CachedGroupMember
                    {
                        GroupSysId = member.GroupSysId,
                        UserSysId = member.UserSysId,
                        Name = member.Name
                    }).ToList()
                };
            }

            _catalog.Save(InstanceUri, copy);
        }
    }

    private async Task<(int Saved, ServiceNowException? Error)> DownloadChoiceCatalogAsync(CancellationToken cancellationToken)
    {
        ServiceNowException? last = null;
        var saved = 0;
        foreach (var field in FormCatalogFields.Independent)
        {
            try
            {
                var choices = await FetchChoiceListAsync(field.Table, field.Element, null, cancellationToken).ConfigureAwait(false);
                if (RememberChoices(field.Table, field.Element, null, choices, persist: false))
                    saved++;
            }
            catch (ServiceNowException ex) when (ex.StatusCode is not 401 and not 403)
            {
                last = ex;
            }
        }

        IReadOnlyList<Choice> categories;
        lock (_cacheGate)
            categories = _choices.TryGetValue(ChoiceKey("incident", "category", null), out var cached) ? cached : [];

        var dependents = 0;
        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category.Value) || dependents >= FormCatalogPolicy.MaxDependentCategories)
                continue;
            dependents++;
            try
            {
                var choices = await FetchChoiceListAsync("incident", "subcategory", category.Value, cancellationToken).ConfigureAwait(false);
                if (RememberChoices("incident", "subcategory", category.Value, choices, persist: false))
                    saved++;
            }
            catch (ServiceNowException ex) when (ex.StatusCode is not 401 and not 403)
            {
                last = ex;
            }
        }

        string[] itemIds;
        lock (_cacheGate)
            itemIds = _catalogForms.Keys.Take(FormCatalogPolicy.MaxCatalogItems).ToArray();

        foreach (var itemId in itemIds)
        {
            try
            {
                var variables = await FetchCatalogVariablesAsync(itemId, cancellationToken).ConfigureAwait(false);
                if (RememberCatalog(itemId, variables, persist: false))
                    saved++;
            }
            catch (ServiceNowException ex) when (ex.StatusCode is not 401 and not 403)
            {
                last = ex;
            }
        }

        return (saved, last);
    }

    private async Task FinishAssignmentDirectoryAsync(TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAssignmentDirectoryCoreAsync(cancellationToken).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private async Task RefreshAssignmentDirectoryCoreAsync(CancellationToken cancellationToken)
    {
        var groups = await FetchGroupsAsync(cancellationToken).ConfigureAwait(false);
        var members = await FetchMembersAsync("user.active=true^group.active=true", FormCatalogPolicy.MaxGroupMembers, cancellationToken).ConfigureAwait(false);
        ReplaceDirectory(groups, members);
        PersistCatalog();
    }

    private async Task<List<Choice>> FetchGroupsAsync(CancellationToken cancellationToken)
    {
        var groups = new List<Choice>();
        await PageRowsAsync(
            "sys_user_group",
            "sys_id,name",
            "active=true^ORDERBYname",
            FormCatalogPolicy.MaxAssignmentGroups,
            row =>
            {
                var id = SnowField.Read(row, "sys_id").Value;
                var name = SnowField.Read(row, "name").Display;
                if (id.Length == 0 || name.Length == 0 || groups.Any(group => group.Value.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    return;
                groups.Add(new Choice(id, name));
            },
            cancellationToken).ConfigureAwait(false);
        return groups.OrderBy(group => group.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<CachedGroupMember>> FetchMembersAsync(string query, int max, CancellationToken cancellationToken)
    {
        var members = new List<CachedGroupMember>();
        await PageRowsAsync(
            "sys_user_grmember",
            "group,user",
            query,
            max,
            row =>
            {
                var group = SnowField.Read(row, "group");
                var user = SnowField.Read(row, "user");
                if (group.Value.Length == 0 || user.Value.Length == 0)
                    return;
                if (members.Any(member => member.GroupSysId.Equals(group.Value, StringComparison.OrdinalIgnoreCase) && member.UserSysId.Equals(user.Value, StringComparison.OrdinalIgnoreCase)))
                    return;
                var name = user.Display.Length > 0 ? user.Display : user.Value;
                members.Add(new CachedGroupMember { GroupSysId = group.Value, UserSysId = user.Value, Name = name });
            },
            cancellationToken).ConfigureAwait(false);
        return members;
    }

    private async Task PageRowsAsync(string table, string fields, string query, int maxRows, Action<JsonElement> accept, CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var offset = 0;
        var kept = 0;
        while (kept < maxRows)
        {
            var limit = Math.Min(pageSize, maxRows - kept);
            var result = await GetListAsync(table, fields, query, limit, offset, cancellationToken).ConfigureAwait(false);
            using (result)
            {
                var rows = RequireArray(result.Document);
                var count = rows.GetArrayLength();
                foreach (var row in rows.EnumerateArray())
                {
                    accept(row);
                    kept++;
                    if (kept >= maxRows)
                        break;
                }

                if (count < limit)
                    break;
                offset += count;
            }
        }
    }

    private void ReplaceDirectory(IReadOnlyList<Choice> groups, IReadOnlyList<CachedGroupMember> members)
    {
        lock (_cacheGate)
        {
            _groups = groups.ToList();
            _membersByGroup.Clear();
            _groupsWithMemberList.Clear();
            foreach (var group in _groups)
                _groupsWithMemberList.Add(group.Value);

            foreach (var member in members)
            {
                if (!_membersByGroup.TryGetValue(member.GroupSysId, out var list))
                {
                    list = [];
                    _membersByGroup[member.GroupSysId] = list;
                }

                if (list.Any(choice => choice.Value.Equals(member.UserSysId, StringComparison.OrdinalIgnoreCase)))
                    continue;
                list.Add(new Choice(member.UserSysId, member.Name));
            }

            foreach (var list in _membersByGroup.Values)
                list.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));

            _snapshot.DirectoryComplete = true;
            _snapshot.DirectoryCapturedAt = DateTimeOffset.UtcNow;
            _snapshot.Groups = _groups.Select(group => new CachedAssignmentGroup { SysId = group.Value, Name = group.Label }).ToList();
            _snapshot.Members = [];
            foreach (var pair in _membersByGroup)
            {
                foreach (var member in pair.Value)
                    _snapshot.Members.Add(new CachedGroupMember { GroupSysId = pair.Key, UserSysId = member.Value, Name = member.Label });
            }
        }
    }

    private void RememberGroupMembers(string groupId, IReadOnlyList<Choice> members)
    {
        lock (_cacheGate)
        {
            var list = members.OrderBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase).ToList();
            _membersByGroup[groupId] = list;
            _groupsWithMemberList.Add(groupId);
            _snapshot.Members.RemoveAll(member => member.GroupSysId.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            foreach (var member in list)
                _snapshot.Members.Add(new CachedGroupMember { GroupSysId = groupId, UserSysId = member.Value, Name = member.Label });
        }

        PersistCatalog();
    }

    private static string ChoiceKey(string table, string element, string? dependentValue) =>
        table + "|" + element + "|" + (dependentValue ?? "");

    private static string ItemUrl(string table, string sysId, string fields)
    {
        var id = EncodedQuery.SafeToken(sysId, "record id");
        return "api/now/table/" + table + "/" + Uri.EscapeDataString(id)
            + "?sysparm_display_value=all&sysparm_exclude_reference_link=true&sysparm_fields=" + Uri.EscapeDataString(fields);
    }

    private static JsonElement RequireObject(JsonDocument document)
    {
        if (!TryGetResult(document, out var result) || result.ValueKind != JsonValueKind.Object)
            throw new ServiceNowException(200, "ServiceNow response did not include the record.", null);
        return result;
    }

    private static JsonElement RequireArray(JsonDocument document)
    {
        if (!TryGetResult(document, out var result))
            throw new ServiceNowException(200, "ServiceNow response did not include a result.", null);
        if (result.ValueKind != JsonValueKind.Array)
            throw new ServiceNowException(200, "ServiceNow response did not include a list.", null);
        return result;
    }

    private static bool TryGetResult(JsonDocument document, out JsonElement result) =>
        document.RootElement.TryGetProperty("result", out result);

    private static int? ReadTotal(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Total-Count", out var values) && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            return count;
        return null;
    }

    private static string JoinDetail(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return right;
        if (string.IsNullOrWhiteSpace(right))
            return left;
        return left + " · " + right;
    }

    private static bool IsLayoutVariable(string type)
    {
        var normalized = type.Trim().ToLowerInvariant();
        return normalized is "container" or "label" or "break" or "container start" or "container end" or "macro";
    }

    private static bool IsMandatory(JsonElement variable)
    {
        if (!variable.TryGetProperty("mandatory", out var mandatory))
            return false;
        if (mandatory.ValueKind == JsonValueKind.True)
            return true;
        var text = SnowField.AsString(mandatory);
        return text.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<Choice> ReadVariableChoices(JsonElement variable)
    {
        if (!variable.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<Choice>();
        foreach (var choice in choices.EnumerateArray())
        {
            var value = SnowField.Read(choice, "value").Value;
            if (value.Length == 0)
                value = SnowField.Read(choice, "value").Display;
            var label = SnowField.Read(choice, "label").Display;
            if (label.Length == 0)
                label = SnowField.Read(choice, "text").Display;
            if (value.Length == 0)
                continue;
            list.Add(new Choice(value, label.Length == 0 ? value : label));
        }

        return list;
    }

    private static string FirstValue(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var field = SnowField.Read(element, name);
            if (field.Value.Length > 0)
                return field.Value;
            if (field.Display.Length > 0)
                return field.Display;
        }

        return "";
    }

    private void Record(string method, string relativeUrl, int statusCode, long elapsedMilliseconds)
    {
        var path = relativeUrl;
        var queryIndex = path.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
            path = path[..queryIndex];

        var entry = new ApiActivity(DateTimeOffset.Now, method, path, statusCode, elapsedMilliseconds);
        lock (_activityGate)
        {
            _activity.Insert(0, entry);
            if (_activity.Count > 30)
                _activity.RemoveAt(_activity.Count - 1);
        }
    }

    private sealed class ApiPayload : IDisposable
    {
        public ApiPayload(JsonDocument document, int? totalCount)
        {
            Document = document;
            TotalCount = totalCount;
        }

        public JsonDocument Document { get; }
        public int? TotalCount { get; }
        public void Dispose() => Document.Dispose();
    }
}

internal sealed class ServiceNowAuthHandler : DelegatingHandler
{
    private readonly ServiceNowSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiry;

    public ServiceNowAuthHandler(ServiceNowSession session)
    {
        _session = session;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_session.AuthMode == ServiceNowAuthMode.BrowserSession)
        {
            request.Headers.Authorization = null;
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", _session.SessionCookie);
            request.Headers.Remove("X-UserToken");
            request.Headers.TryAddWithoutValidation("X-UserToken", _session.UserToken);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (_session.AuthMode == ServiceNowAuthMode.Basic)
        {
            var raw = Encoding.UTF8.GetBytes(_session.Username + ":" + _session.Password);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(false, cancellationToken).ConfigureAwait(false));
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        var retry = await CloneAsync(request, cancellationToken).ConfigureAwait(false);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(true, cancellationToken).ConfigureAwait(false));
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetTokenAsync(bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && _token is not null && DateTimeOffset.UtcNow < _expiry)
                return _token;

            var form = new Dictionary<string, string>
            {
                ["client_id"] = _session.ClientId,
                ["client_secret"] = _session.ClientSecret
            };
            if (_session.AuthMode == ServiceNowAuthMode.OAuthClientCredentials)
            {
                form["grant_type"] = "client_credentials";
            }
            else
            {
                form["grant_type"] = "password";
                form["username"] = _session.Username;
                form["password"] = _session.Password;
            }

            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(_session.InstanceUri, "oauth_token.do"))
            {
                Content = new FormUrlEncodedContent(form)
            };
            using var tokenResponse = await base.SendAsync(tokenRequest, cancellationToken).ConfigureAwait(false);
            var body = await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!tokenResponse.IsSuccessStatusCode)
                throw ServiceNowException.FromResponse((int)tokenResponse.StatusCode, body, "ServiceNow rejected the OAuth sign-in.");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new ServiceNowException((int)tokenResponse.StatusCode, "ServiceNow did not return an OAuth token.", null);
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty("access_token", out var tokenElement))
                    throw new ServiceNowException((int)tokenResponse.StatusCode, "ServiceNow did not return an access token.", null);

                var token = tokenElement.GetString() ?? "";
                if (token.Length == 0)
                    throw new ServiceNowException((int)tokenResponse.StatusCode, "ServiceNow returned an empty access token.", null);

                _token = token;
                var seconds = ReadExpires(document.RootElement);
                _expiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, seconds - 60));
                return token;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static int ReadExpires(JsonElement root)
    {
        if (!root.TryGetProperty("expires_in", out var expires))
            return 1800;
        if (expires.TryGetInt32(out var seconds))
            return seconds;
        return expires.ValueKind == JsonValueKind.String && int.TryParse(expires.GetString(), out seconds) ? seconds : 1800;
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                continue;
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is null)
            return clone;

        var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var content = new ByteArrayContent(bytes);
        foreach (var header in request.Content.Headers)
            content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        clone.Content = content;
        return clone;
    }
}
