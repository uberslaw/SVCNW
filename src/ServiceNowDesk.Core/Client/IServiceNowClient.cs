using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Client;

public interface IServiceNowClient : IDisposable
{
    Uri? InstanceUri { get; }
    IReadOnlyList<ApiActivity> RecentActivity { get; }

    Task<CurrentUser> GetCurrentUserAsync(CancellationToken cancellationToken);

    Task<AlertSnapshot> GetOpenAlertsAsync(AlertSearch search, CancellationToken cancellationToken);

    Task<AlertReport> GetAlertReportAsync(AlertSearch search, CancellationToken cancellationToken);

    /// <summary>
    /// Open incidents with an empty assignee in the signed-in user's groups, plus the watched group when its name is set.
    /// Location is not applied.
    /// </summary>
    Task<IReadOnlyList<WatchedRecord>> ListUnassignedGroupQueueAsync(string? watchedGroupName, CancellationToken cancellationToken);

    Task<PagedResult<IncidentRecord>> SearchIncidentsAsync(TicketQuery query, CancellationToken cancellationToken);
    Task<IncidentRecord> GetIncidentAsync(string sysId, CancellationToken cancellationToken);
    Task<IncidentRecord> CreateIncidentAsync(IncidentChanges changes, CancellationToken cancellationToken);
    Task<IncidentRecord> UpdateIncidentAsync(string sysId, IncidentChanges changes, CancellationToken cancellationToken);
    Task<IncidentRecord> ResolveIncidentAsync(string sysId, string closeCode, string closeNotes, string resolvedState, CancellationToken cancellationToken);

    Task<PagedResult<RequestRecord>> SearchRequestsAsync(TicketQuery query, CancellationToken cancellationToken);
    Task<RequestRecord> GetRequestAsync(string sysId, CancellationToken cancellationToken);
    Task<RequestRecord> CreateRequestAsync(RequestChanges changes, CancellationToken cancellationToken);
    Task<RequestRecord> UpdateRequestAsync(string sysId, RequestChanges changes, CancellationToken cancellationToken);
    Task<RequestRecord> ResolveRequestAsync(string sysId, string requestState, string notes, CancellationToken cancellationToken);

    Task<PagedResult<RequestedItemRecord>> SearchRequestedItemsAsync(TicketQuery query, CancellationToken cancellationToken);
    Task<RequestedItemRecord> GetRequestedItemAsync(string sysId, CancellationToken cancellationToken);
    Task<RequestedItemRecord> CreateRequestedItemAsync(RequestedItemChanges changes, CancellationToken cancellationToken);
    Task<RequestedItemRecord> UpdateRequestedItemAsync(string sysId, RequestedItemChanges changes, CancellationToken cancellationToken);
    Task<RequestedItemRecord> ResolveRequestedItemAsync(string sysId, string state, string closeNotes, CancellationToken cancellationToken);

    Task<PagedResult<InteractionRecord>> SearchInteractionsAsync(TicketQuery query, CancellationToken cancellationToken);
    Task<InteractionRecord> GetInteractionAsync(string sysId, CancellationToken cancellationToken);
    Task<InteractionRecord> CreateInteractionAsync(InteractionChanges changes, CancellationToken cancellationToken);
    Task<InteractionRecord> UpdateInteractionAsync(string sysId, InteractionChanges changes, CancellationToken cancellationToken);
    Task<InteractionConversion> ConvertInteractionToIncidentAsync(string interactionSysId, CancellationToken cancellationToken);

    Task<PagedResult<KnowledgeArticle>> SearchKnowledgeAsync(TicketQuery query, CancellationToken cancellationToken);
    Task<KnowledgeArticle> GetKnowledgeAsync(string sysId, CancellationToken cancellationToken);
    Task<KnowledgeDownload> DownloadKnowledgeAsync(IProgress<DownloadTick>? progress, CancellationToken cancellationToken);
    Task<int?> CountPublishedKnowledgeAsync(CancellationToken cancellationToken);

    Task AddJournalAsync(string table, string sysId, JournalKind kind, string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<JournalEntry>> GetJournalAsync(string table, string sysId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Choice>> GetChoicesAsync(string table, string element, string? dependentValue, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReferenceSuggestion>> SearchUsersAsync(string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReferenceSuggestion>> MatchUsersAsync(string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReferenceSuggestion>> SearchGroupsAsync(string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<AttachmentSummary>> ListAttachmentsAsync(string tableName, string recordSysId, CancellationToken cancellationToken);
    Task<byte[]> DownloadAttachmentAsync(string attachmentSysId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Choice>> ListAssignmentGroupsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Choice>> ListGroupMembersAsync(string groupSysId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Choice>> ListServiceOfferingsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Choice>> ListConfigurationItemsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ReferenceSuggestion>> SearchConfigurationItemsAsync(string text, CancellationToken cancellationToken);

    Task<PagedResult<HardwareAsset>> SearchHardwareAsync(TicketQuery query, CancellationToken cancellationToken);
    Task<HardwareAsset> GetHardwareAsync(string sysId, CancellationToken cancellationToken);
    Task<HardwareAsset> UpdateHardwareAsync(string sysId, HardwareChanges changes, CancellationToken cancellationToken);
    Task<HardwareAsset?> FindHardwareBySerialAsync(string serial, bool ignoreCase, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReferenceSuggestion>> SearchStockroomsAsync(string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReferenceSuggestion>> SearchLocationsAsync(string text, CancellationToken cancellationToken);

    Task<IReadOnlyList<CatalogItemSummary>> SearchCatalogItemsAsync(string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<CatalogVariableDefinition>> GetCatalogVariablesAsync(string itemSysId, CancellationToken cancellationToken);
    Task<CatalogOrderResult> OrderCatalogItemAsync(
        string itemSysId,
        int quantity,
        string? requestedForSysId,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opened, resolved, and updated counts for the lead's defined team.
    /// An empty team returns the prompt and does not call ServiceNow.
    /// IMS is the walk-up <c>interaction</c> table.
    /// Updated counts start as one credit per person, per ticket, per local day.
    /// The report keeps the loaded events so the screen can compare each save without asking again.
    /// </summary>
    Task<WorkEffortReport> GetWorkEffortAsync(
        WorkEffortScale scale,
        DateTime localNow,
        IReadOnlyList<WorkEffortPerson> team,
        CancellationToken cancellationToken);

    /// <summary>
    /// Same count. While it runs, progress is the minute estimate and a bar.
    /// </summary>
    Task<WorkEffortReport> GetWorkEffortAsync(
        WorkEffortScale scale,
        DateTime localNow,
        IReadOnlyList<WorkEffortPerson> team,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Members of client services groups in <paramref name="city"/>.
    /// A blank city returns an empty list and does not query the instance.
    /// </summary>
    Task<IReadOnlyList<LockedLeadPerson>> ListLockedLeadTeamAsync(string? city, CancellationToken cancellationToken);
}
