using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Mapping;
using ServiceNowDesk.Models;
using ServiceNowDesk.Query;

namespace ServiceNowDesk.Client;

public sealed partial class ServiceNowClient : IServiceNowClient
{
    private const string IncidentFields = "sys_id,number,short_description,description,state,priority,impact,urgency,category,subcategory,contact_type,caller_id,assigned_to,assignment_group,service_offering,cmdb_ci,location,opened_at,sys_updated_on,active,close_code,close_notes,hold_reason,follow_up";
    private const string HardwareFields = "sys_id,serial_number,display_name,model,model_category,assigned_to,location,install_status,substatus,stockroom,comments";
    private const string RequestFields = "sys_id,number,short_description,description,request_state,requested_for,opened_by,opened_at,due_date,priority,special_instructions,approval,stage,active,sys_updated_on";
    private const string ItemFields = "sys_id,number,short_description,description,state,stage,request,cat_item,quantity,assigned_to,assignment_group,service_offering,cmdb_ci,location,opened_at,sys_updated_on,active,priority,close_notes,hold_reason,follow_up";
    private const string KnowledgeFields = "sys_id,number,short_description,text,topic,workflow_state,kb_category,kb_knowledge_base,author,sys_updated_on,published";
    private const string KnowledgeListFields = "sys_id,number,short_description,topic,workflow_state,kb_category,kb_knowledge_base,author,sys_updated_on,published";
    private const string AlertIncidentFields = "sys_id,number,short_description,state,assigned_to,assignment_group,location,sys_updated_on,active";
    private const string AlertRequestFields = "sys_id,number,short_description,request_state,assigned_to,assignment_group,sys_updated_on,active";
    private const string AlertItemFields = "sys_id,number,short_description,state,assigned_to,assignment_group,location,sys_updated_on,active";
    private const string PopulationIncidentFields = "sys_id,number,short_description,state,priority,assigned_to,assigned_to.user_name,assignment_group,location,sys_updated_on,sys_updated_by,active,caller_id,caller_id.user_name,follow_up";
    private const string UnassignedQueueFields = PopulationIncidentFields + ",opened_at";
    private const string PopulationItemFields = "sys_id,number,short_description,state,priority,assigned_to,assigned_to.user_name,assignment_group,location,sys_updated_on,sys_updated_by,active,requested_for,requested_for.user_name,request.requested_for,request.requested_for.user_name,follow_up";
    private const string PopulationInteractionFields = "sys_id,number,short_description,state,priority,assigned_to,assigned_to.user_name,assignment_group,location,sys_updated_on,sys_updated_by,active,opened_for,opened_for.user_name,follow_up";
    private const string PopulationInteractionFieldsWithoutFollowUp = "sys_id,number,short_description,state,priority,assigned_to,assigned_to.user_name,assignment_group,location,sys_updated_on,sys_updated_by,active,opened_for,opened_for.user_name";
    private const int AlertLimit = 100;
    private const string InteractionFields = "sys_id,number,short_description,description,state,type,opened_for,assigned_to,assignment_group,location,opened_at,sys_updated_on,active";

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
    private readonly HashSet<string> _completeMemberGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _persistGate = new();
    private List<Choice> _groups = [];
    private List<Choice> _serviceOfferings = [];
    private List<Choice> _configurationItems = [];
    private bool _configurationItemsReady;
    private string[]? _groupIds;
    private string? _signedInUserSysId;
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

    public event EventHandler? BrowserSessionRejected;

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

    public bool ServiceOfferingsAreStale
    {
        get
        {
            lock (_cacheGate)
                return FormCatalogPolicy.IsStale(_snapshot.ServiceOfferingsCapturedAt, DateTimeOffset.UtcNow);
        }
    }

    public bool ConfigurationItemsAreStale
    {
        get
        {
            lock (_cacheGate)
                return FormCatalogPolicy.IsStale(_snapshot.ConfigurationItemsCapturedAt, DateTimeOffset.UtcNow);
        }
    }

    public FormCatalogSnapshot ExportCatalog()
    {
        lock (_cacheGate)
            return CopySnapshot();
    }

    public void ClearChoiceCache()
    {
        lock (_cacheGate)
        {
            _choices.Clear();
            _snapshot.Choices.Clear();
            _snapshot.CapturedAt = default;
        }

        PersistCatalog();
    }

    public void ClearAssignmentGroupCache()
    {
        lock (_cacheGate)
        {
            _groups = [];
            _groupIds = null;
            _snapshot.Groups = [];
        }

        PersistCatalog();
    }

    /// <summary>
    /// Drops the in-memory My Groups id list so the next list/alert query reloads
    /// <c>sys_user_grmember</c> for the signed-in user. Does not touch the form catalog.
    /// </summary>
    public void ClearMyGroupMembershipCache()
    {
        lock (_cacheGate)
            _groupIds = null;
    }

    public void ClearServiceOfferingCache()
    {
        lock (_cacheGate)
        {
            _serviceOfferings = [];
            _snapshot.ServiceOfferings = [];
            _snapshot.ServiceOfferingsCapturedAt = default;
            _snapshot.ServiceOfferingsTruncated = false;
        }

        PersistCatalog();
    }

    public void ClearConfigurationItemCache()
    {
        lock (_cacheGate)
        {
            _configurationItems = [];
            _configurationItemsReady = true;
            _snapshot.ConfigurationItems = [];
            _snapshot.ConfigurationItemsCapturedAt = default;
            _snapshot.ConfigurationItemsTruncated = false;
        }

        PersistCatalog();
    }

    public void ClearAssignmentMemberCache()
    {
        lock (_cacheGate)
        {
            _membersByGroup.Clear();
            _groupsWithMemberList.Clear();
            _completeMemberGroups.Clear();
            _groupIds = null;
            _snapshot.Members = [];
            _snapshot.DirectoryComplete = false;
            _snapshot.MembersVerified = false;
            _snapshot.VerifiedMemberGroups = [];
            _snapshot.DirectoryCapturedAt = default;
        }

        PersistCatalog();
    }

    public void RestoreCatalog(FormCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_cacheGate)
        {
            _choices.Clear();
            _catalogForms.Clear();
            _membersByGroup.Clear();
            _groupsWithMemberList.Clear();
            _completeMemberGroups.Clear();
            _groups = [];
            _serviceOfferings = [];
            _configurationItems = [];
            _configurationItemsReady = false;
            _groupIds = null;
            _signedInUserSysId = null;
            _snapshot.Choices = [];
            _snapshot.CatalogItems = [];
            _snapshot.Groups = [];
            _snapshot.Members = [];
            _snapshot.ServiceOfferings = [];
            _snapshot.ConfigurationItems = [];
            _snapshot.ServiceOfferingsCapturedAt = default;
            _snapshot.ConfigurationItemsCapturedAt = default;
            _snapshot.ServiceOfferingsTruncated = false;
            _snapshot.ConfigurationItemsTruncated = false;
            _snapshot.CapturedAt = default;
            _snapshot.DirectoryCapturedAt = default;
            _snapshot.DirectoryComplete = false;
            _snapshot.MembersVerified = false;
            _snapshot.VerifiedMemberGroups = [];
        }

        ApplyCatalog(snapshot);
        PersistCatalog();
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
            "sys_id,name,user_name,email,location",
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
            var user = new CurrentUser(
                SnowField.Read(row, "sys_id").Value,
                string.IsNullOrWhiteSpace(name) ? userName : name,
                userName,
                SnowField.Read(row, "email").Display)
            {
                Location = SnowField.Read(row, "location").Display
            };
            lock (_cacheGate)
            {
                if (_signedInUserSysId is not null
                    && !_signedInUserSysId.Equals(user.SysId, StringComparison.OrdinalIgnoreCase))
                    _groupIds = null;
                _signedInUserSysId = user.SysId;
            }

            return user;
        }
    }

    public async Task<AlertSnapshot> GetOpenAlertsAsync(AlertSearch search, CancellationToken cancellationToken) =>
        (await GetAlertReportAsync(search, cancellationToken).ConfigureAwait(false)).Personal;

    public async Task<AlertReport> GetAlertReportAsync(AlertSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var incidents = await QueryAlertsAsync("incident", AlertIncidentFields, AlertQueryBuilder.AssignedToMe(search.UserSysId, DeskSection.Incidents, search.Locations), AlertKind.AssignedToMe, DeskSection.Incidents, includeLocation: true, cancellationToken).ConfigureAwait(false);
        // Requests have no location field; keep assignee-only. Incidents / items use offices.
        var requests = await QueryAlertsAsync("sc_request", AlertRequestFields, AlertQueryBuilder.AssignedToMe(search.UserSysId, DeskSection.Requests), AlertKind.AssignedToMe, DeskSection.Requests, includeLocation: false, cancellationToken).ConfigureAwait(false);
        var items = await QueryAlertsAsync("sc_req_item", AlertItemFields, AlertQueryBuilder.AssignedToMe(search.UserSysId, DeskSection.RequestedItems, search.Locations), AlertKind.AssignedToMe, DeskSection.RequestedItems, includeLocation: true, cancellationToken).ConfigureAwait(false);

        var groupQuery = AlertQueryBuilder.WatchedGroup(search.GroupName, search.Locations);
        var group = groupQuery is null
            ? AlertBucket.Empty
            : await QueryAlertsAsync("incident", AlertIncidentFields, groupQuery, AlertKind.WatchedGroup, DeskSection.Incidents, includeLocation: true, cancellationToken).ConfigureAwait(false);

        var assignedRows = incidents.Rows.Concat(requests.Rows).Concat(items.Rows).ToArray();
        var assignedTotal = incidents.TotalCount + requests.TotalCount + items.TotalCount;
        assignedRows = (await AttachAssignmentTimesAsync(search.UserSysId, assignedRows, cancellationToken).ConfigureAwait(false)).ToArray();
        var categories = await LoadCategoryBucketsAsync(search, cancellationToken).ConfigureAwait(false);
        var personal = new AlertSnapshot(new Dictionary<AlertKind, AlertBucket>
        {
            [AlertKind.AssignedToMe] = new(assignedRows, assignedTotal),
            [AlertKind.WatchedGroup] = group,
            [AlertKind.SlaBreaching] = categories.Personal.Sla,
            [AlertKind.OnHoldPastFollowUp] = categories.Personal.OnHold,
            [AlertKind.UpdatedByCaller] = categories.Personal.UpdatedByCaller,
            [AlertKind.ReturnedWithNotes] = categories.Personal.Returned,
            [AlertKind.Unattended] = categories.Personal.Unattended
        });
        return new AlertReport(personal, categories.Leads, categories.Daily);
    }

    public async Task<IReadOnlyList<WatchedRecord>> ListUnassignedGroupQueueAsync(string? watchedGroupName, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> groupIds;
        try
        {
            groupIds = await MemberGroupIdsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceNowException)
        {
            groupIds = [];
        }

        var query = AlertQueryBuilder.UnassignedInGroups(groupIds, watchedGroupName);
        if (query is null)
            return [];

        var rows = await LoadPopulationAsync("incident", UnassignedQueueFields, query, DeskSection.Incidents, cancellationToken).ConfigureAwait(false);
        var open = rows.Where(record => string.IsNullOrWhiteSpace(record.AssignedToSysId)).ToArray();
        if (open.Length == 0)
            return [];

        var ids = open.Select(record => record.SysId).ToArray();
        IReadOnlyDictionary<string, IReadOnlyList<SlaSignal>> sla;
        try
        {
            sla = await LoadSlaAsync(ids, cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceNowException)
        {
            sla = new Dictionary<string, IReadOnlyList<SlaSignal>>(StringComparer.OrdinalIgnoreCase);
        }

        var journal = await LoadLatestJournalAuthorsAsync(ids, cancellationToken).ConfigureAwait(false);
        return DistinctWatched(open.Select(record => FoldSignals(record, sla, journal.Authors)));
    }

    private async Task<AlertBucket> QueryAlertsAsync(
        string table,
        string fields,
        string query,
        AlertKind kind,
        DeskSection section,
        bool includeLocation,
        CancellationToken cancellationToken)
    {
        var result = await GetListAsync(table, fields, query, AlertLimit, 0, cancellationToken).ConfigureAwait(false);
        using (result)
        {
            var mapped = RequireArray(result.Document)
                .EnumerateArray()
                .Select(row => MapAlert(row, kind, section, includeLocation))
                .ToArray();
            var rows = mapped.Where(row => AlertClassifier.IsStillOpen(row.Section, row.State, row.State)).ToArray();
            var total = rows.Length == mapped.Length ? result.TotalCount ?? rows.Length : rows.Length;
            return new AlertBucket(rows, total);
        }
    }

    private static AlertRecord MapAlert(JsonElement row, AlertKind kind, DeskSection section, bool includeLocation)
    {
        var state = section == DeskSection.Requests
            ? SnowField.Read(row, "request_state")
            : SnowField.Read(row, "state");
        var updated = SnowField.Read(row, "sys_updated_on");
        var stateText = state.Display.Length > 0 ? state.Display : state.Value;
        var updatedText = updated.Display.Length > 0 ? updated.Display : updated.Value;
        return new AlertRecord(
            kind,
            section,
            SnowField.Read(row, "sys_id").Value,
            SnowField.Read(row, "number").Display,
            SnowField.Read(row, "short_description").Display,
            stateText,
            SnowField.Read(row, "assignment_group").Display,
            includeLocation ? SnowField.Read(row, "location").Display : "",
            updatedText,
            AssigneeName(row),
            SnowField.Read(row, "assigned_to").Value);
    }

    private async Task<CategoryLoad> LoadCategoryBucketsAsync(AlertSearch search, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var holdNotes = new List<string>();
        IReadOnlyList<string> groupIds;
        try
        {
            groupIds = await MemberGroupIdsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceNowException ex)
        {
            groupIds = [];
            notes.Add("Group membership was skipped: " + ServiceNowException.ShortQueryMessage(ex));
        }

        var watched = new List<WatchedRecord>();
        var incidents = await LoadSectionsAsync(
            "incident",
            PopulationIncidentFields,
            AlertQueryBuilder.PopulationQueries(search.UserSysId, groupIds, search.GroupName, search.Locations, DeskSection.Incidents),
            DeskSection.Incidents,
            "Incidents were skipped: ",
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        watched.AddRange(incidents.Rows);
        AddNote(notes, incidents.Failure);

        var items = await LoadSectionsAsync(
            "sc_req_item",
            PopulationItemFields,
            AlertQueryBuilder.PopulationQueries(search.UserSysId, groupIds, search.GroupName, search.Locations, DeskSection.RequestedItems),
            DeskSection.RequestedItems,
            "Request items were skipped: ",
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        watched.AddRange(items.Rows);
        AddNote(notes, items.Failure);

        var walkUps = await LoadSectionsAsync(
            "interaction",
            PopulationInteractionFields,
            AlertQueryBuilder.PopulationQueries(search.UserSysId, groupIds, search.GroupName, search.Locations, DeskSection.WalkUps),
            DeskSection.WalkUps,
            "Walk-ups were skipped: ",
            PopulationInteractionFieldsWithoutFollowUp,
            "Walk-up follow-up was skipped: ",
            cancellationToken).ConfigureAwait(false);
        watched.AddRange(walkUps.Rows);
        AddNote(notes, walkUps.Failure);
        AddNote(holdNotes, walkUps.HoldNote);

        var leadNotes = new List<string>();
        var lead = new List<WatchedRecord>();
        var leadQuery = AlertQueryBuilder.LeadQueries(search.GroupName, search.TeamMemberIds, DeskSection.Incidents);
        if (leadQuery.Count > 0)
        {
            var leadIncidents = await LoadSectionsAsync(
                "incident",
                PopulationIncidentFields,
                leadQuery,
                DeskSection.Incidents,
                "Lead incidents were skipped: ",
                null,
                null,
                cancellationToken).ConfigureAwait(false);
            lead.AddRange(leadIncidents.Rows);
            AddNote(leadNotes, leadIncidents.Failure);

            var leadItems = await LoadSectionsAsync(
                "sc_req_item",
                PopulationItemFields,
                AlertQueryBuilder.LeadQueries(search.GroupName, search.TeamMemberIds, DeskSection.RequestedItems),
                DeskSection.RequestedItems,
                "Lead request items were skipped: ",
                null,
                null,
                cancellationToken).ConfigureAwait(false);
            lead.AddRange(leadItems.Rows);
            AddNote(leadNotes, leadItems.Failure);

            var leadWalkUps = await LoadSectionsAsync(
                "interaction",
                PopulationInteractionFields,
                AlertQueryBuilder.LeadQueries(search.GroupName, search.TeamMemberIds, DeskSection.WalkUps),
                DeskSection.WalkUps,
                "Lead walk-ups were skipped: ",
                PopulationInteractionFieldsWithoutFollowUp,
                null,
                cancellationToken).ConfigureAwait(false);
            lead.AddRange(leadWalkUps.Rows);
            AddNote(leadNotes, leadWalkUps.Failure);
        }

        // Assigned-to-me matches My Tickets (any office). Group / watched-group queries already
        // apply their own filters; do not drop the user's own tickets by location here.
        var distinct = DistinctWatched(watched);
        var leadDistinct = DistinctWatched(lead);
        var personalIds = new HashSet<string>(distinct.Select(record => record.SysId), StringComparer.OrdinalIgnoreCase);
        var leadOnlyIds = leadDistinct
            .Select(record => record.SysId)
            .Where(id => !personalIds.Contains(id))
            .ToArray();
        var slaIds = personalIds.Concat(leadOnlyIds).ToArray();
        var slaStatus = "";
        IReadOnlyDictionary<string, IReadOnlyList<SlaSignal>> sla;
        try
        {
            sla = await LoadSlaAsync(slaIds, cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceNowException ex)
        {
            sla = new Dictionary<string, IReadOnlyList<SlaSignal>>(StringComparer.OrdinalIgnoreCase);
            slaStatus = SlaFailureStatus(ex);
        }

        var personalJournal = await LoadLatestJournalAuthorsAsync(distinct.Select(record => record.SysId).ToArray(), cancellationToken).ConfigureAwait(false);
        var leadJournal = await LoadLatestJournalAuthorsAsync(leadOnlyIds, cancellationToken).ConfigureAwait(false);
        var personalFolded = distinct.Select(record => FoldSignals(record, sla, personalJournal.Authors)).ToArray();
        var leadAuthors = new Dictionary<string, string>(personalJournal.Authors, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in leadJournal.Authors)
            leadAuthors[pair.Key] = pair.Value;
        var leadFolded = leadDistinct.Select(record => FoldSignals(record, sla, leadAuthors)).ToArray();
        var shared = JoinNotes(notes);
        var now = DateTime.Now;
        var holdSource = walkUps.SkippedFollowUp
            ? personalFolded.Where(record => record.Section != DeskSection.WalkUps)
            : personalFolded;
        var viewer = new AssigneeScope(search.UserSysId);
        var slaScope = new SlaBreachScope(search.UserSysId, groupIds, search.GroupName);
        var personal = new CategoryBuckets(
            AlertClassifier.Bucket(AlertKind.SlaBreaching, personalFolded, now, slaScope, JoinNotes(slaStatus, shared)),
            AlertClassifier.Bucket(AlertKind.OnHoldPastFollowUp, holdSource, now, viewer, JoinNotes(holdNotes, shared)),
            AlertClassifier.Bucket(AlertKind.UpdatedByCaller, personalFolded, now, new CallerUpdateScope(search.UserSysId, search.GroupName, search.Locations), shared),
            AlertClassifier.Bucket(AlertKind.ReturnedWithNotes, personalFolded, now, JoinNotes(personalJournal.Error, shared)),
            AlertClassifier.Bucket(AlertKind.Unattended, personalFolded, now, viewer, shared));
        var daily = DailyWorkBoard.From(personalFolded, leadFolded, now, search.UserSysId, search.TeamMemberIds);
        var leads = AnnotateLead(
            LeadBoard.Build(leadFolded, now, search.TeamMemberIds, search.GroupName, search.Locations),
            JoinNotes(leadNotes),
            leadJournal.Error);
        return new CategoryLoad(personal, leads, daily);
    }

    private static void AddNote(List<string> notes, string? note)
    {
        if (!string.IsNullOrWhiteSpace(note))
            notes.Add(note);
    }

    private static LeadBoard AnnotateLead(LeadBoard board, string? populationStatus, string? journalStatus)
    {
        if (string.IsNullOrWhiteSpace(populationStatus) && string.IsNullOrWhiteSpace(journalStatus))
            return board;
        return new LeadBoard(
            AnnotateLeadSnapshot(board.Team, populationStatus, journalStatus),
            AnnotateLeadSnapshot(board.Regional, populationStatus, journalStatus));
    }

    private static AlertSnapshot AnnotateLeadSnapshot(AlertSnapshot snapshot, string? populationStatus, string? journalStatus)
    {
        var buckets = new Dictionary<AlertKind, AlertBucket>();
        foreach (var kind in AlertCatalog.All)
        {
            var bucket = snapshot.Bucket(kind);
            var status = kind == AlertKind.ReturnedWithNotes
                ? JoinNotes(journalStatus, populationStatus)
                : populationStatus ?? "";
            buckets[kind] = string.IsNullOrWhiteSpace(status) || !string.IsNullOrWhiteSpace(bucket.Status)
                ? bucket
                : new AlertBucket(bucket.Rows, bucket.TotalCount, status);
        }

        return new AlertSnapshot(buckets);
    }

    private static WatchedRecord[] DistinctWatched(IEnumerable<WatchedRecord> records) =>
        records
            .GroupBy(record => record.SysId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

    private async Task<IReadOnlyList<string>> MemberGroupIdsAsync(CancellationToken cancellationToken)
    {
        await MyGroupsClauseAsync(cancellationToken).ConfigureAwait(false);
        return _groupIds ?? [];
    }

    private readonly record struct SectionLoad(WatchedRecord[] Rows, string? Failure, string? HoldNote, bool SkippedFollowUp);

    /// <summary>
    /// Loads each already-short query and merges the rows. One failed request does not drop the rows from the others.
    /// </summary>
    private async Task<SectionLoad> LoadSectionsAsync(
        string table,
        string fields,
        IReadOnlyList<string> queries,
        DeskSection section,
        string failurePrefix,
        string? fallbackFields,
        string? fallbackNotePrefix,
        CancellationToken cancellationToken)
    {
        var rows = new List<WatchedRecord>();
        string? failure = null;
        string? holdNote = null;
        var skippedFollowUp = false;
        var activeFields = fields;
        foreach (var query in queries)
        {
            try
            {
                rows.AddRange(await LoadPopulationAsync(table, activeFields, query, section, cancellationToken).ConfigureAwait(false));
            }
            catch (ServiceNowException ex) when (fallbackFields is not null && !skippedFollowUp && MissingFollowUp(ex))
            {
                skippedFollowUp = true;
                activeFields = fallbackFields;
                if (!string.IsNullOrWhiteSpace(fallbackNotePrefix))
                    holdNote = fallbackNotePrefix + ex.Message;
                try
                {
                    rows.AddRange(await LoadPopulationAsync(table, activeFields, query, section, cancellationToken).ConfigureAwait(false));
                }
                catch (ServiceNowException retry)
                {
                    failure ??= failurePrefix + ServiceNowException.ShortQueryMessage(retry);
                }
            }
            catch (ServiceNowException ex)
            {
                failure ??= failurePrefix + ServiceNowException.ShortQueryMessage(ex);
            }
        }

        return new SectionLoad(rows.ToArray(), failure, holdNote, skippedFollowUp);
    }

    private static bool MissingFollowUp(ServiceNowException exception) =>
        exception.Message.Contains("follow_up", StringComparison.OrdinalIgnoreCase)
        || (exception.Detail?.Contains("follow_up", StringComparison.OrdinalIgnoreCase) ?? false);

    private async Task<WatchedRecord[]> LoadPopulationAsync(
        string table,
        string fields,
        string query,
        DeskSection section,
        CancellationToken cancellationToken)
    {
        var result = await GetListAsync(table, fields, query, AlertLimit, 0, cancellationToken, SuppressPagination(query)).ConfigureAwait(false);
        using (result)
        {
            var rows = new List<WatchedRecord>();
            foreach (var row in RequireArray(result.Document).EnumerateArray())
            {
                var active = SnowField.Read(row, "active");
                if (active.Value.Length > 0 && !SnowField.IsTrue(active))
                    continue;
                var mapped = MapWatched(row, section);
                if (mapped.SysId.Length > 0 && AlertClassifier.IsStillOpen(mapped))
                    rows.Add(mapped);
            }

            return rows.ToArray();
        }
    }

    private static WatchedRecord MapWatched(JsonElement row, DeskSection section)
    {
        var state = SnowField.Read(row, "state");
        var updated = SnowField.Read(row, "sys_updated_on");
        var priority = SnowField.Read(row, "priority");
        var updatedText = updated.Display.Length > 0 ? updated.Display : updated.Value;
        var followUp = FirstText(row, "follow_up");
        var hasFollowUp = AlertClassifier.TryParseInstant(followUp, out var followUpAt);
        return new WatchedRecord
        {
            Section = section,
            SysId = SnowField.Read(row, "sys_id").Value,
            Number = SnowField.Read(row, "number").Display,
            Title = SnowField.Read(row, "short_description").Display,
            State = state.Display.Length > 0 ? state.Display : state.Value,
            StateValue = state.Value,
            Group = SnowField.Read(row, "assignment_group").Display,
            Location = SnowField.Read(row, "location").Display,
            Updated = updatedText,
            UpdatedAt = AlertClassifier.TryParseInstant(updatedText, out var updatedAt) ? updatedAt : null,
            PriorityValue = priority.Value,
            PriorityLabel = priority.Display.Length > 0 ? priority.Display : priority.Value,
            Opened = OpenedText(row),
            UpdatedBy = FirstText(row, "sys_updated_by"),
            CallerUserName = FirstText(row, "caller_id.user_name", "opened_for.user_name", "requested_for.user_name", "request.requested_for.user_name"),
            AssigneeUserName = FirstText(row, "assigned_to.user_name"),
            AssigneeDisplay = AssigneeName(row),
            AssignedToSysId = SnowField.Read(row, "assigned_to").Value,
            AssignmentGroupSysId = SnowField.Read(row, "assignment_group").Value,
            FollowUp = hasFollowUp ? followUpAt : null
        };
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<SlaSignal>>> LoadSlaAsync(IReadOnlyList<string> taskIds, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, List<SlaSignal>>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in AlertQueryBuilder.TaskSlaQueries(taskIds))
        {
            var result = await GetListAsync("task_sla", "task,has_breached,stage,planned_end_time", query, AlertLimit, 0, cancellationToken).ConfigureAwait(false);
            using (result)
            {
                foreach (var row in RequireArray(result.Document).EnumerateArray())
                {
                    var task = SnowField.Read(row, "task").Value;
                    if (task.Length == 0)
                        continue;
                    var stage = SnowField.Read(row, "stage");
                    var plannedText = FirstText(row, "planned_end_time");
                    DateTime? planned = AlertClassifier.TryParseInstant(plannedText, out var parsed) ? parsed : null;
                    if (!found.TryGetValue(task, out var list))
                    {
                        list = [];
                        found[task] = list;
                    }

                    list.Add(new SlaSignal(
                        SnowField.IsTrue(SnowField.Read(row, "has_breached")),
                        stage.Value.Length > 0 ? stage.Value : stage.Display,
                        planned));
                }
            }
        }

        return found.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<SlaSignal>)pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string SlaFailureStatus(ServiceNowException ex)
    {
        if (ex.StatusCode == 403 || ex.Message.Contains("web page", StringComparison.OrdinalIgnoreCase))
            return AlertQueryBuilder.SlaUnavailableStatus;
        return ex.Message;
    }

    private readonly record struct JournalLoad(IReadOnlyDictionary<string, string> Authors, string? Error);

    private async Task<JournalLoad> LoadLatestJournalAuthorsAsync(IReadOnlyList<string> taskIds, CancellationToken cancellationToken)
    {
        var authors = new Dictionary<string, (string Author, DateTime Created)>(StringComparer.OrdinalIgnoreCase);
        string? error = null;
        foreach (var query in AlertQueryBuilder.LatestJournalQueries(taskIds))
        {
            try
            {
                var result = await GetListAsync(
                    "sys_journal_field",
                    "element_id,element,sys_created_by,sys_created_on",
                    query,
                    500,
                    0,
                    cancellationToken,
                    SuppressPagination(query)).ConfigureAwait(false);
                using (result)
                {
                    foreach (var row in RequireArray(result.Document).EnumerateArray())
                    {
                        var element = SnowField.Read(row, "element").Value;
                        if (!element.Equals("comments", StringComparison.OrdinalIgnoreCase)
                            && !element.Equals("work_notes", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var id = SnowField.Read(row, "element_id").Value;
                        var author = FirstText(row, "sys_created_by");
                        if (id.Length == 0 || author.Length == 0)
                            continue;
                        var createdText = FirstText(row, "sys_created_on");
                        var created = AlertClassifier.TryParseInstant(createdText, out var parsed) ? parsed : DateTime.MinValue;
                        if (!authors.TryGetValue(id, out var current) || created >= current.Created)
                            authors[id] = (author, created);
                    }
                }
            }
            catch (ServiceNowException ex)
            {
                error ??= ServiceNowException.ShortQueryMessage(ex);
            }
        }

        return new JournalLoad(
            authors.ToDictionary(pair => pair.Key, pair => pair.Value.Author, StringComparer.OrdinalIgnoreCase),
            error);
    }

    /// <summary>
    /// A packed chunk is already as short as the split allows. Skip the pagination header on that request only.
    /// </summary>
    private static bool SuppressPagination(string query) =>
        query.Length >= AlertQueryBuilder.MaxQueryLength - 48;

    private static WatchedRecord FoldSignals(
        WatchedRecord record,
        IReadOnlyDictionary<string, IReadOnlyList<SlaSignal>> sla,
        IReadOnlyDictionary<string, string> authors)
    {
        var signals = sla.TryGetValue(record.SysId, out var rows) ? rows : [];
        var progress = signals
            .Where(signal => AlertClassifier.StageIsInProgress(signal.Stage))
            .OrderBy(signal => signal.PlannedEnd ?? DateTime.MaxValue)
            .FirstOrDefault();
        return record with
        {
            SlaHasBreached = signals.Any(signal => signal.HasBreached),
            SlaStage = progress?.Stage ?? "",
            SlaPlannedEnd = progress?.PlannedEnd,
            LatestJournalAuthor = authors.TryGetValue(record.SysId, out var author) ? author : ""
        };
    }

    private static string OpenedText(JsonElement row)
    {
        var opened = SnowField.Read(row, "opened_at");
        return opened.Display.Length > 0 ? opened.Display : opened.Value;
    }

    private static string AssigneeName(JsonElement row)
    {
        var assigned = SnowField.Read(row, "assigned_to");
        var name = assigned.Display.Trim();
        if (name.Length == 0 || name.Equals(assigned.Value.Trim(), StringComparison.OrdinalIgnoreCase))
            name = FirstText(row, "assigned_to.user_name");
        return name;
    }

    private static string FirstText(JsonElement row, params string[] names)
    {
        foreach (var name in names)
        {
            var field = SnowField.Read(row, name);
            var text = field.Value.Length > 0 ? field.Value : field.Display;
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return "";
    }

    private static string JoinNotes(params object?[] parts)
    {
        var text = new List<string>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case string note when !string.IsNullOrWhiteSpace(note):
                    text.Add(note.Trim());
                    break;
                case IEnumerable<string> notes:
                    text.AddRange(notes.Where(note => !string.IsNullOrWhiteSpace(note)).Select(note => note.Trim()));
                    break;
            }
        }

        return string.Join(" ", text);
    }

    private sealed record SlaSignal(bool HasBreached, string Stage, DateTime? PlannedEnd);

    private readonly record struct CategoryBuckets(AlertBucket Sla, AlertBucket OnHold, AlertBucket UpdatedByCaller, AlertBucket Returned, AlertBucket Unattended);

    private readonly record struct CategoryLoad(CategoryBuckets Personal, LeadBoard Leads, DailyWorkBoard Daily);

    public Task<PagedResult<IncidentRecord>> SearchIncidentsAsync(TicketQuery query, CancellationToken cancellationToken) =>
        SearchAsync("incident", IncidentFields, ForList(query, DeskSection.Incidents), RecordMapper.Incident, cancellationToken);

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
        SearchAsync("sc_request", RequestFields, ForList(query, DeskSection.Requests), RecordMapper.Request, cancellationToken);

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
        SearchAsync("sc_req_item", ItemFields, ForList(query, DeskSection.RequestedItems), RecordMapper.RequestedItem, cancellationToken);

    public Task<RequestedItemRecord> GetRequestedItemAsync(string sysId, CancellationToken cancellationToken) =>
        GetOneAsync("sc_req_item", sysId, ItemFields, RecordMapper.RequestedItem, cancellationToken);

    public Task<RequestedItemRecord> CreateRequestedItemAsync(RequestedItemChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (string.IsNullOrWhiteSpace(changes.ShortDescription))
            throw new ArgumentException("Enter a short description.");
        return WriteAsync(HttpMethod.Post, "sc_req_item", null, ChangeJson.FromRequestedItem(changes), ItemFields, RecordMapper.RequestedItem, cancellationToken);
    }

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

    public Task<PagedResult<InteractionRecord>> SearchInteractionsAsync(TicketQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var clause = string.IsNullOrWhiteSpace(query.ExtraClause)
            ? "type=" + DefaultChoices.WalkUpType
            : query.ExtraClause + "^type=" + DefaultChoices.WalkUpType;
        return SearchAsync("interaction", InteractionFields, ForList(query, DeskSection.WalkUps) with { ExtraClause = clause }, RecordMapper.Interaction, cancellationToken);
    }

    public Task<InteractionRecord> GetInteractionAsync(string sysId, CancellationToken cancellationToken) =>
        GetOneAsync("interaction", sysId, InteractionFields, RecordMapper.Interaction, cancellationToken);

    public Task<InteractionRecord> CreateInteractionAsync(InteractionChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (string.IsNullOrWhiteSpace(changes.ShortDescription))
            throw new ArgumentException("Enter a short description.");
        return WriteAsync(HttpMethod.Post, "interaction", null, ChangeJson.FromInteraction(changes, creating: true), InteractionFields, RecordMapper.Interaction, cancellationToken);
    }

    public Task<InteractionRecord> UpdateInteractionAsync(string sysId, InteractionChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (!changes.HasChanges)
            throw new InvalidOperationException("There is nothing to update.");
        return WriteAsync(HttpMethod.Patch, "interaction", sysId, ChangeJson.FromInteraction(changes, creating: false), InteractionFields, RecordMapper.Interaction, cancellationToken);
    }

    public async Task<InteractionConversion> ConvertInteractionToIncidentAsync(string interactionSysId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(interactionSysId))
            throw new ArgumentException("Save the walk-up before creating an incident.");

        var id = EncodedQuery.SafeToken(interactionSysId, "interaction id");
        var interaction = await GetInteractionAsync(id, cancellationToken).ConfigureAwait(false);
        string? existingId = null;
        try
        {
            existingId = await FindRelatedIncidentIdAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceNowException)
        {
            existingId = null;
        }

        if (!string.IsNullOrWhiteSpace(existingId))
        {
            var existing = await GetIncidentAsync(existingId, cancellationToken).ConfigureAwait(false);
            return new InteractionConversion(existing, false, null);
        }

        var incident = await CreateIncidentAsync(IncidentFromInteraction(interaction), cancellationToken).ConfigureAwait(false);
        try
        {
            await LinkIncidentAsync(id, incident.SysId, cancellationToken).ConfigureAwait(false);
            return new InteractionConversion(incident, true, null);
        }
        catch (ServiceNowException ex)
        {
            return new InteractionConversion(incident, true, ex.Message);
        }
    }

    public Task<PagedResult<KnowledgeArticle>> SearchKnowledgeAsync(TicketQuery query, CancellationToken cancellationToken) =>
        SearchAsync("kb_knowledge", KnowledgeFields, query, RecordMapper.Knowledge, cancellationToken, SearchFieldSet.Knowledge);

    public Task<KnowledgeArticle> GetKnowledgeAsync(string sysId, CancellationToken cancellationToken) =>
        GetOneAsync("kb_knowledge", sysId, KnowledgeFields, RecordMapper.Knowledge, cancellationToken);

    public async Task<KnowledgeDownload> DownloadKnowledgeAsync(IProgress<DownloadTick>? progress, CancellationToken cancellationToken)
    {
        var articles = new List<KnowledgeArticle>();
        var truncated = await PageRowsAsync(
            "kb_knowledge",
            KnowledgeListFields,
            "ORDERBYDESCsys_updated_on",
            FormCatalogPolicy.MaxKnowledgeArticles,
            progress,
            row => articles.Add(RecordMapper.Knowledge(row)),
            cancellationToken).ConfigureAwait(false);
        return new KnowledgeDownload(articles, truncated);
    }

    public async Task<int?> CountPublishedKnowledgeAsync(CancellationToken cancellationToken)
    {
        var url = "api/now/stats/kb_knowledge?sysparm_count=true&sysparm_query="
            + Uri.EscapeDataString(KnowledgeStats.PublishedQuery);
        try
        {
            var result = await SendAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
            using (result)
                return KnowledgeStats.ReadCount(result.Document.RootElement);
        }
        catch (ServiceNowException)
        {
            return null;
        }
    }

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

    public async Task<IReadOnlyList<JournalEntry>> GetJournalAsync(string table, string sysId, CancellationToken cancellationToken)
    {
        var id = EncodedQuery.SafeToken(sysId, "record id");
        var safeTable = EncodedQuery.SafeToken(table, "table");
        IReadOnlyList<JournalEntry> rows;
        try
        {
            rows = await LoadJournalRowsAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceNowException ex) when (ex.StatusCode is 403 or 404 || IsHtmlPayloadMessage(ex))
        {
            // ACL denials and HTML login pages for sys_journal_field must not fail
            // the whole ticket editor — fall back to activity fields, then empty.
            rows = [];
        }

        if (rows.Any(note => !string.IsNullOrWhiteSpace(note.Text)))
            return NewestFirst(rows);

        // The browser activity stream still shows notes when sys_journal_field is empty
        // (table ACL, or the text lives only on the parent journal fields).
        var activity = await LoadRecordActivityAsync(safeTable, id, cancellationToken).ConfigureAwait(false);
        if (activity.Count > 0)
            return NewestFirst(activity);

        // HTML pages and table ACLs on sys_journal_field must not fail the editor —
        // the record is already loaded and Save must stay usable.
        return NewestFirst(rows);
    }

    private static bool IsHtmlPayloadMessage(ServiceNowException error) =>
        error.Message.Contains("web page instead of API data", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<JournalEntry>> LoadJournalRowsAsync(string id, CancellationToken cancellationToken)
    {
        // work_notes and comments are defined on task. sys_journal_field.name is therefore
        // often "task" for an incident or requested item, not "incident" or "sc_req_item".
        // A name=incident (or name=sc_req_item) clause returns no rows. Match element_id
        // and the journal element, and keep rows whose name is task or the child table.
        var query = "element_id=" + id + "^elementINcomments,additional_comments,work_notes^ORDERBYDESCsys_created_on";
        var result = await GetListAsync(
            "sys_journal_field",
            "sys_id,name,element,value,sys_created_on,sys_created_by",
            query,
            100,
            0,
            cancellationToken).ConfigureAwait(false);
        using (result)
        {
            return RequireArray(result.Document).EnumerateArray().Select(RecordMapper.Journal).ToArray();
        }
    }

    private async Task<IReadOnlyList<JournalEntry>> LoadRecordActivityAsync(string table, string id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendAsync(
                HttpMethod.Get,
                ItemUrl(table, id, "work_notes,comments"),
                null,
                cancellationToken).ConfigureAwait(false);
            using (result)
                return RecordMapper.ActivityHistory(RequireObject(result.Document));
        }
        catch (ServiceNowException)
        {
            return [];
        }
    }

    private static IReadOnlyList<JournalEntry> NewestFirst(IReadOnlyList<JournalEntry> notes) =>
        notes
            .OrderByDescending(note => AlertClassifier.TryParseInstant(note.CreatedDisplay, out var created) ? created : DateTime.MinValue)
            .ThenByDescending(note => note.SysId, StringComparer.Ordinal)
            .ToArray();

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
            (saved, last) = await DownloadChoiceCatalogAsync(cancellationToken, null).ConfigureAwait(false);
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

    public Task RefreshChoiceCatalogAsync(CancellationToken cancellationToken) =>
        RefreshChoiceCatalogAsync(null, cancellationToken);

    public async Task RefreshChoiceCatalogAsync(IProgress<DownloadTick>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var (saved, last) = await DownloadChoiceCatalogAsync(cancellationToken, progress).ConfigureAwait(false);
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

    public Task RefreshAssignmentDirectoryAsync(CancellationToken cancellationToken) =>
        RefreshAssignmentDirectoryAsync(null, null, cancellationToken);

    public Task RefreshAssignmentDirectoryAsync(
        IProgress<DownloadTick>? groupProgress,
        IProgress<DownloadTick>? memberProgress,
        CancellationToken cancellationToken)
    {
        lock (_cacheGate)
        {
            if (_directoryRefresh is { IsCompleted: false })
                return _directoryRefresh;

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _directoryRefresh = completion.Task;
            _ = FinishAssignmentDirectoryAsync(completion, groupProgress, memberProgress, cancellationToken);
            return completion.Task;
        }
    }

    public async Task DownloadAssignmentGroupsAsync(IProgress<DownloadTick>? progress, CancellationToken cancellationToken)
    {
        var groups = await FetchGroupsAsync(progress, cancellationToken).ConfigureAwait(false);
        lock (_cacheGate)
        {
            var existing = ExportMembers();
            _groups = groups;
            _snapshot.Groups = groups.Select(group => new CachedAssignmentGroup { SysId = group.Value, Name = group.Label }).ToList();
            RebuildMemberIndex(existing, markEmptyGroups: false);
            CopyMembersToSnapshot();
        }

        PersistCatalog();
    }

    public async Task DownloadAssignmentMembersAsync(IProgress<DownloadTick>? progress, CancellationToken cancellationToken)
    {
        var fetched = await FetchMembersAsync(MemberDirectoryQuery, FormCatalogPolicy.MaxGroupMembers, progress, cancellationToken).ConfigureAwait(false);
        lock (_cacheGate)
        {
            RebuildMemberIndex(fetched.Members, markEmptyGroups: !fetched.Truncated);
            NoteMemberDirectory(!fetched.Truncated);
            _snapshot.DirectoryCapturedAt = DateTimeOffset.UtcNow;
            _snapshot.Groups = _groups.Select(group => new CachedAssignmentGroup { SysId = group.Value, Name = group.Label }).ToList();
            CopyMembersToSnapshot();
        }

        PersistCatalog();
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

        return SearchReferencesAsync("sys_user", "sys_id,name,first_name,last_name,user_name,email", EncodedQuery.ActiveUserSearch(term), true, 20, cancellationToken);
    }

    public Task<IReadOnlyList<ReferenceSuggestion>> MatchUsersAsync(string text, CancellationToken cancellationToken)
    {
        var term = EncodedQuery.Sanitize(text);
        if (term.Length < 2)
            return Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([]);

        return SearchReferencesAsync("sys_user", "sys_id,name,first_name,last_name,user_name,email", EncodedQuery.ActiveUserExact(term), true, 20, cancellationToken);
    }

    public async Task<IReadOnlyList<Choice>> ListAssignmentGroupsAsync(CancellationToken cancellationToken)
    {
        lock (_cacheGate)
        {
            if (_groups.Count > 0 || _directoryRefresh is { IsCompleted: false })
                return _groups.ToArray();
        }

        var groups = await FetchGroupsAsync(null, cancellationToken).ConfigureAwait(false);
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
        var token = (groupSysId ?? "").Trim();
        if (token.Length == 0)
            return [];

        string id;
        lock (_cacheGate)
        {
            id = ResolveMemberGroupId(token, token);
            if (MembersAreComplete(id))
            {
                if (_membersByGroup.TryGetValue(id, out var cached))
                    return cached.ToArray();
                return [];
            }
        }

        if (!IsGroupToken(id))
            return [];

        var fetched = await FetchMembersAsync("group=" + EncodedQuery.Sanitize(id) + "^ORDERBYsys_id", CompleteGroupMemberCap, null, cancellationToken).ConfigureAwait(false);
        var choices = fetched.Members
            .Where(member => member.UserId.Length > 0)
            .Select(member => new Choice(member.UserId, member.UserName))
            .OrderBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        RememberGroupMembers(id, choices, complete: !fetched.Truncated);
        return choices;
    }

    public Task<ReferenceDownload> DownloadServiceOfferingsAsync(IProgress<DownloadTick>? progress, CancellationToken cancellationToken) =>
        DownloadNamedReferencesAsync("service_offering", FormCatalogPolicy.MaxServiceOfferings, StoreServiceOfferings, progress, cancellationToken);

    public Task<ReferenceDownload> DownloadConfigurationItemsAsync(IProgress<DownloadTick>? progress, CancellationToken cancellationToken) =>
        DownloadConfigurationItemsAsync(FormCatalogPolicy.MaxConfigurationItems, progress, cancellationToken);

    public Task<ReferenceDownload> DownloadConfigurationItemsAsync(int maxRows, IProgress<DownloadTick>? progress, CancellationToken cancellationToken) =>
        DownloadNamedReferencesAsync("cmdb_ci", Math.Clamp(maxRows, 1, FormCatalogPolicy.MaxConfigurationItems), StoreConfigurationItems, progress, cancellationToken);

    public Task<IReadOnlyList<Choice>> ListServiceOfferingsAsync(CancellationToken cancellationToken)
    {
        lock (_cacheGate)
            return Task.FromResult<IReadOnlyList<Choice>>(_serviceOfferings.ToArray());
    }

    public Task<IReadOnlyList<Choice>> ListConfigurationItemsAsync(CancellationToken cancellationToken)
    {
        lock (_cacheGate)
        {
            if (_configurationItemsReady)
                return Task.FromResult<IReadOnlyList<Choice>>(_configurationItems.ToArray());
        }

        return Task.Run(ReadConfigurationItemsFromDisk, cancellationToken);
    }

    private IReadOnlyList<Choice> ReadConfigurationItemsFromDisk()
    {
        FormCatalogSnapshot? snapshot = null;
        if (_catalog is not null && InstanceUri is not null)
            snapshot = _catalog.Load(InstanceUri);

        var saved = snapshot?.ConfigurationItems;
        var choices = saved is { Count: > 0 } ? ToChoices(saved) : null;
        lock (_cacheGate)
        {
            if (_configurationItemsReady)
                return _configurationItems.ToArray();

            if (choices is { Count: > 0 })
            {
                _snapshot.ConfigurationItems = CopyNamed(saved);
                _snapshot.ConfigurationItemsCapturedAt = snapshot!.ConfigurationItemsCapturedAt;
                _snapshot.ConfigurationItemsTruncated = snapshot.ConfigurationItemsTruncated;
                _configurationItems = choices;
            }

            _configurationItemsReady = true;
            return _configurationItems.ToArray();
        }
    }

    public Task<IReadOnlyList<ReferenceSuggestion>> SearchConfigurationItemsAsync(string text, CancellationToken cancellationToken)
    {
        var term = EncodedQuery.Sanitize(text);
        if (term.Length < 2)
            return Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([]);

        return SearchReferencesAsync("cmdb_ci", "sys_id,name,sys_class_name", "active=true^nameLIKE" + term, false, 20, cancellationToken);
    }

    public async Task<PagedResult<HardwareAsset>> SearchHardwareAsync(TicketQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, 100);
        var offset = Math.Max(0, query.Offset);
        var result = await GetListAsync(
            "alm_hardware",
            HardwareFields,
            HardwareCatalog.ListQuery(query.Text, query.Locations, query.LocationSysIds),
            limit,
            offset,
            cancellationToken).ConfigureAwait(false);
        using (result)
        {
            var items = RequireArray(result.Document)
                .EnumerateArray()
                .Select(RecordMapper.Hardware)
                .Where(asset => HardwareCatalog.MatchesSearch(asset, query.Text))
                .OrderBy(asset => asset.SerialNumber, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new PagedResult<HardwareAsset>(items, result.TotalCount);
        }
    }

    public async Task<HardwareCatalogDownload> DownloadHardwareAsync(
        IReadOnlyList<string>? locations,
        IReadOnlyList<string>? locationSysIds,
        IProgress<DownloadTick>? progress,
        CancellationToken cancellationToken)
    {
        var assets = new List<HardwareAsset>();
        var query = HardwareCatalog.DownloadQuery(locations, locationSysIds);
        var truncated = await PageRowsAsync(
            "alm_hardware",
            HardwareFields,
            query,
            FormCatalogPolicy.MaxHardwareAssets,
            progress,
            row =>
            {
                var asset = RecordMapper.Hardware(row);
                if (HardwareCatalog.IsComputer(asset))
                    assets.Add(asset);
            },
            cancellationToken).ConfigureAwait(false);
        return new HardwareCatalogDownload(assets, truncated);
    }

    public Task<HardwareAsset> GetHardwareAsync(string sysId, CancellationToken cancellationToken) =>
        GetOneAsync("alm_hardware", sysId, HardwareFields, RecordMapper.Hardware, cancellationToken);

    public Task<HardwareAsset> UpdateHardwareAsync(string sysId, HardwareChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (!changes.HasChanges)
            throw new InvalidOperationException("There is nothing to update.");

        return WriteAsync(HttpMethod.Patch, "alm_hardware", sysId, ChangeJson.FromHardware(changes), HardwareFields, RecordMapper.Hardware, cancellationToken);
    }

    public async Task<HardwareAsset?> FindHardwareBySerialAsync(string serial, bool ignoreCase, CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = EncodedQuery.SafeToken(serial, "serial number");
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (!ignoreCase)
        {
            var exact = await QueryHardwareAsync("serial_number=" + token, 20, cancellationToken).ConfigureAwait(false);
            return exact.FirstOrDefault(row => string.Equals(row.SerialNumber, serial, StringComparison.Ordinal));
        }

        var like = await QueryHardwareAsync("serial_numberLIKE" + token, 50, cancellationToken).ConfigureAwait(false);
        return like.FirstOrDefault(row => string.Equals(row.SerialNumber, serial, StringComparison.OrdinalIgnoreCase));
    }

    public Task<IReadOnlyList<ReferenceSuggestion>> SearchStockroomsAsync(string text, CancellationToken cancellationToken)
    {
        var term = EncodedQuery.Sanitize(text);
        if (term.Length < 2)
            return Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([]);

        return SearchReferencesAsync("alm_stockroom", "sys_id,name", "nameLIKE" + term, false, 20, cancellationToken);
    }

    public Task<IReadOnlyList<ReferenceSuggestion>> SearchLocationsAsync(string text, CancellationToken cancellationToken)
    {
        var term = EncodedQuery.Sanitize(text);
        if (term.Length < 2)
            return Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([]);

        return SearchReferencesAsync("cmn_location", "sys_id,name", "nameLIKE" + term, false, 20, cancellationToken);
    }

    private async Task<IReadOnlyList<HardwareAsset>> QueryHardwareAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var result = await GetListAsync("alm_hardware", HardwareFields, query, limit, 0, cancellationToken).ConfigureAwait(false);
        using (result)
            return RequireArray(result.Document).EnumerateArray().Select(RecordMapper.Hardware).ToArray();
    }

    public Task<IReadOnlyList<ReferenceSuggestion>> SearchGroupsAsync(string text, CancellationToken cancellationToken)
    {
        var term = EncodedQuery.Sanitize(text);
        if (term.Length < 2)
            return Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([]);

        var query = "active=true^nameLIKE" + term;
        return SearchReferencesAsync("sys_user_group", "sys_id,name,description", query, false, 15, cancellationToken);
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

            var ordered = ReadCatalogOrder(body);
            return new CatalogOrderResult(ordered.RequestSysId, ordered.RequestNumber, ordered.ItemSysId, ordered.ItemNumber);
        }
    }

    private static (string RequestSysId, string RequestNumber, string ItemSysId, string ItemNumber) ReadCatalogOrder(JsonElement body)
    {
        var (itemSysId, itemNumber) = ReadOrderedItem(body);
        var requestSysId = FirstValue(body, "request_id");
        var requestNumber = FirstValue(body, "request_number");
        if (body.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.Object)
        {
            if (requestSysId.Length == 0)
                requestSysId = FirstValue(request, "sys_id", "request_id");
            if (requestNumber.Length == 0)
                requestNumber = FirstValue(request, "number", "request_number");
        }

        var sysId = FirstValue(body, "sys_id");
        var number = FirstValue(body, "number");
        if (requestSysId.Length == 0)
            requestSysId = sysId;
        else if (itemSysId.Length == 0 && sysId.Length > 0 && !sysId.Equals(requestSysId, StringComparison.OrdinalIgnoreCase))
            itemSysId = sysId;

        if (requestNumber.Length == 0)
        {
            if (number.StartsWith("RITM", StringComparison.OrdinalIgnoreCase))
            {
                if (itemNumber.Length == 0)
                    itemNumber = number;
            }
            else
            {
                requestNumber = number;
            }
        }
        else if (itemNumber.Length == 0 && number.StartsWith("RITM", StringComparison.OrdinalIgnoreCase))
        {
            itemNumber = number;
        }

        return (requestSysId, requestNumber, itemSysId, itemNumber);
    }

    private static (string SysId, string Number) ReadOrderedItem(JsonElement body)
    {
        if (body.TryGetProperty("items", out var items))
        {
            if (items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    var read = ReadOneOrderedItem(item);
                    if (read.SysId.Length > 0 || read.Number.Length > 0)
                        return read;
                }
            }
            else if (items.ValueKind == JsonValueKind.Object)
            {
                var read = ReadOneOrderedItem(items);
                if (read.SysId.Length > 0 || read.Number.Length > 0)
                    return read;
            }
        }

        if (body.TryGetProperty("request_item", out var requestItem) && requestItem.ValueKind == JsonValueKind.Object)
        {
            var read = ReadOneOrderedItem(requestItem);
            if (read.SysId.Length > 0 || read.Number.Length > 0)
                return read;
        }

        return (
            FirstValue(body, "request_item_id", "req_item_sys_id", "ritm_sys_id"),
            FirstValue(body, "request_item_number", "req_item_number", "ritm_number"));
    }

    private static (string SysId, string Number) ReadOneOrderedItem(JsonElement item)
    {
        var sysId = FirstValue(item, "sys_id", "request_item_id");
        var number = FirstValue(item, "number", "request_item_number", "req_item_number", "ritm_number");
        if (!number.StartsWith("RITM", StringComparison.OrdinalIgnoreCase))
        {
            var named = FirstValue(item, "request_item_number", "req_item_number", "ritm_number");
            number = named.StartsWith("REQ", StringComparison.OrdinalIgnoreCase) ? "" : named;
        }

        return (sysId, number);
    }

    private async Task<PagedResult<T>> SearchAsync<T>(
        string table,
        string fields,
        TicketQuery query,
        Func<JsonElement, T> map,
        CancellationToken cancellationToken,
        SearchFieldSet textFields = SearchFieldSet.Task)
    {
        ArgumentNullException.ThrowIfNull(query);
        var clause = await BuildClauseAsync(query, textFields, cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(query.Limit, 1, 100);
        var offset = Math.Max(0, query.Offset);
        var result = await GetListAsync(table, fields, clause, limit, offset, cancellationToken).ConfigureAwait(false);
        using (result)
        {
            var items = RequireArray(result.Document).EnumerateArray().Select(map).ToArray();
            return new PagedResult<T>(items, result.TotalCount);
        }
    }

    private async Task<string> BuildClauseAsync(TicketQuery query, SearchFieldSet textFields, CancellationToken cancellationToken)
    {
        var assignment = query.AssignmentClause;
        if (string.IsNullOrWhiteSpace(assignment))
        {
            assignment = query.Assignment switch
            {
                AssignmentScope.Mine => "assigned_to=javascript:gs.getUserID()",
                AssignmentScope.Unassigned when query.OfficeLocations is not null =>
                    "assigned_toISEMPTY^" + await MyGroupsClauseAsync(cancellationToken).ConfigureAwait(false),
                AssignmentScope.Unassigned => "assigned_toISEMPTY",
                AssignmentScope.MyGroups when query.TeamMemberIds is not null =>
                    AlertQueryBuilder.AssignedToAny(query.TeamMemberIds) ?? "sys_id=NO_TEAM",
                AssignmentScope.MyGroups => await MyGroupsClauseAsync(cancellationToken).ConfigureAwait(false),
                _ => ""
            };
        }

        var extra = string.IsNullOrWhiteSpace(query.ParentRequestId)
            ? ""
            : "request=" + EncodedQuery.SafeToken(query.ParentRequestId, "request id");
        if (!string.IsNullOrWhiteSpace(query.ExtraClause))
            extra = extra.Length == 0 ? query.ExtraClause : extra + "^" + query.ExtraClause;
        // Knowledge articles have no assignment group, assignee, or opened_at. The search
        // page hides them while those filters are set instead of dropping every article.
        if (textFields != SearchFieldSet.Knowledge)
        {
            var recordFilters = EncodedQuery.RecordFilters(
                query.AssignmentGroupId,
                query.AssignedToId,
                query.OpenedFrom,
                query.OpenedTo);
            if (recordFilters.Length > 0)
                extra = extra.Length == 0 ? recordFilters : extra + "^" + recordFilters;
        }

        var encoded = EncodedQuery.Build(
            EncodedQuery.TextSearch(query.Text, textFields),
            assignment,
            OpenListClause(query),
            extra);
        // Walk-up My Team is people (TeamMemberIds), so office cities do not apply.
        // My Team and Unassigned stay inside the watched offices. My Tickets is assignee-only.
        if (query.OfficeLocations is not null
            && query.Assignment is AssignmentScope.MyGroups or AssignmentScope.Unassigned
            && query.TeamMemberIds is null)
            return OfficeQueue.ApplyTo(encoded, query.OfficeLocations);
        return encoded;
    }

    /// <summary>
    /// Open ticket lists exclude resolved, closed, and cancelled rows. Search uses any activity and stays unchanged.
    /// The operators are NOT IN and NOT LIKE. A less-than date comparison in this URL makes ServiceNow return an HTML page.
    /// </summary>
    private static string OpenListClause(TicketQuery query)
    {
        if (query.Activity == ActivityFilter.Open && query.ListSection is DeskSection section && IsOpenList(section))
            return AlertQueryBuilder.StillWorking(section);
        return EncodedQuery.ActivityClause(query.Activity);
    }

    private static bool IsOpenList(DeskSection section) =>
        section is DeskSection.Incidents or DeskSection.Requests or DeskSection.RequestedItems or DeskSection.WalkUps;

    private static TicketQuery ForList(TicketQuery query, DeskSection section) =>
        query with { ListSection = section };

    private async Task<string> MyGroupsClauseAsync(CancellationToken cancellationToken)
    {
        if (_groupIds is null)
            _groupIds = await LoadMemberGroupIdsAsync(cancellationToken).ConfigureAwait(false);

        if (_groupIds.Length == 0)
            return "sys_id=NO_GROUP_MEMBERSHIP";

        return "assignment_groupIN" + string.Join(",", _groupIds);
    }

    /// <summary>
    /// Real <c>sys_user_grmember</c> rows for the signed-in user only. Uses the resolved
    /// sys_id (not <c>javascript:gs.getUserID()</c>) so a failed script filter cannot widen
    /// the result set, and pages past the first fifty memberships.
    /// </summary>
    private async Task<string[]> LoadMemberGroupIdsAsync(CancellationToken cancellationToken)
    {
        var userFilter = await MembershipUserFilterAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await PageRowsAsync(
            "sys_user_grmember",
            "group",
            userFilter + "^ORDERBYsys_id",
            FormCatalogPolicy.MaxAssignmentGroups,
            null,
            row =>
            {
                var group = SnowField.Read(row, "group").Value;
                if (group.Length == 0)
                    return;
                try
                {
                    var token = EncodedQuery.SafeToken(group, "group id");
                    if (seen.Add(token))
                        ids.Add(token);
                }
                catch (InvalidOperationException)
                {
                }
            },
            cancellationToken).ConfigureAwait(false);
        return ids.ToArray();
    }

    /// <summary>
    /// Prefer the signed-in sys_id remembered by <see cref="GetCurrentUserAsync"/> at Connect.
    /// Fall back to the Table API script only when Connect has not resolved a user yet.
    /// </summary>
    private Task<string> MembershipUserFilterAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        lock (_cacheGate)
        {
            if (!string.IsNullOrWhiteSpace(_signedInUserSysId))
                return Task.FromResult("user=" + EncodedQuery.SafeToken(_signedInUserSysId, "user id"));
        }

        return Task.FromResult("user=javascript:gs.getUserID()");
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
        int limit,
        CancellationToken cancellationToken)
    {
        var result = await GetListAsync(table, fields, query + "^ORDERBYname", limit, 0, cancellationToken).ConfigureAwait(false);
        using (result)
        {
            var suggestions = new List<ReferenceSuggestion>();
            foreach (var row in RequireArray(result.Document).EnumerateArray())
            {
                var sysId = SnowField.Read(row, "sys_id").Value;
                if (sysId.Length == 0)
                    continue;
                var name = SnowField.Read(row, "name").Display.Trim();
                var userName = user ? SnowField.Read(row, "user_name").Display.Trim() : "";
                var email = user ? SnowField.Read(row, "email").Display.Trim() : "";
                if (name.Length == 0)
                    name = userName.Length > 0 ? userName : email;
                if (name.Length == 0)
                    continue;
                var detail = user
                    ? (email.Length > 0 ? email : userName)
                    : SnowField.Read(row, "description").Display;
                suggestions.Add(new ReferenceSuggestion(sysId, name, detail) { UserName = userName, Email = email });
            }

            return suggestions;
        }
    }

    public async Task<IReadOnlyList<AttachmentSummary>> ListAttachmentsAsync(string tableName, string recordSysId, CancellationToken cancellationToken)
    {
        var table = AttachmentTable(tableName);
        var id = EncodedQuery.SafeToken(recordSysId, "record id");
        var query = "table_name=" + table + "^table_sys_id=" + id;
        var items = new List<AttachmentSummary>();
        var offset = 0;
        const int pageSize = 100;
        for (var page = 0; page < 50; page++)
        {
            var url = "api/now/attachment?sysparm_limit=" + pageSize
                + "&sysparm_offset=" + offset
                + "&sysparm_query=" + Uri.EscapeDataString(query);
            var result = await SendAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
            using (result)
            {
                var rows = RequireArray(result.Document);
                var count = rows.GetArrayLength();
                foreach (var row in rows.EnumerateArray())
                {
                    var sysId = SnowField.Read(row, "sys_id").Value;
                    if (sysId.Length == 0)
                        continue;
                    var name = SnowField.Read(row, "file_name").Display;
                    if (name.Length == 0)
                        name = SnowField.Read(row, "file_name").Value;
                    if (name.Length == 0)
                        name = "attachment";
                    if (items.Any(item => item.SysId.Equals(sysId, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    items.Add(new AttachmentSummary(sysId, name));
                }

                offset += count;
                var expected = result.TotalCount;
                if (count == 0)
                    break;
                if (expected is > 0 && offset >= expected.Value)
                    break;
                if (expected is > 0 && offset < expected.Value)
                    continue;
                if (!string.IsNullOrEmpty(result.NextLink))
                    continue;
                if (count < pageSize)
                    break;
            }
        }

        return items;
    }

    public async Task<byte[]> DownloadAttachmentAsync(string attachmentSysId, CancellationToken cancellationToken)
    {
        var id = EncodedQuery.SafeToken(attachmentSysId, "attachment id");
        return await GetBytesAsync("api/now/attachment/" + Uri.EscapeDataString(id) + "/file", cancellationToken).ConfigureAwait(false);
    }

    private static string AttachmentTable(string tableName)
    {
        var table = (tableName ?? "").Trim();
        if (table is "incident" or "sc_req_item")
            return table;
        throw new ArgumentException("Attachments are available on incidents and request items.");
    }

    private static IncidentChanges IncidentFromInteraction(InteractionRecord interaction) => new()
    {
        ShortDescription = interaction.ShortDescription,
        Description = string.IsNullOrWhiteSpace(interaction.Description) ? null : interaction.Description,
        CallerId = string.IsNullOrWhiteSpace(interaction.OpenedFor.SysId) ? null : interaction.OpenedFor.SysId,
        AssignedToId = string.IsNullOrWhiteSpace(interaction.AssignedTo.SysId) ? null : interaction.AssignedTo.SysId,
        AssignmentGroupId = string.IsNullOrWhiteSpace(interaction.AssignmentGroup.SysId) ? null : interaction.AssignmentGroup.SysId
    };

    private async Task<string?> FindRelatedIncidentIdAsync(string interactionSysId, CancellationToken cancellationToken)
    {
        var query = "interaction=" + interactionSysId + "^document_table=incident";
        var result = await GetListAsync(
            "interaction_related_record",
            "sys_id,document_id,document_table,interaction",
            query,
            1,
            0,
            cancellationToken).ConfigureAwait(false);
        using (result)
        {
            foreach (var row in RequireArray(result.Document).EnumerateArray())
            {
                var documentId = SnowField.Read(row, "document_id").Value;
                if (documentId.Length > 0)
                    return documentId;
            }
        }

        return null;
    }

    private async Task LinkIncidentAsync(string interactionSysId, string incidentSysId, CancellationToken cancellationToken)
    {
        var json = ChangeJson.Serialize(new Dictionary<string, string?>
        {
            ["interaction"] = interactionSysId,
            ["document_table"] = "incident",
            ["document_id"] = incidentSysId
        });
        var result = await SendAsync(HttpMethod.Post, "api/now/table/interaction_related_record", json, cancellationToken).ConfigureAwait(false);
        result.Dispose();
    }

    private async Task<ApiPayload> GetListAsync(string table, string fields, string query, int limit, int offset, CancellationToken cancellationToken, bool suppressPaginationHeader = false)
    {
        var url = "api/now/table/" + table
            + "?sysparm_display_value=all&sysparm_exclude_reference_link=true"
            + "&sysparm_fields=" + Uri.EscapeDataString(fields)
            + "&sysparm_limit=" + limit
            + "&sysparm_offset=" + offset
            + "&sysparm_query=" + Uri.EscapeDataString(query);
        if (suppressPaginationHeader)
            url += "&sysparm_suppress_pagination_header=true";
        var payload = await SendAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
        // A blank HTTP body is stored as result:{}. List callers need an array.
        if (TryGetResult(payload.Document, out var result)
            && result.ValueKind == JsonValueKind.Object
            && !result.EnumerateObject().Any())
        {
            var total = payload.TotalCount;
            var next = payload.NextLink;
            payload.Dispose();
            return new ApiPayload(JsonDocument.Parse("""{"result":[]}"""), total, next);
        }

        return payload;
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
                throw DescribeFailure((int)response.StatusCode, body, relativeUrl);

            if (string.IsNullOrWhiteSpace(body))
                return new ApiPayload(JsonDocument.Parse("""{"result":{}}"""), ReadTotal(response), ReadNextLink(response));

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
                    var error = DescribeFailure((int)response.StatusCode, body, relativeUrl);
                    document.Dispose();
                    throw error;
                }

                return new ApiPayload(document, ReadTotal(response), ReadNextLink(response));
            }
            catch (JsonException)
            {
                throw new ServiceNowException((int)response.StatusCode, "ServiceNow returned a response that was not JSON.", null);
            }
        }
    }

    private ServiceNowException DescribeFailure(int statusCode, string body, string? relativeUrl = null)
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

        if (IsSlaTableDenial(statusCode, body, relativeUrl))
            return error;

        if (IsRejectedBrowserSession(statusCode, body, error))
        {
            BrowserSessionRejected?.Invoke(this, EventArgs.Empty);
            return new ServiceNowException(
                statusCode,
                "The browser sign-in expired or was rejected. Open Connection and sign in with the browser again.",
                error.Detail);
        }

        return error;
    }

    private bool IsRejectedBrowserSession(int statusCode, string body, ServiceNowException error)
    {
        if (_authMode != ServiceNowAuthMode.BrowserSession)
            return false;
        if (ContainsInvalidGrant(body) || ContainsInvalidGrant(error.Message) || ContainsInvalidGrant(error.Detail))
            return true;
        // 401 means the session cookie/token is no longer accepted.
        if (statusCode == 401)
            return true;
        // 403 is usually a table ACL. Only treat it as a dead browser session when
        // ServiceNow explicitly says the user is not authenticated — otherwise a
        // denied list during bootstrap would wipe a fresh sign-in.
        // HTML login/error pages must not clear the session either: a single
        // Forbidden lookup (journal, members, attachments) often returns a web
        // page whose body can contain the words "not authenticated".
        if (statusCode == 403)
        {
            if (LooksLikeHtml(body))
                return false;
            return LooksUnauthenticated(body)
                || LooksUnauthenticated(error.Message)
                || LooksUnauthenticated(error.Detail);
        }
        return false;
    }

    private static bool LooksLikeHtml(string? text) =>
        text is not null && text.TrimStart().StartsWith('<');

    private static bool LooksUnauthenticated(string? text) =>
        text is not null
        && (text.Contains("User Not Authenticated", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Required to provide Auth information", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not authenticated", StringComparison.OrdinalIgnoreCase));

    private static bool IsSlaTableDenial(int statusCode, string body, string? relativeUrl)
    {
        if (relativeUrl is null || !relativeUrl.Contains("api/now/table/task_sla", StringComparison.OrdinalIgnoreCase))
            return false;
        if (statusCode == 403)
            return true;
        return body.TrimStart().StartsWith('<');
    }

    private static bool ContainsInvalidGrant(string? text) =>
        text?.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) == true;

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
            _snapshot.MembersVerified = snapshot.MembersVerified;
            _snapshot.VerifiedMemberGroups = snapshot.VerifiedMemberGroups ?? [];
            _snapshot.Groups = snapshot.Groups ?? [];
            _snapshot.Members = snapshot.Members ?? [];
            _snapshot.ServiceOfferingsCapturedAt = snapshot.ServiceOfferingsCapturedAt;
            _snapshot.ServiceOfferingsTruncated = snapshot.ServiceOfferingsTruncated;
            _snapshot.ServiceOfferings = CopyNamed(snapshot.ServiceOfferings);
            _snapshot.ConfigurationItemsCapturedAt = snapshot.ConfigurationItemsCapturedAt;
            _snapshot.ConfigurationItemsTruncated = snapshot.ConfigurationItemsTruncated;
            _snapshot.ConfigurationItems = CopyNamed(snapshot.ConfigurationItems);
            _serviceOfferings = ToChoices(_snapshot.ServiceOfferings);
            _configurationItems = ToChoices(_snapshot.ConfigurationItems);
            _configurationItemsReady = true;
            _groups = _snapshot.Groups
                .Where(group => !string.IsNullOrWhiteSpace(group.SysId) && !string.IsNullOrWhiteSpace(group.Name))
                .Select(group => new Choice(group.SysId, group.Name))
                .ToList();
            var savedMembers = (_snapshot.Members ?? [])
                .Select(member => new RawMember(member.GroupSysId ?? "", member.GroupSysId ?? "", member.UserSysId ?? "", string.IsNullOrWhiteSpace(member.Name) ? member.UserSysId ?? "" : member.Name))
                .ToList();
            RebuildMemberIndex(savedMembers, markEmptyGroups: snapshot.MembersVerified);
            _completeMemberGroups.Clear();
            foreach (var groupId in _snapshot.VerifiedMemberGroups)
            {
                if (!string.IsNullOrWhiteSpace(groupId))
                    _completeMemberGroups.Add(groupId);
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

    private FormCatalogSnapshot CopySnapshot() => new()
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
        MembersVerified = _snapshot.MembersVerified,
        VerifiedMemberGroups = _snapshot.VerifiedMemberGroups.ToList(),
        Groups = _snapshot.Groups.Select(group => new CachedAssignmentGroup { SysId = group.SysId, Name = group.Name }).ToList(),
        Members = _snapshot.Members.Select(member => new CachedGroupMember
        {
            GroupSysId = member.GroupSysId,
            UserSysId = member.UserSysId,
            Name = member.Name
        }).ToList(),
        ServiceOfferingsCapturedAt = _snapshot.ServiceOfferingsCapturedAt,
        ServiceOfferingsTruncated = _snapshot.ServiceOfferingsTruncated,
        ServiceOfferings = CopyNamed(_snapshot.ServiceOfferings),
        ConfigurationItemsCapturedAt = _snapshot.ConfigurationItemsCapturedAt,
        ConfigurationItemsTruncated = _snapshot.ConfigurationItemsTruncated,
        ConfigurationItems = CopyNamed(_snapshot.ConfigurationItems)
    };

    private void PersistCatalog()
    {
        if (_catalog is null || InstanceUri is null)
            return;

        lock (_persistGate)
        {
            FormCatalogSnapshot copy;
            lock (_cacheGate)
                copy = CopySnapshot();
            _catalog.Save(InstanceUri, copy);
        }
    }

    private async Task<(int Saved, ServiceNowException? Error)> DownloadChoiceCatalogAsync(CancellationToken cancellationToken, IProgress<DownloadTick>? progress)
    {
        ServiceNowException? last = null;
        var saved = 0;
        var completed = 0;
        var total = FormCatalogFields.Independent.Length;
        progress?.Report(new DownloadTick(0, total));
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

            completed++;
            progress?.Report(new DownloadTick(completed, total));
        }

        IReadOnlyList<Choice> categories;
        string[] itemIds;
        lock (_cacheGate)
        {
            categories = _choices.TryGetValue(ChoiceKey("incident", "category", null), out var cached) ? cached : [];
            itemIds = _catalogForms.Keys.Take(FormCatalogPolicy.MaxCatalogItems).ToArray();
        }

        var dependents = categories.Count(category => !string.IsNullOrWhiteSpace(category.Value));
        if (dependents > FormCatalogPolicy.MaxDependentCategories)
            dependents = FormCatalogPolicy.MaxDependentCategories;
        total += dependents + itemIds.Length;
        progress?.Report(new DownloadTick(completed, Math.Max(total, 1)));

        var dependentDone = 0;
        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category.Value) || dependentDone >= FormCatalogPolicy.MaxDependentCategories)
                continue;
            dependentDone++;
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

            completed++;
            progress?.Report(new DownloadTick(completed, Math.Max(total, 1)));
        }

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

            completed++;
            progress?.Report(new DownloadTick(completed, Math.Max(total, 1)));
        }

        return (saved, last);
    }

    private async Task FinishAssignmentDirectoryAsync(
        TaskCompletionSource completion,
        IProgress<DownloadTick>? groupProgress,
        IProgress<DownloadTick>? memberProgress,
        CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAssignmentDirectoryCoreAsync(groupProgress, memberProgress, cancellationToken).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private async Task RefreshAssignmentDirectoryCoreAsync(
        IProgress<DownloadTick>? groupProgress,
        IProgress<DownloadTick>? memberProgress,
        CancellationToken cancellationToken)
    {
        var groups = await FetchGroupsAsync(groupProgress, cancellationToken).ConfigureAwait(false);
        var fetched = await FetchMembersAsync(MemberDirectoryQuery, FormCatalogPolicy.MaxGroupMembers, memberProgress, cancellationToken).ConfigureAwait(false);
        lock (_cacheGate)
        {
            _groups = groups;
            RebuildMemberIndex(fetched.Members, markEmptyGroups: !fetched.Truncated);
            NoteMemberDirectory(!fetched.Truncated);
            _snapshot.DirectoryCapturedAt = DateTimeOffset.UtcNow;
            _snapshot.Groups = _groups.Select(group => new CachedAssignmentGroup { SysId = group.Value, Name = group.Label }).ToList();
            CopyMembersToSnapshot();
        }

        PersistCatalog();
    }

    private async Task<ReferenceDownload> DownloadNamedReferencesAsync(
        string table,
        int maxRows,
        Action<List<Choice>, bool> store,
        IProgress<DownloadTick>? progress,
        CancellationToken cancellationToken)
    {
        var rows = new List<Choice>();
        var seen = 0;
        await PageRowsAsync(
            table,
            "sys_id,name",
            "active=true^ORDERBYname",
            maxRows,
            progress,
            row =>
            {
                seen++;
                var id = SnowField.Read(row, "sys_id").Value;
                var name = SnowField.Read(row, "name").Display.Trim();
                if (id.Length == 0 || name.Length == 0)
                    return;
                if (rows.Any(choice => choice.Value.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    return;
                rows.Add(new Choice(id, name));
            },
            cancellationToken).ConfigureAwait(false);

        var capped = seen >= maxRows;
        store(rows, capped);
        PersistCatalog();
        return new ReferenceDownload(rows.Count, capped);
    }

    private void StoreServiceOfferings(List<Choice> rows, bool truncated)
    {
        lock (_cacheGate)
        {
            _serviceOfferings = rows;
            _snapshot.ServiceOfferings = rows.Select(ToNamed).ToList();
            _snapshot.ServiceOfferingsCapturedAt = DateTimeOffset.UtcNow;
            _snapshot.ServiceOfferingsTruncated = truncated;
        }
    }

    private void StoreConfigurationItems(List<Choice> rows, bool truncated)
    {
        lock (_cacheGate)
        {
            _configurationItems = rows;
            _configurationItemsReady = true;
            _snapshot.ConfigurationItems = rows.Select(ToNamed).ToList();
            _snapshot.ConfigurationItemsCapturedAt = DateTimeOffset.UtcNow;
            _snapshot.ConfigurationItemsTruncated = truncated;
        }
    }

    private static CachedNamedRecord ToNamed(Choice choice) => new() { SysId = choice.Value, Name = choice.Label };

    private static List<CachedNamedRecord> CopyNamed(IEnumerable<CachedNamedRecord>? rows) =>
        (rows ?? []).Select(row => new CachedNamedRecord { SysId = row.SysId ?? "", Name = row.Name ?? "" }).ToList();

    private static List<Choice> ToChoices(IEnumerable<CachedNamedRecord> rows)
    {
        var choices = new List<Choice>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.SysId) || string.IsNullOrWhiteSpace(row.Name))
                continue;
            if (choices.Any(choice => choice.Value.Equals(row.SysId, StringComparison.OrdinalIgnoreCase)))
                continue;
            choices.Add(new Choice(row.SysId, row.Name));
        }

        choices.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));
        return choices;
    }

    private async Task<List<Choice>> FetchGroupsAsync(IProgress<DownloadTick>? progress, CancellationToken cancellationToken)
    {
        var groups = new List<Choice>();
        await PageRowsAsync(
            "sys_user_group",
            "sys_id,name",
            "active=true^ORDERBYname",
            FormCatalogPolicy.MaxAssignmentGroups,
            progress,
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

    private async Task<MemberFetch> FetchMembersAsync(string query, int max, IProgress<DownloadTick>? progress, CancellationToken cancellationToken)
    {
        var members = new List<RawMember>();
        var truncated = await PageRowsAsync(
            "sys_user_grmember",
            "group,user,user.name,user.user_name,user.email",
            query,
            max,
            progress,
            row =>
            {
                var group = SnowField.Read(row, "group");
                var user = SnowField.Read(row, "user");
                if (group.Value.Length == 0 && group.Display.Length == 0)
                    return;
                if (user.Value.Length == 0)
                    return;
                var name = MemberLabel(row, user);
                if (members.Any(member => member.GroupValue.Equals(group.Value, StringComparison.OrdinalIgnoreCase) && member.UserId.Equals(user.Value, StringComparison.OrdinalIgnoreCase)))
                    return;
                members.Add(new RawMember(group.Value, group.Display, user.Value, name));
            },
            cancellationToken).ConfigureAwait(false);
        return new MemberFetch(members, truncated);
    }

    private static string MemberLabel(JsonElement row, SnowField user)
    {
        foreach (var field in new[] { "user", "user.name", "user.user_name", "user.email" })
        {
            var display = DisplayOnly(row, field);
            if (display.Length == 0 || display.Equals(user.Value, StringComparison.OrdinalIgnoreCase))
                continue;
            return display;
        }

        return user.Value;
    }

    private static string DisplayOnly(JsonElement row, string name)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(name, out var element))
            return "";
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (!element.TryGetProperty("display_value", out var shown))
                return "";
            return SnowField.AsString(shown).Trim();
        }

        if (element.ValueKind == JsonValueKind.String)
            return (element.GetString() ?? "").Trim();
        return "";
    }

    private async Task<bool> PageRowsAsync(
        string table,
        string fields,
        string query,
        int maxRows,
        IProgress<DownloadTick>? progress,
        Action<JsonElement> accept,
        CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var offset = 0;
        var kept = 0;
        var received = 0;
        int? total = null;
        var truncated = false;
        progress?.Report(new DownloadTick(0, 0));
        while (kept < maxRows)
        {
            var limit = Math.Min(pageSize, Math.Max(1, maxRows - kept));
            var result = await GetListAsync(table, fields, query, limit, offset, cancellationToken).ConfigureAwait(false);
            using (result)
            {
                var rows = RequireArray(result.Document);
                var count = rows.GetArrayLength();
                foreach (var row in rows.EnumerateArray())
                {
                    if (kept >= maxRows)
                        break;
                    accept(row);
                    kept++;
                }

                received += count;
                if (result.TotalCount is int reported)
                    total = reported;
                if (total is > 0)
                    progress?.Report(new DownloadTick(Math.Min(received, total.Value), total.Value));
                else if (count < limit)
                    progress?.Report(new DownloadTick(Math.Max(received, 1), Math.Max(received, 1)));
                else
                    progress?.Report(new DownloadTick(received, received + limit));

                var hasNext = !string.IsNullOrWhiteSpace(result.NextLink);
                var linkOffset = TryReadOffset(result.NextLink);
                var more = total is int expected && received < expected;
                if (count == 0)
                {
                    truncated = more || hasNext;
                    break;
                }

                if (total is int done && received >= done)
                    break;

                if (!more && !hasNext && count < limit)
                    break;

                var step = offset + count;
                var nextOffset = step;
                if (linkOffset > offset && (count >= limit || linkOffset <= step))
                    nextOffset = linkOffset;

                if (nextOffset <= offset || kept >= maxRows)
                {
                    truncated = more || hasNext || count >= limit;
                    break;
                }

                offset = nextOffset;
            }
        }

        if (kept >= maxRows && total is int expectedTotal && received < expectedTotal)
            truncated = true;
        return truncated;
    }

    private async Task<byte[]> GetBytesAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        var watch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Record(HttpMethod.Get.Method, relativeUrl, 0, watch.ElapsedMilliseconds);
            throw new ServiceNowException(0, "The ServiceNow instance did not respond in time.", null);
        }
        catch (HttpRequestException exception)
        {
            Record(HttpMethod.Get.Method, relativeUrl, 0, watch.ElapsedMilliseconds);
            throw new ServiceNowException(0, "Could not reach the ServiceNow instance. Check the URL and your network.", exception.Message);
        }

        using (response)
        {
            Record(HttpMethod.Get.Method, relativeUrl, (int)response.StatusCode, watch.ElapsedMilliseconds);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw DescribeFailure((int)response.StatusCode, Encoding.UTF8.GetString(bytes));

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) && bytes.Length > 0)
            {
                var text = Encoding.UTF8.GetString(bytes);
                if (text.Contains("\"failure\"", StringComparison.OrdinalIgnoreCase) && text.Contains("\"error\"", StringComparison.OrdinalIgnoreCase))
                    throw DescribeFailure((int)response.StatusCode, text);
            }

            return bytes;
        }
    }

    private List<RawMember> ExportMembers()
    {
        var members = new List<RawMember>();
        foreach (var pair in _membersByGroup)
        {
            foreach (var choice in pair.Value)
                members.Add(new RawMember(pair.Key, pair.Key, choice.Value, choice.Label));
        }

        return members;
    }

    private void RebuildMemberIndex(IReadOnlyList<RawMember> members, bool markEmptyGroups)
    {
        _membersByGroup.Clear();
        _groupsWithMemberList.Clear();
        _completeMemberGroups.Clear();
        foreach (var member in members)
        {
            if (string.IsNullOrWhiteSpace(member.UserId))
                continue;
            var groupId = ResolveMemberGroupId(member.GroupValue, member.GroupDisplay);
            if (string.IsNullOrWhiteSpace(groupId))
                continue;
            if (!_membersByGroup.TryGetValue(groupId, out var list))
            {
                list = [];
                _membersByGroup[groupId] = list;
            }

            if (list.Any(choice => choice.Value.Equals(member.UserId, StringComparison.OrdinalIgnoreCase)))
                continue;
            var name = string.IsNullOrWhiteSpace(member.UserName) ? member.UserId : member.UserName;
            list.Add(new Choice(member.UserId, name));
        }

        foreach (var list in _membersByGroup.Values)
            list.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));

        foreach (var group in _groups)
        {
            var listed = _membersByGroup.TryGetValue(group.Value, out var people) && people.Count > 0;
            if (listed || markEmptyGroups)
                _groupsWithMemberList.Add(group.Value);
        }
    }

    private string ResolveMemberGroupId(string rawValue, string display)
    {
        var value = (rawValue ?? "").Trim();
        var name = (display ?? "").Trim();
        foreach (var group in _groups)
        {
            if (value.Length > 0 && group.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                return group.Value;
        }

        Choice? named = null;
        var matches = 0;
        foreach (var group in _groups)
        {
            var label = group.Label.Trim();
            var valueMatch = value.Length > 0 && label.Equals(value, StringComparison.OrdinalIgnoreCase);
            var nameMatch = name.Length > 0 && label.Equals(name, StringComparison.OrdinalIgnoreCase);
            if (!valueMatch && !nameMatch)
                continue;
            named = group;
            matches++;
        }

        return matches == 1 && named is not null ? named.Value : value;
    }

    private void CopyMembersToSnapshot()
    {
        _snapshot.Members = [];
        foreach (var pair in _membersByGroup)
        {
            foreach (var member in pair.Value)
                _snapshot.Members.Add(new CachedGroupMember { GroupSysId = pair.Key, UserSysId = member.Value, Name = member.Label });
        }
    }

    private static bool IsGroupToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            EncodedQuery.SafeToken(value, "group id");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private const string MemberDirectoryQuery = "userISNOTEMPTY^groupISNOTEMPTY^ORDERBYsys_id";
    private const int CompleteGroupMemberCap = 50000;

    private readonly record struct RawMember(string GroupValue, string GroupDisplay, string UserId, string UserName);

    private readonly record struct MemberFetch(List<RawMember> Members, bool Truncated);

    private void NoteMemberDirectory(bool complete)
    {
        _snapshot.DirectoryComplete = complete;
        _snapshot.MembersVerified = complete;
        if (!complete)
            _completeMemberGroups.Clear();
        _snapshot.VerifiedMemberGroups = _completeMemberGroups.ToList();
    }

    private bool MembersAreComplete(string id)
    {
        if (_completeMemberGroups.Contains(id))
            return true;
        return _snapshot.MembersVerified && _groupsWithMemberList.Contains(id);
    }

    private void RememberGroupMembers(string groupId, IReadOnlyList<Choice> members, bool complete)
    {
        lock (_cacheGate)
        {
            var list = members.OrderBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase).ToList();
            _membersByGroup[groupId] = list;
            _groupsWithMemberList.Add(groupId);
            if (complete)
                _completeMemberGroups.Add(groupId);
            else
                _completeMemberGroups.Remove(groupId);
            _snapshot.VerifiedMemberGroups = _completeMemberGroups.ToList();
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
        if (result.ValueKind == JsonValueKind.Array)
            return result;
        if (result.ValueKind == JsonValueKind.Null)
            throw new ServiceNowException(200, "ServiceNow response did not include a list.", null);
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(message.GetString()))
        {
            throw new ServiceNowException(200, message.GetString()!.Trim(), null);
        }

        throw new ServiceNowException(200, "ServiceNow response did not include a list.", null);
    }

    private static bool TryGetResult(JsonDocument document, out JsonElement result) =>
        document.RootElement.TryGetProperty("result", out result);

    private static int? ReadTotal(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Total-Count", out var values) && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            return count;
        return null;
    }

    private static string? ReadNextLink(HttpResponseMessage response)
    {
        if (!TryLinkValues(response.Headers, out var values) && (response.Content is null || !TryLinkValues(response.Content.Headers, out values)))
            return null;

        var headerText = string.Join(",", values!);
        foreach (var part in SplitLinkParts(headerText))
        {
            var rel = part.IndexOf("rel=", StringComparison.OrdinalIgnoreCase);
            if (rel < 0 || !part[rel..].Contains("next", StringComparison.OrdinalIgnoreCase))
                continue;
            var start = part.IndexOf('<');
            var end = part.IndexOf('>');
            if (start < 0 || end <= start)
                continue;
            var url = part[(start + 1)..end].Trim();
            if (url.Length > 0)
                return url;
        }

        return null;
    }

    private static IEnumerable<string> SplitLinkParts(string header)
    {
        var start = 0;
        var depth = 0;
        for (var index = 0; index < header.Length; index++)
        {
            var character = header[index];
            if (character == '<')
                depth++;
            else if (character == '>' && depth > 0)
                depth--;
            else if (character == ',' && depth == 0)
            {
                yield return header[start..index];
                start = index + 1;
            }
        }

        if (start < header.Length)
            yield return header[start..];
    }

    private static bool TryLinkValues(HttpHeaders headers, out IEnumerable<string>? values) =>
        headers.TryGetValues("Link", out values);

    private static int TryReadOffset(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return 0;
        var text = Uri.UnescapeDataString(url);
        const string marker = "sysparm_offset=";
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return 0;
        var start = index + marker.Length;
        var end = start;
        while (end < text.Length && char.IsDigit(text[end]))
            end++;
        return end > start && int.TryParse(text[start..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset)
            ? offset
            : 0;
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
        public ApiPayload(JsonDocument document, int? totalCount, string? nextLink = null)
        {
            Document = document;
            TotalCount = totalCount;
            NextLink = nextLink;
        }

        public JsonDocument Document { get; }
        public int? TotalCount { get; }
        public string? NextLink { get; }
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
