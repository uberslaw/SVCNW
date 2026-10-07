using System.Text;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Mapping;
using ServiceNowDesk.Models;
using ServiceNowDesk.Query;

namespace ServiceNowDesk.Client;

public sealed class SampleServiceNowClient : IServiceNowClient
{
    private static readonly CurrentUser Me = new("sample-user", "Alex Rivera", "alex.rivera", "alex.rivera@example.com");
    private static readonly ReferenceValue Alex = new("sample-user", "Alex Rivera");
    private static readonly ReferenceValue Jordan = new("user-jordan", "Jordan Lee");
    private static readonly ReferenceValue Sam = new("user-sam", "Sam Patel");
    private static readonly ReferenceValue ClientServices = new("group-cs", "Client Services");
    private static readonly ReferenceValue AusClientServices = new("group-aus", "Aus DT - Client Services");
    private static readonly ReferenceValue Network = new("group-net", "Network");

    private readonly List<IncidentRecord> _incidents = [];
    private readonly List<RequestRecord> _requests = [];
    private readonly List<RequestedItemRecord> _items = [];
    private readonly List<KnowledgeArticle> _articles = [];
    private readonly List<InteractionRecord> _interactions = [];
    private readonly List<HardwareAsset> _hardware = [];
    private readonly List<ReferenceSuggestion> _stockrooms = [];
    private readonly List<ReferenceSuggestion> _locations = [];
    private readonly Dictionary<string, string> _relatedIncidents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<JournalEntry>> _journal = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SampleAlertSignals> _signals = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ApiActivity> _activity = [];
    private int _sequence = 1000;
    private int _unassignedQueueReads;

    public SampleServiceNowClient()
    {
        Seed();
    }

    public Uri? InstanceUri => null;

    public int HardwareCount => _hardware.Count;

    public string LastHardwarePayload { get; private set; } = "";

    public IReadOnlyList<Choice>? ContactTypeChoices { get; set; }

    public IReadOnlyList<ApiActivity> RecentActivity => _activity.ToArray();

    public void Dispose()
    {
    }

    public Task<CurrentUser> GetCurrentUserAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Me);

    public Task<AlertSnapshot> GetOpenAlertsAsync(AlertSearch search, CancellationToken cancellationToken) =>
        Task.FromResult(BuildReport(search, cancellationToken).Personal);

    public Task<AlertReport> GetAlertReportAsync(AlertSearch search, CancellationToken cancellationToken) =>
        Task.FromResult(BuildReport(search, cancellationToken));

    public Task<IReadOnlyList<WatchedRecord>> ListUnassignedGroupQueueAsync(string? watchedGroupName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _unassignedQueueReads++;
        if (_unassignedQueueReads > 1)
            ReleaseLateQueueIncident();

        Record("GET", "api/now/table/incident");
        var watched = EncodedQuery.Sanitize(watchedGroupName);
        var rows = _incidents
            .Where(record => IsOpenUnassigned(record) && InGroupQueue(record, watched))
            .Select(record => Describe(
                DeskSection.Incidents,
                record.SysId,
                record.Number,
                record.ShortDescription,
                record.State,
                record.StateLabel,
                record.AssignmentGroup,
                record.Location,
                record.UpdatedAtDisplay,
                record.Caller,
                record.AssignedTo,
                record.Priority,
                record.PriorityLabel) with
            {
                Opened = record.OpenedAtDisplay
            })
            .ToArray();
        return Task.FromResult<IReadOnlyList<WatchedRecord>>(rows);
    }

    /// <summary>
    /// Practice stand-in for My Groups: the sample user is in Client Services only.
    /// The watched group is included by the name the caller passes, with no location filter.
    /// </summary>
    private static bool InGroupQueue(IncidentRecord record, string watchedName)
    {
        if (record.AssignmentGroup.SysId == ClientServices.SysId
            || record.AssignmentGroup.Display.Equals(ClientServices.Display, StringComparison.OrdinalIgnoreCase))
            return true;
        if (watchedName.Length == 0)
            return false;
        return record.AssignmentGroup.Display.Equals(watchedName, StringComparison.OrdinalIgnoreCase)
            || record.AssignmentGroup.SysId.Equals(watchedName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOpenUnassigned(IncidentRecord record) =>
        record.Active
        && string.IsNullOrWhiteSpace(record.AssignedTo.SysId)
        && AlertClassifier.IsStillOpen(DeskSection.Incidents, record.State, record.StateLabel);

    private void ReleaseLateQueueIncident()
    {
        if (_incidents.Any(record => record.SysId == "inc-queue-new"))
            return;

        var now = Stamp();
        AddIncident(new IncidentRecord
        {
            SysId = "inc-queue-new",
            Number = "INC0010017",
            ShortDescription = "New unassigned priority 1 on the group queue",
            Description = "This arrived after the first look at the group queue.",
            State = "1",
            StateLabel = "New",
            Priority = "1",
            PriorityLabel = "1 - Critical",
            Impact = "1",
            ImpactLabel = "1 - High",
            Urgency = "1",
            UrgencyLabel = "1 - High",
            Category = "inquiry",
            CategoryLabel = "Inquiry / Help",
            ContactType = "phone",
            ContactTypeLabel = "Phone",
            Caller = Jordan,
            AssignedTo = ReferenceValue.Empty,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = now,
            UpdatedAtDisplay = now,
            UpdatedAtValue = now,
            Active = true
        });
    }

    private AlertReport BuildReport(AlertSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        cancellationToken.ThrowIfCancellationRequested();
        var userId = EncodedQuery.SafeToken(search.UserSysId, "user id");
        var assigned = new List<AlertRecord>();
        assigned.AddRange(_incidents.Where(record => record.Active && record.AssignedTo.SysId == userId && AlertClassifier.IsStillOpen(DeskSection.Incidents, record.State, record.StateLabel)).Select(record => ToAlert(record, AlertKind.AssignedToMe)));
        assigned.AddRange(_requests.Where(record => record.Active && record.AssignedTo.SysId == userId && AlertClassifier.IsStillOpen(DeskSection.Requests, record.RequestState, record.RequestStateLabel)).Select(record => ToAlert(record, AlertKind.AssignedToMe)));
        assigned.AddRange(_items.Where(record => record.Active && record.AssignedTo.SysId == userId && AlertClassifier.IsStillOpen(DeskSection.RequestedItems, record.State, record.StateLabel)).Select(record => ToAlert(record, AlertKind.AssignedToMe)));

        var group = new List<AlertRecord>();
        if (AlertQueryBuilder.WatchedGroup(search.GroupName, search.Locations) is not null)
        {
            var name = EncodedQuery.Sanitize(search.GroupName);
            var cities = search.Locations
                .Select(EncodedQuery.Sanitize)
                .Where(city => city.Length > 0)
                .ToArray();
            group.AddRange(_incidents.Where(record =>
                record.Active
                && AlertClassifier.IsStillOpen(DeskSection.Incidents, record.State, record.StateLabel)
                && string.Equals(record.AssignmentGroup.Display, name, StringComparison.OrdinalIgnoreCase)
                && cities.Any(city => string.Equals(record.Location, city, StringComparison.OrdinalIgnoreCase)))
                .Select(record => ToAlert(record, AlertKind.WatchedGroup)));
            Record("GET", "api/now/table/incident");
        }

        Record("GET", "api/now/table/incident");
        Record("GET", "api/now/table/sc_request");
        Record("GET", "api/now/table/sc_req_item");
        var watched = WatchedPopulation(search);
        var lead = LeadPopulation(search);
        var now = DateTime.Now;
        var callerScope = new CallerUpdateScope(userId, search.GroupName, search.Locations);
        var viewer = new AssigneeScope(userId);
        var slaScope = new SlaBreachScope(userId, ["group-cs"], search.GroupName);
        var personal = new AlertSnapshot(new Dictionary<AlertKind, AlertBucket>
        {
            [AlertKind.AssignedToMe] = new(assigned, assigned.Count),
            [AlertKind.WatchedGroup] = new(group, group.Count),
            [AlertKind.SlaBreaching] = AlertClassifier.Bucket(AlertKind.SlaBreaching, watched, now, slaScope),
            [AlertKind.OnHoldPastFollowUp] = AlertClassifier.Bucket(AlertKind.OnHoldPastFollowUp, watched, now, viewer),
            [AlertKind.UpdatedByCaller] = AlertClassifier.Bucket(AlertKind.UpdatedByCaller, watched, now, callerScope),
            [AlertKind.ReturnedWithNotes] = AlertClassifier.Bucket(AlertKind.ReturnedWithNotes, watched, now),
            [AlertKind.Unattended] = AlertClassifier.Bucket(AlertKind.Unattended, watched, now, viewer)
        });
        return new AlertReport(
            personal,
            LeadBoard.Build(lead, now, search.TeamMemberIds, search.GroupName),
            DailyWorkBoard.From(watched, lead, now, userId, search.TeamMemberIds));
    }

    private List<WatchedRecord> LeadPopulation(AlertSearch search)
    {
        var team = new HashSet<string>(
            (search.TeamMemberIds ?? []).Select(id => id?.Trim() ?? "").Where(id => id.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var group = search.GroupName?.Trim() ?? "";
        bool Take(string? assigneeId, ReferenceValue assignmentGroup)
        {
            var name = assignmentGroup.Display?.Trim() ?? "";
            if (group.Length > 0 && name.Equals(group, StringComparison.OrdinalIgnoreCase))
                return true;
            var id = assigneeId?.Trim() ?? "";
            return id.Length > 0 && team.Contains(id);
        }

        var rows = new List<WatchedRecord>();
        foreach (var record in _incidents)
        {
            if (record.Active && AlertClassifier.IsStillOpen(DeskSection.Incidents, record.State, record.StateLabel) && Take(record.AssignedTo.SysId, record.AssignmentGroup))
                rows.Add(Describe(DeskSection.Incidents, record.SysId, record.Number, record.ShortDescription, record.State, record.StateLabel, record.AssignmentGroup, record.Location, record.UpdatedAtDisplay, record.Caller, record.AssignedTo, record.Priority, record.PriorityLabel));
        }

        foreach (var record in _items)
        {
            if (record.Active && AlertClassifier.IsStillOpen(DeskSection.RequestedItems, record.State, record.StateLabel) && Take(record.AssignedTo.SysId, record.AssignmentGroup))
                rows.Add(Describe(DeskSection.RequestedItems, record.SysId, record.Number, record.ShortDescription, record.State, record.StateLabel, record.AssignmentGroup, "", record.UpdatedAtDisplay, ReferenceValue.Empty, record.AssignedTo, record.Priority, record.PriorityLabel));
        }

        foreach (var record in _interactions)
        {
            if (record.Active && AlertClassifier.IsStillOpen(DeskSection.WalkUps, record.State, record.StateLabel) && Take(record.AssignedTo.SysId, record.AssignmentGroup))
                rows.Add(Describe(DeskSection.WalkUps, record.SysId, record.Number, record.ShortDescription, record.State, record.StateLabel, record.AssignmentGroup, "", record.UpdatedAtDisplay, record.OpenedFor, record.AssignedTo));
        }

        return rows;
    }

    private List<WatchedRecord> WatchedPopulation(AlertSearch search)
    {
        var userId = EncodedQuery.SafeToken(search.UserSysId, "user id");
        var watched = AlertQueryBuilder.WatchedGroup(search.GroupName, search.Locations) is not null;
        var groupName = EncodedQuery.Sanitize(search.GroupName);
        var cities = search.Locations
            .Select(EncodedQuery.Sanitize)
            .Where(city => city.Length > 0)
            .ToArray();
        var rows = new List<WatchedRecord>();
        foreach (var record in _incidents)
        {
            if (AlertClassifier.IsStillOpen(DeskSection.Incidents, record.State, record.StateLabel) && InPopulation(record.Active, record.AssignedTo.SysId, record.AssignmentGroup, record.Location, userId, watched, groupName, cities))
                rows.Add(Describe(DeskSection.Incidents, record.SysId, record.Number, record.ShortDescription, record.State, record.StateLabel, record.AssignmentGroup, record.Location, record.UpdatedAtDisplay, record.Caller, record.AssignedTo, record.Priority, record.PriorityLabel));
        }

        foreach (var record in _items)
        {
            if (AlertClassifier.IsStillOpen(DeskSection.RequestedItems, record.State, record.StateLabel) && InPopulation(record.Active, record.AssignedTo.SysId, record.AssignmentGroup, "", userId, watched, groupName, cities))
                rows.Add(Describe(DeskSection.RequestedItems, record.SysId, record.Number, record.ShortDescription, record.State, record.StateLabel, record.AssignmentGroup, "", record.UpdatedAtDisplay, ReferenceValue.Empty, record.AssignedTo, record.Priority, record.PriorityLabel));
        }

        foreach (var record in _interactions)
        {
            if (AlertClassifier.IsStillOpen(DeskSection.WalkUps, record.State, record.StateLabel) && InPopulation(record.Active, record.AssignedTo.SysId, record.AssignmentGroup, "", userId, watched, groupName, cities))
                rows.Add(Describe(DeskSection.WalkUps, record.SysId, record.Number, record.ShortDescription, record.State, record.StateLabel, record.AssignmentGroup, "", record.UpdatedAtDisplay, record.OpenedFor, record.AssignedTo));
        }

        return rows;
    }

    private static bool InPopulation(
        bool active,
        string assignedId,
        ReferenceValue group,
        string location,
        string userId,
        bool watched,
        string groupName,
        IReadOnlyList<string> cities)
    {
        if (!active)
            return false;
        if (assignedId == userId)
            return true;
        if (group.SysId == "group-cs" || group.Display.Equals("Client Services", StringComparison.OrdinalIgnoreCase))
            return true;
        return watched
            && group.Display.Equals(groupName, StringComparison.OrdinalIgnoreCase)
            && cities.Any(city => location.Equals(city, StringComparison.OrdinalIgnoreCase));
    }

    private WatchedRecord Describe(
        DeskSection section,
        string sysId,
        string number,
        string title,
        string stateValue,
        string stateLabel,
        ReferenceValue group,
        string location,
        string updated,
        ReferenceValue caller,
        ReferenceValue assignee,
        string priorityValue = "",
        string priorityLabel = "")
    {
        _signals.TryGetValue(sysId, out var signals);
        var followUp = signals?.FollowUp ?? "";
        var planned = signals?.PlannedEnd ?? "";
        return new WatchedRecord
        {
            Section = section,
            SysId = sysId,
            Number = number,
            Title = title,
            State = stateLabel,
            StateValue = stateValue,
            Group = group.Display,
            Location = location,
            Updated = updated,
            UpdatedAt = AlertClassifier.TryParseInstant(updated, out var updatedAt) ? updatedAt : null,
            PriorityValue = priorityValue ?? "",
            PriorityLabel = string.IsNullOrWhiteSpace(priorityLabel) ? priorityValue ?? "" : priorityLabel,
            UpdatedBy = signals?.UpdatedBy ?? "",
            CallerUserName = UserNameOf(caller),
            AssigneeUserName = UserNameOf(assignee),
            AssigneeDisplay = assignee.Display?.Trim() ?? "",
            AssignedToSysId = assignee.SysId,
            AssignmentGroupSysId = group.SysId,
            FollowUp = AlertClassifier.TryParseInstant(followUp, out var followUpAt) ? followUpAt : null,
            SlaHasBreached = signals?.SlaBreached ?? false,
            SlaStage = signals?.SlaStage ?? "",
            SlaPlannedEnd = AlertClassifier.TryParseInstant(planned, out var plannedAt) ? plannedAt : null,
            LatestJournalAuthor = LatestJournalAuthor(sysId)
        };
    }

    private string LatestJournalAuthor(string sysId)
    {
        if (!_journal.TryGetValue(sysId, out var notes) || notes.Count == 0)
            return "";
        return notes
            .OrderByDescending(note => AlertClassifier.TryParseInstant(note.CreatedDisplay, out var created) ? created : DateTime.MinValue)
            .ThenByDescending(note => note.SysId, StringComparer.Ordinal)
            .First()
            .Author;
    }

    private static string UserNameOf(ReferenceValue user) => user.SysId switch
    {
        "sample-user" => "alex.rivera",
        "user-jordan" => "jordan.lee",
        "user-sam" => "sam.patel",
        "user-casey" => "casey.ng",
        "user-casey2" => "casey.ng2",
        _ => ""
    };

    private static AlertRecord ToAlert(IncidentRecord record, AlertKind kind) => new(
        kind,
        DeskSection.Incidents,
        record.SysId,
        record.Number,
        record.ShortDescription,
        record.StateLabel,
        record.AssignmentGroup.Display,
        record.Location,
        record.UpdatedAtDisplay,
        record.AssignedTo.Display?.Trim() ?? "",
        record.AssignedTo.SysId ?? "");

    private static AlertRecord ToAlert(RequestRecord record, AlertKind kind) => new(
        kind,
        DeskSection.Requests,
        record.SysId,
        record.Number,
        record.ShortDescription,
        record.RequestStateLabel,
        record.AssignmentGroup.Display,
        "",
        record.UpdatedAtDisplay,
        record.AssignedTo.Display?.Trim() ?? "",
        record.AssignedTo.SysId ?? "");

    private static AlertRecord ToAlert(RequestedItemRecord record, AlertKind kind) => new(
        kind,
        DeskSection.RequestedItems,
        record.SysId,
        record.Number,
        record.ShortDescription,
        record.StateLabel,
        record.AssignmentGroup.Display,
        "",
        record.UpdatedAtDisplay,
        record.AssignedTo.Display?.Trim() ?? "",
        record.AssignedTo.SysId ?? "");

    public Task<PagedResult<IncidentRecord>> SearchIncidentsAsync(TicketQuery query, CancellationToken cancellationToken)
    {
        var matches = _incidents.Where(record => Passes(
            query,
            IdOf(record.AssignedTo),
            IdOf(record.AssignmentGroup),
            "",
            "",
            record.Active,
            record.Number,
            Texts(record.ShortDescription, record.Description, record.CloseNotes, record.Number, JournalText(record.SysId)),
            DeskSection.Incidents,
            record.State,
            record.StateLabel,
            record.OpenedAtDisplay));
        return Task.FromResult(Page(matches, query));
    }

    public Task<IncidentRecord> GetIncidentAsync(string sysId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(_incidents, sysId, "incident"));

    public Task<IncidentRecord> CreateIncidentAsync(IncidentChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (string.IsNullOrWhiteSpace(changes.ShortDescription))
            throw new ArgumentException("Enter a short description.");

        var now = Stamp();
        var record = new IncidentRecord
        {
            SysId = NextId("inc"),
            Number = NextNumber("INC"),
            ShortDescription = changes.ShortDescription.Trim(),
            Description = changes.Description?.Trim() ?? "",
            State = string.IsNullOrWhiteSpace(changes.State) ? "1" : changes.State,
            StateLabel = Label(DefaultChoices.IncidentStates, changes.State, "New"),
            Priority = changes.Priority ?? "",
            PriorityLabel = Label(DefaultChoices.Priorities, changes.Priority, ""),
            Impact = string.IsNullOrWhiteSpace(changes.Impact) ? "3" : changes.Impact,
            ImpactLabel = Label(DefaultChoices.Impacts, changes.Impact, "3 - Low"),
            Urgency = string.IsNullOrWhiteSpace(changes.Urgency) ? "3" : changes.Urgency,
            UrgencyLabel = Label(DefaultChoices.Urgencies, changes.Urgency, "3 - Low"),
            Category = changes.Category ?? "",
            CategoryLabel = Label(DefaultChoices.Categories, changes.Category, changes.Category ?? ""),
            Subcategory = changes.Subcategory ?? "",
            ContactType = string.IsNullOrWhiteSpace(changes.ContactType) ? ContactTypeCatalog.DirectValue : changes.ContactType,
            ContactTypeLabel = Label(DefaultChoices.ContactTypes, string.IsNullOrWhiteSpace(changes.ContactType) ? ContactTypeCatalog.DirectValue : changes.ContactType, ContactTypeCatalog.DirectLabel),
            Caller = UserRef(changes.CallerId),
            AssignedTo = UserRef(changes.AssignedToId),
            AssignmentGroup = GroupRef(changes.AssignmentGroupId),
            ServiceOffering = NamedRef(changes.ServiceOfferingId, SampleOfferings),
            ConfigurationItem = NamedRef(changes.ConfigurationItemId, AllConfigurationItems),
            OpenedAtDisplay = now,
            UpdatedAtDisplay = now,
            UpdatedAtValue = now,
            Active = true
        };
        _incidents.Insert(0, record);
        Record("POST", "api/now/table/incident");
        return Task.FromResult(record);
    }

    public Task<IncidentRecord> UpdateIncidentAsync(string sysId, IncidentChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (!changes.HasChanges)
            throw new InvalidOperationException("There is nothing to update.");

        var current = Find(_incidents, sysId, "incident");
        var updated = current with
        {
            ShortDescription = changes.ShortDescription?.Trim() ?? current.ShortDescription,
            Description = changes.Description ?? current.Description,
            State = changes.State ?? current.State,
            StateLabel = changes.State is null ? current.StateLabel : Label(DefaultChoices.IncidentStates, changes.State, changes.State),
            Impact = changes.Impact ?? current.Impact,
            ImpactLabel = changes.Impact is null ? current.ImpactLabel : Label(DefaultChoices.Impacts, changes.Impact, changes.Impact),
            Urgency = changes.Urgency ?? current.Urgency,
            UrgencyLabel = changes.Urgency is null ? current.UrgencyLabel : Label(DefaultChoices.Urgencies, changes.Urgency, changes.Urgency),
            Priority = changes.Priority ?? current.Priority,
            PriorityLabel = changes.Priority is null ? current.PriorityLabel : Label(DefaultChoices.Priorities, changes.Priority, changes.Priority),
            Category = changes.Category ?? current.Category,
            CategoryLabel = changes.Category is null ? current.CategoryLabel : Label(DefaultChoices.Categories, changes.Category, changes.Category),
            Subcategory = changes.Subcategory ?? current.Subcategory,
            ContactType = changes.ContactType ?? current.ContactType,
            ContactTypeLabel = changes.ContactType is null ? current.ContactTypeLabel : Label(DefaultChoices.ContactTypes, changes.ContactType, changes.ContactType),
            CloseCode = changes.CloseCode ?? current.CloseCode,
            CloseCodeLabel = changes.CloseCode ?? current.CloseCodeLabel,
            CloseNotes = changes.CloseNotes ?? current.CloseNotes,
            HoldReason = changes.HoldReason ?? current.HoldReason,
            HoldReasonLabel = changes.HoldReason is null ? current.HoldReasonLabel : Label(DefaultChoices.HoldReasons, changes.HoldReason, changes.HoldReason),
            Caller = changes.CallerId is null ? current.Caller : UserRef(changes.CallerId),
            AssignedTo = changes.ClearAssignedTo ? ReferenceValue.Empty : changes.AssignedToId is null ? current.AssignedTo : UserRef(changes.AssignedToId),
            AssignmentGroup = changes.ClearAssignmentGroup ? ReferenceValue.Empty : changes.AssignmentGroupId is null ? current.AssignmentGroup : GroupRef(changes.AssignmentGroupId),
            ServiceOffering = changes.ClearServiceOffering ? ReferenceValue.Empty : changes.ServiceOfferingId is null ? current.ServiceOffering : NamedRef(changes.ServiceOfferingId, SampleOfferings),
            ConfigurationItem = changes.ClearConfigurationItem ? ReferenceValue.Empty : changes.ConfigurationItemId is null ? current.ConfigurationItem : NamedRef(changes.ConfigurationItemId, AllConfigurationItems),
            Active = (changes.State ?? current.State) is "6" or "7" or "8" ? false : current.Active,
            UpdatedAtDisplay = Stamp(),
            UpdatedAtValue = Stamp()
        };
        Replace(_incidents, updated);
        Record("PATCH", "api/now/table/incident");
        return Task.FromResult(updated);
    }

    public async Task<IncidentRecord> ResolveIncidentAsync(string sysId, string closeCode, string closeNotes, string resolvedState, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(closeCode))
            throw new ArgumentException("Choose a close code.");
        if (string.IsNullOrWhiteSpace(closeNotes))
            throw new ArgumentException("Enter close notes before resolving.");

        var state = string.IsNullOrWhiteSpace(resolvedState) ? "6" : resolvedState;
        var updated = await UpdateIncidentAsync(sysId, new IncidentChanges
        {
            State = state,
            CloseCode = closeCode.Trim(),
            CloseNotes = closeNotes.Trim()
        }, cancellationToken).ConfigureAwait(false);
        await AddJournalAsync("incident", sysId, JournalKind.WorkNotes, closeNotes, cancellationToken).ConfigureAwait(false);
        return updated with { Active = false, State = state, StateLabel = Label(DefaultChoices.IncidentStates, state, "Resolved") };
    }

    public Task<PagedResult<RequestRecord>> SearchRequestsAsync(TicketQuery query, CancellationToken cancellationToken)
    {
        var matches = _requests.Where(record => Passes(
            query,
            IdOf(record.AssignedTo),
            IdOf(record.AssignmentGroup),
            record.RequestedFor.SysId,
            record.OpenedBy.SysId,
            record.Active,
            record.Number,
            Texts(record.ShortDescription, record.Description, record.SpecialInstructions, record.Number, JournalText(record.SysId)),
            DeskSection.Requests,
            record.RequestState,
            record.RequestStateLabel,
            record.OpenedAtDisplay));
        return Task.FromResult(Page(matches, query));
    }

    public Task<RequestRecord> GetRequestAsync(string sysId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(_requests, sysId, "request"));

    public Task<RequestRecord> CreateRequestAsync(RequestChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (string.IsNullOrWhiteSpace(changes.ShortDescription))
            throw new ArgumentException("Enter a short description.");

        var now = Stamp();
        var record = new RequestRecord
        {
            SysId = NextId("req"),
            Number = NextNumber("REQ"),
            ShortDescription = changes.ShortDescription.Trim(),
            Description = changes.Description?.Trim() ?? "",
            SpecialInstructions = changes.SpecialInstructions?.Trim() ?? "",
            RequestState = string.IsNullOrWhiteSpace(changes.RequestState) ? "requested" : changes.RequestState,
            RequestStateLabel = Label(DefaultChoices.RequestStates, changes.RequestState, "Requested"),
            Priority = changes.Priority ?? "4",
            PriorityLabel = Label(DefaultChoices.Priorities, changes.Priority ?? "4", "4 - Low"),
            RequestedFor = UserRef(changes.RequestedForId),
            OpenedBy = Alex,
            DueDate = changes.DueDate ?? "",
            OpenedAtDisplay = now,
            UpdatedAtDisplay = now,
            UpdatedAtValue = now,
            Active = true,
            StageLabel = "Requested"
        };
        _requests.Insert(0, record);
        Record("POST", "api/now/table/sc_request");
        return Task.FromResult(record);
    }

    public Task<RequestRecord> UpdateRequestAsync(string sysId, RequestChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (!changes.HasChanges)
            throw new InvalidOperationException("There is nothing to update.");

        var current = Find(_requests, sysId, "request");
        var state = changes.RequestState ?? current.RequestState;
        var updated = current with
        {
            ShortDescription = changes.ShortDescription?.Trim() ?? current.ShortDescription,
            Description = changes.Description ?? current.Description,
            SpecialInstructions = changes.SpecialInstructions ?? current.SpecialInstructions,
            RequestedFor = changes.RequestedForId is null ? current.RequestedFor : UserRef(changes.RequestedForId),
            RequestState = state,
            RequestStateLabel = changes.RequestState is null ? current.RequestStateLabel : Label(DefaultChoices.RequestStates, state, state),
            Priority = changes.Priority ?? current.Priority,
            PriorityLabel = changes.Priority is null ? current.PriorityLabel : Label(DefaultChoices.Priorities, changes.Priority, changes.Priority),
            DueDate = changes.DueDate ?? current.DueDate,
            Active = state.StartsWith("closed", StringComparison.OrdinalIgnoreCase) ? false : current.Active,
            UpdatedAtDisplay = Stamp(),
            UpdatedAtValue = Stamp()
        };
        Replace(_requests, updated);
        Record("PATCH", "api/now/table/sc_request");
        return Task.FromResult(updated);
    }

    public async Task<RequestRecord> ResolveRequestAsync(string sysId, string requestState, string notes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestState))
            throw new ArgumentException("Choose how to close the request.");
        if (string.IsNullOrWhiteSpace(notes))
            throw new ArgumentException("Enter notes before closing the request.");

        var updated = await UpdateRequestAsync(sysId, new RequestChanges { RequestState = requestState.Trim() }, cancellationToken).ConfigureAwait(false);
        await AddJournalAsync("sc_request", sysId, JournalKind.WorkNotes, notes, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public Task<PagedResult<RequestedItemRecord>> SearchRequestedItemsAsync(TicketQuery query, CancellationToken cancellationToken)
    {
        var matches = _items.Where(record =>
            (string.IsNullOrWhiteSpace(query.ParentRequestId) || record.Request.SysId == query.ParentRequestId)
            && Passes(
                query,
                IdOf(record.AssignedTo),
                IdOf(record.AssignmentGroup),
                "",
                "",
                record.Active,
                record.Number,
                Texts(record.ShortDescription, record.Description, record.CloseNotes, record.Number, record.CatalogItem.Display, JournalText(record.SysId)),
                DeskSection.RequestedItems,
                record.State,
                record.StateLabel,
                record.OpenedAtDisplay));
        return Task.FromResult(Page(matches, query));
    }

    public Task<RequestedItemRecord> GetRequestedItemAsync(string sysId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(_items, sysId, "requested item"));

    public Task<RequestedItemRecord> CreateRequestedItemAsync(RequestedItemChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (string.IsNullOrWhiteSpace(changes.ShortDescription))
            throw new ArgumentException("Enter a short description.");

        var now = Stamp();
        var state = string.IsNullOrWhiteSpace(changes.State) ? "1" : changes.State.Trim();
        var record = new RequestedItemRecord
        {
            SysId = NextId("ritm"),
            Number = NextNumber("RITM"),
            ShortDescription = changes.ShortDescription.Trim(),
            Description = changes.Description?.Trim() ?? "",
            State = state,
            StateLabel = Label(DefaultChoices.ItemStates, state, "Open"),
            Priority = changes.Priority ?? "",
            PriorityLabel = Label(DefaultChoices.Priorities, changes.Priority, ""),
            Quantity = "",
            Request = ReferenceValue.Empty,
            CatalogItem = ReferenceValue.Empty,
            AssignedTo = UserRef(changes.AssignedToId),
            AssignmentGroup = changes.AssignmentGroupId is null ? ReferenceValue.Empty : GroupRef(changes.AssignmentGroupId),
            ServiceOffering = changes.ServiceOfferingId is null ? ReferenceValue.Empty : NamedRef(changes.ServiceOfferingId, SampleOfferings),
            ConfigurationItem = changes.ConfigurationItemId is null ? ReferenceValue.Empty : NamedRef(changes.ConfigurationItemId, AllConfigurationItems),
            OpenedAtDisplay = now,
            UpdatedAtDisplay = now,
            UpdatedAtValue = now,
            Active = state != "3" && state != "4" && state != "7",
            StageLabel = ""
        };
        _items.Insert(0, record);
        Record("POST", "api/now/table/sc_req_item");
        return Task.FromResult(record);
    }

    public Task<RequestedItemRecord> UpdateRequestedItemAsync(string sysId, RequestedItemChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (!changes.HasChanges)
            throw new InvalidOperationException("There is nothing to update.");

        var current = Find(_items, sysId, "requested item");
        var state = changes.State ?? current.State;
        var updated = current with
        {
            ShortDescription = changes.ShortDescription?.Trim() ?? current.ShortDescription,
            Description = changes.Description ?? current.Description,
            State = state,
            StateLabel = changes.State is null ? current.StateLabel : Label(DefaultChoices.ItemStates, state, state),
            Priority = changes.Priority ?? current.Priority,
            PriorityLabel = changes.Priority is null ? current.PriorityLabel : Label(DefaultChoices.Priorities, changes.Priority, changes.Priority),
            AssignedTo = changes.ClearAssignedTo ? ReferenceValue.Empty : changes.AssignedToId is null ? current.AssignedTo : UserRef(changes.AssignedToId),
            AssignmentGroup = changes.ClearAssignmentGroup ? ReferenceValue.Empty : changes.AssignmentGroupId is null ? current.AssignmentGroup : GroupRef(changes.AssignmentGroupId),
            ServiceOffering = changes.ClearServiceOffering ? ReferenceValue.Empty : changes.ServiceOfferingId is null ? current.ServiceOffering : NamedRef(changes.ServiceOfferingId, SampleOfferings),
            ConfigurationItem = changes.ClearConfigurationItem ? ReferenceValue.Empty : changes.ConfigurationItemId is null ? current.ConfigurationItem : NamedRef(changes.ConfigurationItemId, AllConfigurationItems),
            CloseNotes = changes.CloseNotes ?? current.CloseNotes,
            Active = state is "3" or "4" or "7" ? false : current.Active,
            UpdatedAtDisplay = Stamp(),
            UpdatedAtValue = Stamp()
        };
        Replace(_items, updated);
        Record("PATCH", "api/now/table/sc_req_item");
        return Task.FromResult(updated);
    }

    public async Task<RequestedItemRecord> ResolveRequestedItemAsync(string sysId, string state, string closeNotes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state))
            throw new ArgumentException("Choose how to close the requested item.");
        if (string.IsNullOrWhiteSpace(closeNotes))
            throw new ArgumentException("Enter close notes before closing the requested item.");

        var updated = await UpdateRequestedItemAsync(sysId, new RequestedItemChanges
        {
            State = state.Trim(),
            CloseNotes = closeNotes.Trim()
        }, cancellationToken).ConfigureAwait(false);
        await AddJournalAsync("sc_req_item", sysId, JournalKind.WorkNotes, closeNotes, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public Task AddJournalAsync(string table, string sysId, JournalKind kind, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Enter a note before posting.");

        FindAny(sysId);
        var entry = new JournalEntry(NextId("journal"), kind == JournalKind.Comments ? "comments" : "work_notes", kind == JournalKind.Comments ? "Customer comment" : "Work note", text.Trim(), Me.UserName, Stamp());
        if (!_journal.TryGetValue(sysId, out var list))
        {
            list = [];
            _journal[sysId] = list;
        }

        list.Insert(0, entry);
        Record("PATCH", "api/now/table/" + table);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JournalEntry>> GetJournalAsync(string table, string sysId, CancellationToken cancellationToken)
    {
        EncodedQuery.SafeToken(table, "table");
        Record("GET", "api/now/table/sys_journal_field");
        if (!_journal.TryGetValue(sysId, out var list) || list.Count == 0)
            return Task.FromResult<IReadOnlyList<JournalEntry>>([]);

        var ordered = list
            .OrderByDescending(note => AlertClassifier.TryParseInstant(note.CreatedDisplay, out var created) ? created : DateTime.MinValue)
            .ThenByDescending(note => note.SysId, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<JournalEntry>>(ordered);
    }

    public Task<IReadOnlyList<Choice>> GetChoicesAsync(string table, string element, string? dependentValue, CancellationToken cancellationToken)
    {
        if (table == "incident" && element == "contact_type" && ContactTypeChoices is not null)
            return Task.FromResult(ContactTypeChoices);

        if (table == "incident" && element == "subcategory")
        {
            IReadOnlyList<Choice> choices = dependentValue switch
            {
                "software" => [new("email", "Email"), new("os", "Operating System")],
                "hardware" => [new("printer", "Printer"), new("laptop", "Laptop")],
                "network" => [new("vpn", "VPN"), new("wifi", "Wi-Fi")],
                _ => []
            };
            return Task.FromResult(choices);
        }

        if (table == "alm_hardware" && element == "install_status")
            return Task.FromResult(HardwareCatalog.InstallStatuses);

        if (table == "alm_hardware" && element == "substatus")
            return Task.FromResult(HardwareCatalog.RealSubstates(dependentValue));

        return Task.FromResult(DefaultChoices.For(table, element));
    }

    public Task<PagedResult<HardwareAsset>> SearchHardwareAsync(TicketQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var matches = _hardware
            .Where(asset => HardwareCatalog.MatchesSearch(asset, query.Text))
            .OrderBy(asset => asset.SerialNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Record("GET", "api/now/table/alm_hardware");
        var limit = Math.Clamp(query.Limit, 1, 100);
        return Task.FromResult(new PagedResult<HardwareAsset>(matches.Take(limit).ToArray(), matches.Length));
    }

    public Task<HardwareAsset> GetHardwareAsync(string sysId, CancellationToken cancellationToken)
    {
        Record("GET", "api/now/table/alm_hardware");
        return Task.FromResult(Find(_hardware, sysId, "hardware asset"));
    }

    public Task<HardwareAsset> UpdateHardwareAsync(string sysId, HardwareChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (!changes.HasChanges)
            throw new InvalidOperationException("There is nothing to update.");

        var current = Find(_hardware, sysId, "hardware asset");
        var updated = current with
        {
            InstallStatus = changes.InstallStatus ?? current.InstallStatus,
            InstallStatusLabel = changes.InstallStatus is null
                ? current.InstallStatusLabel
                : Label(HardwareCatalog.InstallStatuses, changes.InstallStatus, changes.InstallStatus),
            Substatus = changes.ClearSubstatus ? "" : changes.Substatus ?? current.Substatus,
            SubstatusLabel = changes.ClearSubstatus
                ? ""
                : changes.Substatus is null
                    ? current.SubstatusLabel
                    : Label(HardwareCatalog.RealSubstates(changes.InstallStatus ?? current.InstallStatus), changes.Substatus, changes.Substatus),
            AssignedTo = changes.ClearAssignedTo ? ReferenceValue.Empty : changes.AssignedToId is null ? current.AssignedTo : UserRef(changes.AssignedToId),
            Location = changes.ClearLocation ? ReferenceValue.Empty : changes.LocationId is null ? current.Location : PlaceRef(_locations, changes.LocationId),
            Stockroom = changes.ClearStockroom ? ReferenceValue.Empty : changes.StockroomId is null ? current.Stockroom : PlaceRef(_stockrooms, changes.StockroomId),
            Comments = changes.Comments ?? current.Comments
        };
        Replace(_hardware, updated);
        LastHardwarePayload = ChangeJson.FromHardware(changes);
        Record("PATCH", "api/now/table/alm_hardware");
        return Task.FromResult(updated);
    }

    public Task<HardwareAsset?> FindHardwareBySerialAsync(string serial, bool ignoreCase, CancellationToken cancellationToken)
    {
        Record("GET", "api/now/table/alm_hardware");
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Task.FromResult(_hardware.FirstOrDefault(asset => string.Equals(asset.SerialNumber, serial, comparison)));
    }

    public Task<IReadOnlyList<ReferenceSuggestion>> SearchStockroomsAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult(SearchPeople(text, _stockrooms));

    public Task<IReadOnlyList<ReferenceSuggestion>> SearchLocationsAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult(SearchPeople(text, _locations));

    private static ReferenceValue PlaceRef(IReadOnlyList<ReferenceSuggestion> places, string? sysId)
    {
        if (string.IsNullOrWhiteSpace(sysId))
            return ReferenceValue.Empty;
        var place = places.FirstOrDefault(item => item.SysId == sysId);
        return place is null ? new ReferenceValue(sysId, sysId) : new ReferenceValue(place.SysId, place.Display);
    }

    public Task<IReadOnlyList<ReferenceSuggestion>> SearchUsersAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult(SearchPeople(text, Users));

    public Task<IReadOnlyList<ReferenceSuggestion>> MatchUsersAsync(string text, CancellationToken cancellationToken) =>
        SearchUsersAsync(text, cancellationToken);

    public Task<IReadOnlyList<AttachmentSummary>> ListAttachmentsAsync(string tableName, string recordSysId, CancellationToken cancellationToken)
    {
        Record("GET", "api/now/attachment");
        var table = (tableName ?? "").Trim();
        var id = (recordSysId ?? "").Trim();
        IReadOnlyList<AttachmentSummary> files = (table, id) switch
        {
            ("incident", "inc-printer") or ("sc_req_item", "ritm-laptop") => [new AttachmentSummary("att-practice", "practice-attachment.txt")],
            _ => []
        };
        return Task.FromResult(files);
    }

    public Task<byte[]> DownloadAttachmentAsync(string attachmentSysId, CancellationToken cancellationToken)
    {
        Record("GET", "api/now/attachment/" + (attachmentSysId ?? "").Trim() + "/file");
        if (string.Equals(attachmentSysId?.Trim(), "att-practice", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Encoding.UTF8.GetBytes("Practice attachment for the printer incident."));
        throw new ServiceNowException(404, "ServiceNow could not find that attachment.", null);
    }

    public Task<IReadOnlyList<ReferenceSuggestion>> SearchGroupsAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult(SearchPeople(text, Groups));

    public Task<IReadOnlyList<Choice>> ListAssignmentGroupsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Choice>>(Groups.Select(group => new Choice(group.SysId, group.Display)).ToArray());

    public Task<IReadOnlyList<Choice>> ListServiceOfferingsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Choice>>(SampleOfferings);

    public Task<IReadOnlyList<Choice>> ListConfigurationItemsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Choice>>(SampleConfigurationItems);

    public Task<IReadOnlyList<ReferenceSuggestion>> SearchConfigurationItemsAsync(string text, CancellationToken cancellationToken)
    {
        var term = (text ?? "").Trim();
        if (term.Length < 2)
            return Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([]);

        var matches = SampleConfigurationItems
            .Concat(ExtraConfigurationItems)
            .Where(choice => choice.Label.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(choice => new ReferenceSuggestion(choice.Value, choice.Label, "Configuration item"))
            .ToArray();
        Record("GET", "api/now/table/cmdb_ci");
        return Task.FromResult<IReadOnlyList<ReferenceSuggestion>>(matches);
    }

    public Task<IReadOnlyList<Choice>> ListGroupMembersAsync(string groupSysId, CancellationToken cancellationToken)
    {
        var token = (groupSysId ?? "").Trim();
        var group = Groups.FirstOrDefault(candidate =>
            candidate.SysId.Equals(token, StringComparison.OrdinalIgnoreCase)
            || candidate.Display.Equals(token, StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<Choice> members = group?.SysId switch
        {
            "group-cs" => [new("sample-user", "Alex Rivera"), new("user-jordan", "Jordan Lee")],
            "group-aus" => [new("user-jordan", "Jordan Lee"), new("user-sam", "Sam Patel")],
            "group-net" => [new("user-sam", "Sam Patel")],
            _ => []
        };
        return Task.FromResult(members);
    }

    public Task<IReadOnlyList<CatalogItemSummary>> SearchCatalogItemsAsync(string text, CancellationToken cancellationToken)
    {
        var term = (text ?? "").Trim();
        IReadOnlyList<CatalogItemSummary> items = term.Length < 2
            ? []
            : Catalog.Where(item => item.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || item.ShortDescription.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        Record("GET", "api/sn_sc/servicecatalog/items");
        return Task.FromResult(items);
    }

    public Task<IReadOnlyList<CatalogVariableDefinition>> GetCatalogVariablesAsync(string itemSysId, CancellationToken cancellationToken)
    {
        IReadOnlyList<CatalogVariableDefinition> variables = itemSysId switch
        {
            "cat-laptop" =>
            [
                new("department", "Department", true, []),
                new("preferred_os", "Preferred operating system", true, [new("win11", "Windows 11"), new("macos", "macOS")])
            ],
            "cat-monitor" => [new("location", "Desk location", true, [])],
            _ => []
        };
        return Task.FromResult(variables);
    }

    public Task<CatalogOrderResult> OrderCatalogItemAsync(string itemSysId, int quantity, string? requestedForSysId, IReadOnlyDictionary<string, string> variables, CancellationToken cancellationToken)
    {
        var item = Catalog.FirstOrDefault(candidate => candidate.SysId == itemSysId)
            ?? throw new ServiceNowException(404, "ServiceNow could not find that catalog item.", null);
        var request = new RequestRecord
        {
            SysId = NextId("req"),
            Number = NextNumber("REQ"),
            ShortDescription = item.Name,
            Description = item.ShortDescription,
            RequestState = "requested",
            RequestStateLabel = "Requested",
            RequestedFor = UserRef(requestedForSysId),
            OpenedBy = Alex,
            Priority = "4",
            PriorityLabel = "4 - Low",
            OpenedAtDisplay = Stamp(),
            UpdatedAtDisplay = Stamp(),
            UpdatedAtValue = Stamp(),
            Active = true,
            StageLabel = "Requested"
        };
        _requests.Insert(0, request);
        _items.Insert(0, new RequestedItemRecord
        {
            SysId = NextId("ritm"),
            Number = NextNumber("RITM"),
            ShortDescription = item.Name,
            Description = item.ShortDescription,
            State = "1",
            StateLabel = "Open",
            Request = new ReferenceValue(request.SysId, request.Number),
            CatalogItem = new ReferenceValue(item.SysId, item.Name),
            Quantity = Math.Clamp(quantity, 1, 50).ToString(),
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = Stamp(),
            UpdatedAtDisplay = Stamp(),
            UpdatedAtValue = Stamp(),
            Active = true,
            StageLabel = "Waiting for approval"
        });
        Record("POST", "api/sn_sc/servicecatalog/items/" + itemSysId + "/order_now");
        return Task.FromResult(new CatalogOrderResult(request.SysId, request.Number));
    }

    public Task<PagedResult<KnowledgeArticle>> SearchKnowledgeAsync(TicketQuery query, CancellationToken cancellationToken)
    {
        var matches = _articles.Where(article => Passes(
            query,
            "",
            "",
            "",
            "",
            article.WorkflowState.Length == 0 || article.WorkflowState.Equals("published", StringComparison.OrdinalIgnoreCase),
            article.Number,
            Texts(article.ShortDescription, article.Text, article.Topic, article.Category, article.KnowledgeBase, article.Number),
            honorRecordFilters: false));
        return Task.FromResult(Page(matches, query));
    }

    public Task<KnowledgeArticle> GetKnowledgeAsync(string sysId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(_articles, sysId, "knowledge article"));

    public Task<KnowledgeDownload> DownloadKnowledgeAsync(IProgress<DownloadTick>? progress, CancellationToken cancellationToken)
    {
        var articles = _articles.ToArray();
        progress?.Report(new DownloadTick(0, Math.Max(articles.Length, 1)));
        progress?.Report(new DownloadTick(Math.Max(articles.Length, 1), Math.Max(articles.Length, 1)));
        Record("GET", "api/now/table/kb_knowledge");
        return Task.FromResult(new KnowledgeDownload(articles, false));
    }

    public Task<int?> CountPublishedKnowledgeAsync(CancellationToken cancellationToken)
    {
        Record("GET", "api/now/stats/kb_knowledge?sysparm_count=true&sysparm_query=" + Uri.EscapeDataString(KnowledgeStats.PublishedQuery));
        var count = _articles.Count(article => article.WorkflowState.Equals("published", StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<int?>(count);
    }

    public Task<PagedResult<InteractionRecord>> SearchInteractionsAsync(TicketQuery query, CancellationToken cancellationToken)
    {
        var matches = _interactions.Where(record =>
            string.Equals(record.Type, DefaultChoices.WalkUpType, StringComparison.OrdinalIgnoreCase)
            && Passes(
                query,
                IdOf(record.AssignedTo),
                IdOf(record.AssignmentGroup),
                "",
                "",
                record.Active,
                record.Number,
                Texts(record.ShortDescription, record.Description, record.Number, record.OpenedFor.Display, JournalText(record.SysId)),
                DeskSection.WalkUps,
                record.State,
                record.StateLabel,
                record.OpenedAtDisplay));
        return Task.FromResult(Page(matches, query));
    }

    public Task<InteractionRecord> GetInteractionAsync(string sysId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(_interactions, sysId, "interaction"));

    public Task<InteractionRecord> CreateInteractionAsync(InteractionChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (string.IsNullOrWhiteSpace(changes.ShortDescription))
            throw new ArgumentException("Enter a short description.");

        var now = Stamp();
        var state = string.IsNullOrWhiteSpace(changes.State) ? "new" : changes.State;
        var record = new InteractionRecord
        {
            SysId = NextId("ims"),
            Number = NextNumber("IMS"),
            ShortDescription = changes.ShortDescription.Trim(),
            Description = changes.Description?.Trim() ?? "",
            State = state,
            StateLabel = Label(DefaultChoices.InteractionStates, state, state),
            Type = DefaultChoices.WalkUpType,
            TypeLabel = "Walk-up",
            OpenedFor = UserRef(changes.OpenedForId),
            AssignedTo = UserRef(changes.AssignedToId),
            AssignmentGroup = GroupRef(changes.AssignmentGroupId),
            OpenedAtDisplay = now,
            UpdatedAtDisplay = now,
            UpdatedAtValue = now,
            Active = !IsClosedInteraction(state)
        };
        _interactions.Insert(0, record);
        Record("POST", "api/now/table/interaction");
        return Task.FromResult(record);
    }

    public Task<InteractionRecord> UpdateInteractionAsync(string sysId, InteractionChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (!changes.HasChanges)
            throw new InvalidOperationException("There is nothing to update.");

        var current = Find(_interactions, sysId, "interaction");
        var state = changes.State ?? current.State;
        var type = changes.Type ?? current.Type;
        var updated = current with
        {
            ShortDescription = changes.ShortDescription?.Trim() ?? current.ShortDescription,
            Description = changes.Description ?? current.Description,
            State = state,
            StateLabel = changes.State is null ? current.StateLabel : Label(DefaultChoices.InteractionStates, state, state),
            Type = type,
            TypeLabel = changes.Type is null ? current.TypeLabel : Label(DefaultChoices.InteractionTypes, type, type),
            OpenedFor = changes.OpenedForId is null ? current.OpenedFor : UserRef(changes.OpenedForId),
            AssignedTo = changes.ClearAssignedTo ? ReferenceValue.Empty : changes.AssignedToId is null ? current.AssignedTo : UserRef(changes.AssignedToId),
            AssignmentGroup = changes.ClearAssignmentGroup ? ReferenceValue.Empty : changes.AssignmentGroupId is null ? current.AssignmentGroup : GroupRef(changes.AssignmentGroupId),
            Active = changes.State is null ? current.Active : !IsClosedInteraction(state),
            UpdatedAtDisplay = Stamp(),
            UpdatedAtValue = Stamp()
        };
        Replace(_interactions, updated);
        Record("PATCH", "api/now/table/interaction");
        return Task.FromResult(updated);
    }

    public async Task<InteractionConversion> ConvertInteractionToIncidentAsync(string interactionSysId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(interactionSysId))
            throw new ArgumentException("Save the walk-up before creating an incident.");

        var interaction = Find(_interactions, interactionSysId, "interaction");
        if (_relatedIncidents.TryGetValue(interaction.SysId, out var existingId))
        {
            Record("GET", "api/now/table/interaction_related_record");
            var existing = await GetIncidentAsync(existingId, cancellationToken).ConfigureAwait(false);
            return new InteractionConversion(existing, false, null);
        }

        var incident = await CreateIncidentAsync(new IncidentChanges
        {
            ShortDescription = interaction.ShortDescription,
            Description = string.IsNullOrWhiteSpace(interaction.Description) ? null : interaction.Description,
            CallerId = string.IsNullOrWhiteSpace(interaction.OpenedFor.SysId) ? null : interaction.OpenedFor.SysId,
            AssignedToId = string.IsNullOrWhiteSpace(interaction.AssignedTo.SysId) ? null : interaction.AssignedTo.SysId,
            AssignmentGroupId = string.IsNullOrWhiteSpace(interaction.AssignmentGroup.SysId) ? null : interaction.AssignmentGroup.SysId
        }, cancellationToken).ConfigureAwait(false);
        _relatedIncidents[interaction.SysId] = incident.SysId;
        Record("POST", "api/now/table/interaction_related_record");
        return new InteractionConversion(incident, true, null);
    }

    private static bool IsClosedInteraction(string state) =>
        state is "closed_complete" or "closed_abandoned";

    private void Seed()
    {
        AddIncident(new IncidentRecord
        {
            SysId = "inc-printer",
            Number = "INC0010001",
            ShortDescription = "Printer jam on floor 3",
            Description = "The HP printer by finance is jammed and the queue is stuck.",
            State = "2",
            StateLabel = "In Progress",
            Priority = "3",
            PriorityLabel = "3 - Moderate",
            Impact = "3",
            ImpactLabel = "3 - Low",
            Urgency = "2",
            UrgencyLabel = "2 - Medium",
            Category = "hardware",
            CategoryLabel = "Hardware",
            Subcategory = "printer",
            SubcategoryLabel = "Printer",
            ContactType = "phone",
            ContactTypeLabel = "Phone",
            Caller = Jordan,
            AssignedTo = Alex,
            AssignmentGroup = ClientServices,
            ServiceOffering = new ReferenceValue("offering-print", "Printing"),
            ConfigurationItem = new ReferenceValue("ci-printer", "HQ-PRINTER-01"),
            OpenedAtDisplay = "2026-09-28 09:15",
            UpdatedAtDisplay = "2026-09-28 10:40",
            UpdatedAtValue = "2026-09-28 10:40:00",
            Active = true
        },
            new JournalEntry("journal-printer-comment", "comments", "Customer comment", "The finance queue is still stuck. Can someone call me?", "jordan.lee", "2026-09-28 09:30"),
            new JournalEntry("journal-printer", "work_notes", "Work note", "Replaced the tray and asked finance to reprint.", "alex.rivera", "2026-09-28 10:40"));

        AddIncident(new IncidentRecord
        {
            SysId = "inc-vpn",
            Number = "INC0010002",
            ShortDescription = "VPN drops every few minutes",
            Description = "Remote staff lose the VPN tunnel every few minutes after the morning change.",
            State = "1",
            StateLabel = "New",
            Priority = "2",
            PriorityLabel = "2 - High",
            Impact = "2",
            ImpactLabel = "2 - Medium",
            Urgency = "1",
            UrgencyLabel = "1 - High",
            Category = "network",
            CategoryLabel = "Network",
            Subcategory = "vpn",
            SubcategoryLabel = "VPN",
            ContactType = "email",
            ContactTypeLabel = "Email",
            Caller = Sam,
            AssignedTo = Alex,
            AssignmentGroup = Network,
            OpenedAtDisplay = "2026-09-29 08:05",
            UpdatedAtDisplay = "2026-09-29 08:05",
            UpdatedAtValue = "2026-09-29 08:05:00",
            Active = true
        });

        AddIncident(new IncidentRecord
        {
            SysId = "inc-password",
            Number = "INC0010003",
            ShortDescription = "Password reset for finance",
            Description = "Caller is locked out and cannot reach payroll.",
            State = "6",
            StateLabel = "Resolved",
            Priority = "3",
            PriorityLabel = "3 - Moderate",
            Impact = "3",
            ImpactLabel = "3 - Low",
            Urgency = "2",
            UrgencyLabel = "2 - Medium",
            Category = "software",
            CategoryLabel = "Software",
            ContactType = "phone",
            ContactTypeLabel = "Phone",
            CloseCode = "Solved (Permanently)",
            CloseCodeLabel = "Solved (Permanently)",
            CloseNotes = "Reset and unlocked the account.",
            Caller = Jordan,
            AssignedTo = Alex,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-09-20 11:12",
            UpdatedAtDisplay = "2026-09-20 11:20",
            UpdatedAtValue = "2026-09-20 11:20:00",
            Active = false
        });

        AddIncident(new IncidentRecord
        {
            SysId = "inc-email",
            Number = "INC0010004",
            ShortDescription = "Mailbox full for the front desk",
            Description = "The shared front desk mailbox is rejecting new mail.",
            State = "1",
            StateLabel = "New",
            Priority = "4",
            PriorityLabel = "4 - Low",
            Impact = "3",
            ImpactLabel = "3 - Low",
            Urgency = "3",
            UrgencyLabel = "3 - Low",
            Category = "software",
            CategoryLabel = "Software",
            Subcategory = "email",
            ContactType = "walk-in",
            ContactTypeLabel = "Walk-in",
            Caller = Jordan,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-09-27 15:45",
            UpdatedAtDisplay = "2026-09-27 15:45",
            UpdatedAtValue = "2026-09-27 15:45:00",
            Active = true
        });

        AddIncident(new IncidentRecord
        {
            SysId = "inc-badge",
            Number = "INC0010005",
            ShortDescription = "Badge reader offline at the lab door",
            Description = "Staff cannot badge into the lab. The reader shows a red light.",
            State = "2",
            StateLabel = "In Progress",
            Priority = "2",
            PriorityLabel = "2 - High",
            Impact = "2",
            ImpactLabel = "2 - Medium",
            Urgency = "2",
            UrgencyLabel = "2 - Medium",
            Category = "hardware",
            CategoryLabel = "Hardware",
            ContactType = "phone",
            ContactTypeLabel = "Phone",
            Caller = Sam,
            AssignedTo = Jordan,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-09-26 07:55",
            UpdatedAtDisplay = "2026-09-26 09:10",
            UpdatedAtValue = "2026-09-26 09:10:00",
            Active = true
        });

        AddIncident(new IncidentRecord
        {
            SysId = "inc-bluescreen",
            Number = "INC0010006",
            ShortDescription = "Laptop blue screen after update",
            Description = "Analyst laptop restarts to a blue screen after yesterday's update.",
            State = "2",
            StateLabel = "In Progress",
            Priority = "2",
            PriorityLabel = "2 - High",
            Impact = "2",
            ImpactLabel = "2 - Medium",
            Urgency = "2",
            UrgencyLabel = "2 - Medium",
            Category = "hardware",
            CategoryLabel = "Hardware",
            Subcategory = "laptop",
            ContactType = "phone",
            ContactTypeLabel = "Phone",
            Caller = Sam,
            AssignedTo = Alex,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-09-25 13:00",
            UpdatedAtDisplay = "2026-09-25 16:22",
            UpdatedAtValue = "2026-09-25 16:22:00",
            Active = true
        }, new JournalEntry("journal-blue", "work_notes", "Work note", "Memory dump shows bugcheck 0x50 after the graphics driver update.", "alex.rivera", "2026-09-25 16:22"));

        AddIncident(new IncidentRecord
        {
            SysId = "inc-brisbane",
            Number = "INC0010007",
            ShortDescription = "Desktop will not boot in Brisbane",
            Description = "The reception desktop in the Brisbane office stays on the manufacturer logo.",
            State = "1",
            StateLabel = "New",
            Priority = "3",
            PriorityLabel = "3 - Moderate",
            Impact = "3",
            ImpactLabel = "3 - Low",
            Urgency = "2",
            UrgencyLabel = "2 - Medium",
            Category = "hardware",
            CategoryLabel = "Hardware",
            ContactType = "phone",
            ContactTypeLabel = "Phone",
            Caller = Jordan,
            AssignedTo = Jordan,
            AssignmentGroup = AusClientServices,
            Location = "Brisbane",
            OpenedAtDisplay = "2026-09-30 08:20",
            UpdatedAtDisplay = "2026-09-30 08:20",
            UpdatedAtValue = "2026-09-30 08:20:00",
            Active = true
        });
        _signals["inc-brisbane"] = new SampleAlertSignals { UpdatedBy = "jordan.lee" };

        AddIncident(new IncidentRecord
        {
            SysId = "inc-sydney",
            Number = "INC0010008",
            ShortDescription = "Monitor flicker in Sydney",
            Description = "A desk monitor in the Sydney office flickers after lunch.",
            State = "1",
            StateLabel = "New",
            Priority = "4",
            PriorityLabel = "4 - Low",
            Impact = "3",
            ImpactLabel = "3 - Low",
            Urgency = "3",
            UrgencyLabel = "3 - Low",
            Category = "hardware",
            CategoryLabel = "Hardware",
            ContactType = "email",
            ContactTypeLabel = "Email",
            Caller = Sam,
            AssignedTo = Sam,
            AssignmentGroup = AusClientServices,
            Location = "Sydney",
            OpenedAtDisplay = "2026-09-30 11:05",
            UpdatedAtDisplay = "2026-09-30 11:05",
            UpdatedAtValue = "2026-09-30 11:05:00",
            Active = true
        });

        AddIncident(new IncidentRecord
        {
            SysId = "inc-brisbane-closed",
            Number = "INC0010009",
            ShortDescription = "Closed Brisbane printer jam",
            Description = "This Brisbane printer jam is already resolved.",
            State = "6",
            StateLabel = "Resolved",
            Priority = "4",
            PriorityLabel = "4 - Low",
            Impact = "3",
            ImpactLabel = "3 - Low",
            Urgency = "3",
            UrgencyLabel = "3 - Low",
            Category = "hardware",
            CategoryLabel = "Hardware",
            ContactType = "phone",
            ContactTypeLabel = "Phone",
            Caller = Jordan,
            AssignedTo = Jordan,
            AssignmentGroup = AusClientServices,
            Location = "Brisbane",
            CloseCode = "Solved (Permanently)",
            CloseCodeLabel = "Solved (Permanently)",
            CloseNotes = "Cleared the jam.",
            OpenedAtDisplay = "2026-09-18 09:00",
            UpdatedAtDisplay = "2026-09-18 09:30",
            UpdatedAtValue = "2026-09-18 09:30:00",
            Active = false
        });

        var laptop = new RequestRecord
        {
            SysId = "req-laptop",
            Number = "REQ0010001",
            ShortDescription = "New laptop for analyst",
            Description = "Replacement laptop for the finance analyst.",
            RequestState = "in_process",
            RequestStateLabel = "In Process",
            Priority = "3",
            PriorityLabel = "3 - Moderate",
            SpecialInstructions = "Needs a dock and the finance software image.",
            RequestedFor = Jordan,
            OpenedBy = Alex,
            AssignedTo = Alex,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-09-24 09:00",
            UpdatedAtDisplay = "2026-09-28 09:00",
            UpdatedAtValue = "2026-09-28 09:00:00",
            Active = true,
            StageLabel = "Fulfillment",
            ApprovalLabel = "Approved"
        };
        var access = new RequestRecord
        {
            SysId = "req-access",
            Number = "REQ0010002",
            ShortDescription = "Badge access to the lab",
            Description = "Grant lab door access for the new technician.",
            RequestState = "requested",
            RequestStateLabel = "Requested",
            Priority = "3",
            PriorityLabel = "3 - Moderate",
            RequestedFor = Sam,
            OpenedBy = Alex,
            OpenedAtDisplay = "2026-09-29 07:40",
            UpdatedAtDisplay = "2026-09-29 07:40",
            UpdatedAtValue = "2026-09-29 07:40:00",
            Active = true,
            StageLabel = "Requested",
            ApprovalLabel = "Requested"
        };
        var monitorRequest = new RequestRecord
        {
            SysId = "req-monitor",
            Number = "REQ0010003",
            ShortDescription = "Monitor replacement",
            Description = "The desk monitor flickers and needs a replacement.",
            RequestState = "closed_complete",
            RequestStateLabel = "Closed Complete",
            Priority = "4",
            PriorityLabel = "4 - Low",
            RequestedFor = Jordan,
            OpenedBy = Alex,
            OpenedAtDisplay = "2026-09-12 10:00",
            UpdatedAtDisplay = "2026-09-14 15:00",
            UpdatedAtValue = "2026-09-14 15:00:00",
            Active = false,
            StageLabel = "Completed",
            ApprovalLabel = "Approved"
        };
        _requests.AddRange([laptop, access, monitorRequest]);
        RememberJournal(
            laptop.SysId,
            new JournalEntry("journal-req-comment", "comments", "Customer comment", "Please include the finance software image.", "jordan.lee", "2026-09-24 09:20"),
            new JournalEntry("journal-req-work", "work_notes", "Work note", "Image is staged and waiting on the dock.", "alex.rivera", "2026-09-28 08:15"));

        _items.AddRange(
        [
            new RequestedItemRecord
            {
                SysId = "ritm-laptop",
                Number = "RITM0010001",
                ShortDescription = "Standard laptop",
                Description = "Standard laptop for the finance analyst.",
                State = "2",
                StateLabel = "Work in Progress",
                Priority = "3",
                PriorityLabel = "3 - Moderate",
                StageLabel = "Fulfillment",
                Quantity = "1",
                Request = new ReferenceValue(laptop.SysId, laptop.Number),
                CatalogItem = new ReferenceValue("cat-laptop", "Standard laptop"),
                AssignedTo = Alex,
                AssignmentGroup = ClientServices,
                ServiceOffering = new ReferenceValue("offering-euc", "End-user computing"),
                ConfigurationItem = new ReferenceValue("ci-laptop", "LAPTOP-FIN-014"),
                OpenedAtDisplay = "2026-09-24 09:00",
                UpdatedAtDisplay = "2026-09-28 09:00",
                UpdatedAtValue = "2026-09-28 09:00:00",
                Active = true
            },
            new RequestedItemRecord
            {
                SysId = "ritm-dock",
                Number = "RITM0010002",
                ShortDescription = "USB-C dock",
                Description = "Dock for the new laptop.",
                State = "1",
                StateLabel = "Open",
                Priority = "4",
                PriorityLabel = "4 - Low",
                StageLabel = "Waiting for approval",
                Quantity = "1",
                Request = new ReferenceValue(laptop.SysId, laptop.Number),
                CatalogItem = new ReferenceValue("cat-dock", "USB-C dock"),
                AssignmentGroup = ClientServices,
                OpenedAtDisplay = "2026-09-24 09:01",
                UpdatedAtDisplay = "2026-09-24 09:01",
                UpdatedAtValue = "2026-09-24 09:01:00",
                Active = true
            },
            new RequestedItemRecord
            {
                SysId = "ritm-badge",
                Number = "RITM0010003",
                ShortDescription = "Lab badge access",
                Description = "Add the lab door to the technician badge.",
                State = "2",
                StateLabel = "Work in Progress",
                Priority = "3",
                PriorityLabel = "3 - Moderate",
                Request = new ReferenceValue(access.SysId, access.Number),
                CatalogItem = new ReferenceValue("cat-badge", "Badge access"),
                AssignedTo = Jordan,
                AssignmentGroup = ClientServices,
                OpenedAtDisplay = "2026-09-29 07:40",
                UpdatedAtDisplay = "2026-09-29 07:40",
                UpdatedAtValue = "2026-09-29 07:40:00",
                Active = true,
                Quantity = "1"
            },
            new RequestedItemRecord
            {
                SysId = "ritm-monitor",
                Number = "RITM0010004",
                ShortDescription = "27 inch monitor",
                Description = "Replacement 27 inch monitor.",
                State = "3",
                StateLabel = "Closed Complete",
                CloseNotes = "Delivered to the desk.",
                Request = new ReferenceValue(monitorRequest.SysId, monitorRequest.Number),
                CatalogItem = new ReferenceValue("cat-monitor", "27 inch monitor"),
                AssignedTo = Alex,
                AssignmentGroup = ClientServices,
                OpenedAtDisplay = "2026-09-12 10:00",
                UpdatedAtDisplay = "2026-09-14 15:00",
                UpdatedAtValue = "2026-09-14 15:00:00",
                Active = false,
                Quantity = "1"
            }
        ]);
        RememberJournal(
            "ritm-laptop",
            new JournalEntry("journal-ritm-comment", "comments", "Customer comment", "I need the laptop before Monday.", "jordan.lee", "2026-09-25 11:00"),
            new JournalEntry("journal-ritm-work", "work_notes", "Work note", "Laptop is built and queued for delivery.", "alex.rivera", "2026-09-28 08:40"));

        _articles.AddRange(
        [
            new KnowledgeArticle
            {
                SysId = "kb-zephyr",
                Number = "KB0001001",
                ShortDescription = "Outlook shows a blank folder list",
                Text = "<p>Use <strong>zephyrmail</strong> when Outlook shows a blank folder list.</p><img src=\"sys_attachment.do?sys_id=outlook-folder\"><script>alert('xss')</script><style>body{color:red}</style><p>Close Outlook, then start it again.</p>",
                Topic = "Email",
                WorkflowState = "published",
                WorkflowStateLabel = "Published",
                Category = "Email",
                KnowledgeBase = "IT",
                Author = Alex,
                UpdatedAtDisplay = "2026-09-18 14:22",
                UpdatedAtValue = "2026-09-18 14:22:00",
                PublishedDisplay = "2026-09-18"
            },
            new KnowledgeArticle
            {
                SysId = "kb-toner",
                Number = "KB0001002",
                ShortDescription = "Replace a toner cartridge",
                Text = "<p>Open the front door, remove the old cartridge, and seat the new one until it clicks.</p><p>Print a test page before closing the ticket.</p>",
                Topic = "Hardware",
                WorkflowState = "published",
                WorkflowStateLabel = "Published",
                Category = "Printers",
                KnowledgeBase = "IT",
                Author = Jordan,
                UpdatedAtDisplay = "2026-08-02 09:00",
                UpdatedAtValue = "2026-08-02 09:00:00",
                PublishedDisplay = "2026-08-02"
            },
            new KnowledgeArticle
            {
                SysId = "kb-vpn",
                Number = "KB0001003",
                ShortDescription = "VPN client keeps disconnecting",
                Text = "<p>Reinstall the VPN client and uncheck split tunneling.</p><ul><li>Save the profile first</li><li>Restart the laptop</li></ul>",
                Topic = "Network",
                WorkflowState = "published",
                WorkflowStateLabel = "Published",
                Category = "Network",
                KnowledgeBase = "IT",
                Author = Sam,
                UpdatedAtDisplay = "2026-09-01 11:30",
                UpdatedAtValue = "2026-09-01 11:30:00",
                PublishedDisplay = "2026-09-01"
            }
        ]);

        AddInteraction(new InteractionRecord
        {
            SysId = "ims-password",
            Number = "IMS0010001",
            ShortDescription = "Password reset at the front desk",
            Description = "Sam walked up and is locked out of payroll.",
            State = "new",
            StateLabel = "New",
            Type = DefaultChoices.WalkUpType,
            TypeLabel = "Walk-up",
            OpenedFor = Sam,
            AssignedTo = Alex,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-10-01 09:10",
            UpdatedAtDisplay = "2026-10-01 09:12",
            UpdatedAtValue = "2026-10-01 09:12:00",
            Active = true
        },
            new JournalEntry("journal-walkup-comment", "comments", "Customer comment", "I am locked out of payroll at the front desk.", "sam.patel", "2026-10-01 09:05"),
            new JournalEntry("journal-walkup", "work_notes", "Work note", "Checked the badge photo against the payroll roster.", "alex.rivera", "2026-10-01 09:12"));

        AddInteraction(new InteractionRecord
        {
            SysId = "ims-badge",
            Number = "IMS0010002",
            ShortDescription = "Badge will not print",
            Description = "The front desk printer feeds a blank card.",
            State = "work_in_progress",
            StateLabel = "Work in Progress",
            Type = DefaultChoices.WalkUpType,
            TypeLabel = "Walk-up",
            OpenedFor = Jordan,
            AssignedTo = ReferenceValue.Empty,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-10-02 11:20",
            UpdatedAtDisplay = "2026-10-02 11:25",
            UpdatedAtValue = "2026-10-02 11:25:00",
            Active = true
        });

        AddIncident(new IncidentRecord
        {
            SysId = "inc-sla",
            Number = "INC0010010",
            ShortDescription = "VPN gateway SLA has breached",
            Description = "The gateway incident is past its resolution SLA.",
            State = "2",
            StateLabel = "In Progress",
            Priority = "2",
            PriorityLabel = "2 - High",
            Impact = "2",
            ImpactLabel = "2 - Medium",
            Urgency = "2",
            UrgencyLabel = "2 - Medium",
            Category = "network",
            CategoryLabel = "Network",
            ContactType = "phone",
            ContactTypeLabel = "Phone",
            Caller = Sam,
            AssignedTo = Alex,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-10-01 08:00",
            UpdatedAtDisplay = "2026-10-04 09:00",
            UpdatedAtValue = "2026-10-04 09:00:00",
            Active = true
        });
        _signals["inc-sla"] = new SampleAlertSignals { SlaBreached = true, UpdatedBy = "alex.rivera" };

        _items.Add(new RequestedItemRecord
        {
            SysId = "ritm-sla",
            Number = "RITM0010005",
            ShortDescription = "Laptop image is past its SLA",
            Description = "The image task is still in progress after the planned end.",
            State = "2",
            StateLabel = "Work in Progress",
            Priority = "3",
            PriorityLabel = "3 - Moderate",
            Quantity = "1",
            AssignedTo = ReferenceValue.Empty,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-10-01 08:00",
            UpdatedAtDisplay = "2026-10-04 09:10",
            UpdatedAtValue = "2026-10-04 09:10:00",
            Active = true
        });
        _signals["ritm-sla"] = new SampleAlertSignals { SlaStage = "in_progress", PlannedEnd = "2026-10-02 09:00:00" };

        AddInteraction(new InteractionRecord
        {
            SysId = "ims-sla",
            Number = "IMS0010003",
            ShortDescription = "Walk-up waiting past the SLA",
            Description = "The visitor is still at the desk after the response SLA.",
            State = "work_in_progress",
            StateLabel = "Work in Progress",
            Type = DefaultChoices.WalkUpType,
            TypeLabel = "Walk-up",
            OpenedFor = Sam,
            AssignedTo = Alex,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-10-03 10:00",
            UpdatedAtDisplay = "2026-10-04 10:00",
            UpdatedAtValue = "2026-10-04 10:00:00",
            Active = true
        });
        _signals["ims-sla"] = new SampleAlertSignals { SlaBreached = true };

        AddIncident(new IncidentRecord
        {
            SysId = "inc-hold",
            Number = "INC0010011",
            ShortDescription = "On hold past the caller follow-up",
            Description = "Waiting on the caller, and the follow-up time has passed.",
            State = "3",
            StateLabel = "On Hold",
            HoldReason = "awaiting_caller",
            HoldReasonLabel = "Awaiting Caller",
            Priority = "3",
            PriorityLabel = "3 - Moderate",
            Impact = "3",
            ImpactLabel = "3 - Low",
            Urgency = "3",
            UrgencyLabel = "3 - Low",
            Category = "inquiry",
            CategoryLabel = "Inquiry / Help",
            ContactType = "phone",
            ContactTypeLabel = "Phone",
            Caller = Jordan,
            AssignedTo = Jordan,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-09-20 09:00",
            UpdatedAtDisplay = "2026-10-01 09:00",
            UpdatedAtValue = "2026-10-01 09:00:00",
            Active = true
        });
        _signals["inc-hold"] = new SampleAlertSignals { FollowUp = "2026-10-01 09:00:00", UpdatedBy = "alex.rivera" };

        _items.Add(new RequestedItemRecord
        {
            SysId = "ritm-hold",
            Number = "RITM0010006",
            ShortDescription = "Dock request on hold past follow-up",
            Description = "The dock is on hold and the follow-up was yesterday.",
            State = "on_hold",
            StateLabel = "On Hold",
            Priority = "4",
            PriorityLabel = "4 - Low",
            Quantity = "1",
            AssignedTo = Jordan,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-09-22 09:00",
            UpdatedAtDisplay = "2026-10-01 11:00",
            UpdatedAtValue = "2026-10-01 11:00:00",
            Active = true
        });
        _signals["ritm-hold"] = new SampleAlertSignals { FollowUp = "2026-10-01 11:00:00" };

        AddInteraction(new InteractionRecord
        {
            SysId = "ims-hold",
            Number = "IMS0010004",
            ShortDescription = "Walk-up on hold past follow-up",
            Description = "The walk-up was parked and the follow-up time passed.",
            State = "on_hold",
            StateLabel = "On Hold",
            Type = DefaultChoices.WalkUpType,
            TypeLabel = "Walk-up",
            OpenedFor = Jordan,
            AssignedTo = Jordan,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-09-29 14:00",
            UpdatedAtDisplay = "2026-10-02 14:00",
            UpdatedAtValue = "2026-10-02 14:00:00",
            Active = true
        });
        _signals["ims-hold"] = new SampleAlertSignals { FollowUp = "2026-10-02 14:00:00" };

        AddIncident(new IncidentRecord
        {
            SysId = "inc-caller",
            Number = "INC0010012",
            ShortDescription = "Caller updated the VPN notes",
            Description = "The latest update on this incident was made by the caller.",
            State = "2",
            StateLabel = "In Progress",
            Priority = "3",
            PriorityLabel = "3 - Moderate",
            Impact = "3",
            ImpactLabel = "3 - Low",
            Urgency = "2",
            UrgencyLabel = "2 - Medium",
            Category = "network",
            CategoryLabel = "Network",
            ContactType = "email",
            ContactTypeLabel = "Email",
            Caller = Jordan,
            AssignedTo = Sam,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-10-03 08:00",
            UpdatedAtDisplay = "2026-10-05 08:30",
            UpdatedAtValue = "2026-10-05 08:30:00",
            Active = true
        });
        _signals["inc-caller"] = new SampleAlertSignals { UpdatedBy = "jordan.lee" };

        AddIncident(new IncidentRecord
        {
            SysId = "inc-returned",
            Number = "INC0010013",
            ShortDescription = "Returned with a note from another analyst",
            Description = "Someone other than the caller or the assignee wrote the latest note. The assignee is blank.",
            State = "2",
            StateLabel = "In Progress",
            Priority = "3",
            PriorityLabel = "3 - Moderate",
            Impact = "3",
            ImpactLabel = "3 - Low",
            Urgency = "3",
            UrgencyLabel = "3 - Low",
            Category = "software",
            CategoryLabel = "Software",
            ContactType = "email",
            ContactTypeLabel = "Email",
            Caller = Jordan,
            AssignedTo = ReferenceValue.Empty,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-10-03 12:00",
            UpdatedAtDisplay = "2026-10-05 15:00",
            UpdatedAtValue = "2026-10-05 15:00:00",
            Active = true
        },
            new JournalEntry("journal-returned-old", "comments", "Customer comment", "I added a comment first.", "jordan.lee", "2026-10-04 09:00"),
            new JournalEntry("journal-returned-new", "work_notes", "Work note", "Sending this back with what I found.", "casey.ng", "2026-10-05 15:00"));
        _signals["inc-returned"] = new SampleAlertSignals { UpdatedBy = "alex.rivera" };

        AddIncident(new IncidentRecord
        {
            SysId = "inc-outside",
            Number = "INC0010014",
            ShortDescription = "Network SLA outside the watched population",
            Description = "This breached SLA belongs to Network and is not assigned to Alex.",
            State = "2",
            StateLabel = "In Progress",
            Priority = "2",
            PriorityLabel = "2 - High",
            Impact = "2",
            ImpactLabel = "2 - Medium",
            Urgency = "2",
            UrgencyLabel = "2 - Medium",
            Category = "network",
            CategoryLabel = "Network",
            ContactType = "phone",
            ContactTypeLabel = "Phone",
            Caller = Sam,
            AssignedTo = Sam,
            AssignmentGroup = Network,
            Location = "Sydney",
            OpenedAtDisplay = "2026-10-01 08:00",
            UpdatedAtDisplay = "2026-10-04 08:00",
            UpdatedAtValue = "2026-10-04 08:00:00",
            Active = true
        });
        _signals["inc-outside"] = new SampleAlertSignals { SlaBreached = true, UpdatedBy = "sam.patel" };

        AddIncident(new IncidentRecord
        {
            SysId = "inc-resolved-today",
            Number = "INC0010015",
            ShortDescription = "Resolved today and still marked active",
            Description = "This incident was resolved today. The breach flag stays true, and it stays out of the queues.",
            State = "6",
            StateLabel = "Resolved",
            Priority = "1",
            PriorityLabel = "1 - Critical",
            Impact = "1",
            ImpactLabel = "1 - High",
            Urgency = "1",
            UrgencyLabel = "1 - High",
            Category = "software",
            CategoryLabel = "Software",
            ContactType = "phone",
            ContactTypeLabel = "Phone",
            Caller = Jordan,
            AssignedTo = Alex,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-10-06 08:00",
            UpdatedAtDisplay = "2026-10-06 09:00",
            UpdatedAtValue = "2026-10-06 09:00:00",
            Active = true
        });
        _signals["inc-resolved-today"] = new SampleAlertSignals { SlaBreached = true, UpdatedBy = "alex.rivera" };

        AddIncident(new IncidentRecord
        {
            SysId = "inc-sla-colleague",
            Number = "INC0010016",
            ShortDescription = "Colleague's breached SLA in the same group",
            Description = "Assigned to someone else in Client Services, so it is not Alex's notification.",
            State = "2",
            StateLabel = "In Progress",
            Priority = "2",
            PriorityLabel = "2 - High",
            Impact = "2",
            ImpactLabel = "2 - Medium",
            Urgency = "2",
            UrgencyLabel = "2 - Medium",
            Category = "network",
            CategoryLabel = "Network",
            ContactType = "phone",
            ContactTypeLabel = "Phone",
            Caller = Sam,
            AssignedTo = Jordan,
            AssignmentGroup = ClientServices,
            OpenedAtDisplay = "2026-10-02 08:00",
            UpdatedAtDisplay = "2026-10-04 11:00",
            UpdatedAtValue = "2026-10-04 11:00:00",
            Active = true
        });
        _signals["inc-sla-colleague"] = new SampleAlertSignals { SlaBreached = true };

        AddIncident(new IncidentRecord
        {
            SysId = "inc-aus-open",
            Number = "INC0010018",
            ShortDescription = "Unassigned in the watched group outside the office list",
            Description = "The watched group queue includes this even though the location is not an office city.",
            State = "1",
            StateLabel = "New",
            Priority = "2",
            PriorityLabel = "2 - High",
            Impact = "2",
            ImpactLabel = "2 - Medium",
            Urgency = "2",
            UrgencyLabel = "2 - Medium",
            Category = "hardware",
            CategoryLabel = "Hardware",
            ContactType = "phone",
            ContactTypeLabel = "Phone",
            Caller = Jordan,
            AssignedTo = ReferenceValue.Empty,
            AssignmentGroup = AusClientServices,
            Location = "Melbourne",
            OpenedAtDisplay = "2026-10-01 08:00",
            UpdatedAtDisplay = "2026-10-06 08:00",
            UpdatedAtValue = "2026-10-06 08:00:00",
            Active = true
        });
        SeedHardware();
    }

    private void SeedHardware()
    {
        _stockrooms.Add(new ReferenceSuggestion("stock-bne", "Brisbane", ""));
        _locations.Add(new ReferenceSuggestion("loc-bne", "Brisbane Office", ""));
        _locations.Add(new ReferenceSuggestion("loc-syd", "Sydney Office", ""));
        _locations.Add(new ReferenceSuggestion("loc-hkg", "Hong Kong Office", ""));

        _hardware.Add(new HardwareAsset
        {
            SysId = "hw-transit",
            SerialNumber = "5CG6245F8S",
            DisplayName = "HP HP ZBook Ultra G1a 14 inch Mobile Workstation PC",
            Model = "HP ZBook Ultra G1a 14 inch Mobile Workstation PC",
            ModelCategory = HardwareCatalog.Computer,
            AssignedTo = ReferenceValue.Empty,
            Location = new ReferenceValue("loc-bne", "Brisbane Office"),
            Stockroom = ReferenceValue.Empty,
            InstallStatus = HardwareCatalog.InTransit,
            InstallStatusLabel = HardwareCatalog.InTransit,
            Comments = "For testing by Mark Lindsay"
        });
        _hardware.Add(new HardwareAsset
        {
            SysId = "hw-inuse",
            SerialNumber = "5CG0000DBR",
            DisplayName = "HP ZBook Ultra 16 inch G1i Mobile Workstation",
            Model = "HP ZBook Ultra 16 inch G1i Mobile Workstation",
            ModelCategory = HardwareCatalog.Computer,
            AssignedTo = Jordan,
            Location = new ReferenceValue("loc-syd", "Sydney Office"),
            Stockroom = ReferenceValue.Empty,
            InstallStatus = HardwareCatalog.InUse,
            InstallStatusLabel = HardwareCatalog.InUse,
            Comments = "Assigned laptop"
        });
        _hardware.Add(new HardwareAsset
        {
            SysId = "hw-stock",
            SerialNumber = "5CG30710BR",
            DisplayName = "HP ZBook Fury 16 G9",
            Model = "HP ZBook Fury 16 G9",
            ModelCategory = HardwareCatalog.Computer,
            AssignedTo = ReferenceValue.Empty,
            Location = new ReferenceValue("loc-hkg", "Hong Kong Office"),
            Stockroom = new ReferenceValue("stock-bne", "Brisbane"),
            InstallStatus = HardwareCatalog.InStock,
            InstallStatusLabel = HardwareCatalog.InStock,
            Substatus = HardwareCatalog.Available,
            SubstatusLabel = HardwareCatalog.Available,
            Comments = "On the shelf"
        });
        _hardware.Add(new HardwareAsset
        {
            SysId = "hw-hp-case",
            SerialNumber = "5CD6220GYW",
            DisplayName = "HP ZBook",
            Model = "HP ZBook",
            ModelCategory = HardwareCatalog.Computer,
            AssignedTo = ReferenceValue.Empty,
            Location = new ReferenceValue("loc-bne", "Brisbane Office"),
            Stockroom = ReferenceValue.Empty,
            InstallStatus = HardwareCatalog.InTransit,
            InstallStatusLabel = HardwareCatalog.InTransit,
            Comments = "HP serial stored in uppercase"
        });
        _hardware.Add(new HardwareAsset
        {
            SysId = "hw-dell",
            SerialNumber = "ABCDEFG",
            DisplayName = "Dell Latitude",
            Model = "Dell Latitude",
            ModelCategory = HardwareCatalog.Computer,
            AssignedTo = ReferenceValue.Empty,
            Location = new ReferenceValue("loc-syd", "Sydney Office"),
            Stockroom = ReferenceValue.Empty,
            InstallStatus = HardwareCatalog.InTransit,
            InstallStatusLabel = HardwareCatalog.InTransit,
            Comments = "Dell service tag"
        });
    }

    private sealed class SampleAlertSignals
    {
        public bool SlaBreached { get; init; }
        public string SlaStage { get; init; } = "";
        public string PlannedEnd { get; init; } = "";
        public string FollowUp { get; init; } = "";
        public string UpdatedBy { get; init; } = "";
    }

    private void RememberJournal(string sysId, params JournalEntry[] notes)
    {
        if (notes.Length > 0)
            _journal[sysId] = notes.ToList();
    }

    private void AddInteraction(InteractionRecord record, params JournalEntry[] notes)
    {
        _interactions.Add(record);
        if (notes.Length > 0)
            _journal[record.SysId] = notes.ToList();
    }

    private void AddIncident(IncidentRecord record, params JournalEntry[] notes)
    {
        _incidents.Add(record);
        if (notes.Length > 0)
            _journal[record.SysId] = notes.ToList();
    }

    private static readonly ReferenceSuggestion[] Users =
    [
        new("sample-user", "Alex Rivera", "alex.rivera@example.com") { UserName = "alex.rivera", Email = "alex.rivera@example.com" },
        new("user-jordan", "Jordan Lee", "jordan.lee@example.com") { UserName = "jordan.lee", Email = "jordan.lee@example.com" },
        new("user-sam", "Sam Patel", "sam.patel@example.com") { UserName = "sam.patel", Email = "sam.patel@example.com" },
        new("user-casey", "Casey Ng", "casey.ng@example.com") { UserName = "casey.ng", Email = "casey.ng@example.com" },
        new("user-casey2", "Casey Ng", "casey.ng2@example.com") { UserName = "casey.ng2", Email = "casey.ng2@example.com" }
    ];

    private static readonly ReferenceSuggestion[] Groups =
    [
        new("group-cs", "Client Services", "Service desk"),
        new("group-aus", "Aus DT - Client Services", "Queensland client services"),
        new("group-net", "Network", "Network operations")
    ];

    private static readonly Choice[] SampleOfferings =
    [
        new("offering-euc", "End-user computing"),
        new("offering-network", "Network access"),
        new("offering-print", "Printing")
    ];

    private static readonly Choice[] SampleConfigurationItems =
    [
        new("ci-printer", "HQ-PRINTER-01"),
        new("ci-laptop", "LAPTOP-FIN-014"),
        new("ci-vpn", "VPN-GATEWAY")
    ];

    private static readonly Choice[] ExtraConfigurationItems =
    [
        new("ci-switch", "CORE-SWITCH-02")
    ];

    private static Choice[] AllConfigurationItems => SampleConfigurationItems.Concat(ExtraConfigurationItems).ToArray();

    private static readonly CatalogItemSummary[] Catalog =
    [
        new("cat-laptop", "Standard laptop", "Windows or macOS laptop with a dock"),
        new("cat-monitor", "27 inch monitor", "Desk monitor for an existing computer")
    ];

    private bool Passes(
        TicketQuery query,
        string assignedTo,
        string groupId,
        string requestedFor,
        string openedBy,
        bool active,
        string number,
        IEnumerable<string> haystack,
        DeskSection? section = null,
        string? stateValue = null,
        string? stateLabel = null,
        string? openedAt = null,
        bool honorRecordFilters = true)
    {
        if (query.Activity == ActivityFilter.Open)
        {
            if (!active)
                return false;
            if (section is DeskSection known && !AlertClassifier.IsStillOpen(known, stateValue, stateLabel))
                return false;
        }
        if (query.Activity == ActivityFilter.Closed && active)
            return false;

        if (honorRecordFilters)
        {
            if (!EncodedQuery.SameReference(query.AssignmentGroupId, groupId))
                return false;
            if (!EncodedQuery.SameReference(query.AssignedToId, assignedTo))
                return false;
            if (!EncodedQuery.OpenedInRange(query.OpenedFrom, query.OpenedTo, openedAt))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(query.AssignmentClause))
        {
            if (query.AssignmentClause.Contains("requested_for", StringComparison.Ordinal) && requestedFor != Me.SysId)
                return false;
            if (query.AssignmentClause.Contains("opened_by", StringComparison.Ordinal) && openedBy != Me.SysId)
                return false;
        }
        else
        {
            switch (query.Assignment)
            {
                case AssignmentScope.Mine when assignedTo != Me.SysId:
                    return false;
                case AssignmentScope.Unassigned when assignedTo.Length != 0:
                    return false;
                case AssignmentScope.MyGroups when groupId != ClientServices.SysId:
                    return false;
            }
        }

        return EncodedQuery.Matches(query.Text, number, haystack);
    }

    private static string IdOf(ReferenceValue value) => value.SysId ?? "";

    private string JournalText(string sysId) =>
        _journal.TryGetValue(sysId, out var notes) ? string.Join('\n', notes.Select(note => note.Text)) : "";

    private static IEnumerable<string> Texts(params string[] values) => values;

    private PagedResult<T> Page<T>(IEnumerable<T> matches, TicketQuery query) where T : class
    {
        var ordered = matches.OrderByDescending(UpdatedValue).ToArray();
        var limit = Math.Clamp(query.Limit, 1, 100);
        Record("GET", "api/now/table");
        return new PagedResult<T>(ordered.Take(limit).ToArray(), ordered.Length);
    }

    private string UpdatedValue<T>(T record) => record switch
    {
        IncidentRecord incident => incident.UpdatedAtValue,
        RequestRecord request => request.UpdatedAtValue,
        RequestedItemRecord item => item.UpdatedAtValue,
        KnowledgeArticle article => article.UpdatedAtValue,
        InteractionRecord interaction => interaction.UpdatedAtValue,
        _ => ""
    };

    private static T Find<T>(List<T> source, string sysId, string label) where T : class
    {
        var found = source.FirstOrDefault(record => SysIdOf(record).Equals(sysId, StringComparison.OrdinalIgnoreCase));
        return found ?? throw new ServiceNowException(404, $"ServiceNow could not find that {label}.", null);
    }

    private void FindAny(string sysId)
    {
        if (_incidents.Any(record => record.SysId == sysId)
            || _requests.Any(record => record.SysId == sysId)
            || _items.Any(record => record.SysId == sysId)
            || _interactions.Any(record => record.SysId == sysId))
            return;
        throw new ServiceNowException(404, "ServiceNow could not find that record.", null);
    }

    private static string SysIdOf<T>(T record) => record switch
    {
        IncidentRecord incident => incident.SysId,
        RequestRecord request => request.SysId,
        RequestedItemRecord item => item.SysId,
        KnowledgeArticle article => article.SysId,
        InteractionRecord interaction => interaction.SysId,
        HardwareAsset asset => asset.SysId,
        _ => ""
    };

    private static void Replace<T>(List<T> source, T updated) where T : class
    {
        var index = source.FindIndex(record => SysIdOf(record) == SysIdOf(updated));
        if (index >= 0)
            source[index] = updated;
    }

    private static string Label(IReadOnlyList<Choice> choices, string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        return choices.FirstOrDefault(choice => choice.Value == value)?.Label ?? fallback;
    }

    private static ReferenceValue UserRef(string? sysId) => sysId switch
    {
        "sample-user" => Alex,
        "user-jordan" => Jordan,
        "user-sam" => Sam,
        null or "" => ReferenceValue.Empty,
        _ => new ReferenceValue(sysId, sysId)
    };

    private static ReferenceValue NamedRef(string? sysId, IReadOnlyList<Choice> known)
    {
        if (string.IsNullOrWhiteSpace(sysId))
            return ReferenceValue.Empty;
        var match = known.FirstOrDefault(choice => choice.Value.Equals(sysId, StringComparison.OrdinalIgnoreCase));
        return match is null ? new ReferenceValue(sysId, sysId) : new ReferenceValue(match.Value, match.Label);
    }

    private static ReferenceValue GroupRef(string? sysId) => sysId switch
    {
        "group-cs" => ClientServices,
        "group-aus" => AusClientServices,
        "group-net" => Network,
        null or "" => ReferenceValue.Empty,
        _ => new ReferenceValue(sysId, sysId)
    };

    private static IReadOnlyList<ReferenceSuggestion> SearchPeople(string text, IReadOnlyList<ReferenceSuggestion> source)
    {
        var term = (text ?? "").Trim();
        if (term.Length < 2)
            return [];
        return source.Where(person =>
            person.Display.Contains(term, StringComparison.OrdinalIgnoreCase)
            || person.Detail.Contains(term, StringComparison.OrdinalIgnoreCase)
            || person.UserName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || person.Email.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private string NextId(string prefix) => prefix + "-" + (++_sequence);

    private string NextNumber(string prefix) => prefix + "001" + (++_sequence).ToString("0000");

    private static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm");

    private void Record(string method, string path) =>
        _activity.Insert(0, new ApiActivity(DateTimeOffset.Now, method, path, 200, 1));
}
