using ServiceNowDesk.Models;

namespace ServiceNowDesk.Client;

public interface IServiceNowClient : IDisposable
{
    Uri? InstanceUri { get; }
    IReadOnlyList<ApiActivity> RecentActivity { get; }

    Task<CurrentUser> GetCurrentUserAsync(CancellationToken cancellationToken);

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
    Task<RequestedItemRecord> UpdateRequestedItemAsync(string sysId, RequestedItemChanges changes, CancellationToken cancellationToken);
    Task<RequestedItemRecord> ResolveRequestedItemAsync(string sysId, string state, string closeNotes, CancellationToken cancellationToken);

    Task<PagedResult<KnowledgeArticle>> SearchKnowledgeAsync(TicketQuery query, CancellationToken cancellationToken);
    Task<KnowledgeArticle> GetKnowledgeAsync(string sysId, CancellationToken cancellationToken);

    Task AddJournalAsync(string table, string sysId, JournalKind kind, string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<JournalEntry>> GetJournalAsync(string sysId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Choice>> GetChoicesAsync(string table, string element, string? dependentValue, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReferenceSuggestion>> SearchUsersAsync(string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReferenceSuggestion>> SearchGroupsAsync(string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<Choice>> ListAssignmentGroupsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Choice>> ListGroupMembersAsync(string groupSysId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CatalogItemSummary>> SearchCatalogItemsAsync(string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<CatalogVariableDefinition>> GetCatalogVariablesAsync(string itemSysId, CancellationToken cancellationToken);
    Task<CatalogOrderResult> OrderCatalogItemAsync(
        string itemSysId,
        int quantity,
        string? requestedForSysId,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken);
}
