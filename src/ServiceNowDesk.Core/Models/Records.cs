namespace ServiceNowDesk.Models;

public sealed record IncidentRecord
{
    public string SysId { get; init; } = "";
    public string Number { get; init; } = "";
    public string ShortDescription { get; init; } = "";
    public string Description { get; init; } = "";
    public string State { get; init; } = "";
    public string StateLabel { get; init; } = "";
    public string Priority { get; init; } = "";
    public string PriorityLabel { get; init; } = "";
    public string Impact { get; init; } = "";
    public string ImpactLabel { get; init; } = "";
    public string Urgency { get; init; } = "";
    public string UrgencyLabel { get; init; } = "";
    public string Category { get; init; } = "";
    public string CategoryLabel { get; init; } = "";
    public string Subcategory { get; init; } = "";
    public string SubcategoryLabel { get; init; } = "";
    public string ContactType { get; init; } = "";
    public string ContactTypeLabel { get; init; } = "";
    public string CloseCode { get; init; } = "";
    public string CloseCodeLabel { get; init; } = "";
    public string CloseNotes { get; init; } = "";
    public string HoldReason { get; init; } = "";
    public string HoldReasonLabel { get; init; } = "";
    public ReferenceValue Caller { get; init; }
    public ReferenceValue AssignedTo { get; init; }
    public ReferenceValue AssignmentGroup { get; init; }
    public ReferenceValue ServiceOffering { get; init; }
    public ReferenceValue ConfigurationItem { get; init; }
    public string Location { get; init; } = "";
    public string OpenedAtDisplay { get; init; } = "";
    public string UpdatedAtDisplay { get; init; } = "";
    public string UpdatedAtValue { get; init; } = "";
    public bool Active { get; init; } = true;
}

public sealed record RequestRecord
{
    public string SysId { get; init; } = "";
    public string Number { get; init; } = "";
    public string ShortDescription { get; init; } = "";
    public string Description { get; init; } = "";
    public string RequestState { get; init; } = "";
    public string RequestStateLabel { get; init; } = "";
    public string Priority { get; init; } = "";
    public string PriorityLabel { get; init; } = "";
    public string SpecialInstructions { get; init; } = "";
    public string ApprovalLabel { get; init; } = "";
    public string StageLabel { get; init; } = "";
    public string DueDate { get; init; } = "";
    public ReferenceValue RequestedFor { get; init; }
    public ReferenceValue OpenedBy { get; init; }
    public ReferenceValue AssignedTo { get; init; }
    public ReferenceValue AssignmentGroup { get; init; }
    public string OpenedAtDisplay { get; init; } = "";
    public string UpdatedAtDisplay { get; init; } = "";
    public string UpdatedAtValue { get; init; } = "";
    public bool Active { get; init; } = true;
}

public sealed record RequestedItemRecord
{
    public string SysId { get; init; } = "";
    public string Number { get; init; } = "";
    public string ShortDescription { get; init; } = "";
    public string Description { get; init; } = "";
    public string State { get; init; } = "";
    public string StateLabel { get; init; } = "";
    public string Priority { get; init; } = "";
    public string PriorityLabel { get; init; } = "";
    public string StageLabel { get; init; } = "";
    public string Quantity { get; init; } = "";
    public string CloseNotes { get; init; } = "";
    public ReferenceValue Request { get; init; }
    public ReferenceValue CatalogItem { get; init; }
    public ReferenceValue AssignedTo { get; init; }
    public ReferenceValue AssignmentGroup { get; init; }
    public ReferenceValue ServiceOffering { get; init; }
    public ReferenceValue ConfigurationItem { get; init; }
    public string OpenedAtDisplay { get; init; } = "";
    public string UpdatedAtDisplay { get; init; } = "";
    public string UpdatedAtValue { get; init; } = "";
    public bool Active { get; init; } = true;
}

public sealed record InteractionRecord
{
    public string SysId { get; init; } = "";
    public string Number { get; init; } = "";
    public string ShortDescription { get; init; } = "";
    public string Description { get; init; } = "";
    public string State { get; init; } = "";
    public string StateLabel { get; init; } = "";
    public string Type { get; init; } = "";
    public string TypeLabel { get; init; } = "";
    public ReferenceValue OpenedFor { get; init; }
    public ReferenceValue AssignedTo { get; init; }
    public ReferenceValue AssignmentGroup { get; init; }
    public string OpenedAtDisplay { get; init; } = "";
    public string UpdatedAtDisplay { get; init; } = "";
    public string UpdatedAtValue { get; init; } = "";
    public bool Active { get; init; } = true;
}

public sealed record InteractionConversion(IncidentRecord Incident, bool Created, string? LinkError);

public sealed record KnowledgeArticle
{
    public string SysId { get; init; } = "";
    public string Number { get; init; } = "";
    public string ShortDescription { get; init; } = "";
    public string Text { get; init; } = "";
    public string Topic { get; init; } = "";
    public string WorkflowState { get; init; } = "";
    public string WorkflowStateLabel { get; init; } = "";
    public string Category { get; init; } = "";
    public string KnowledgeBase { get; init; } = "";
    public ReferenceValue Author { get; init; }
    public string UpdatedAtDisplay { get; init; } = "";
    public string UpdatedAtValue { get; init; } = "";
    public string PublishedDisplay { get; init; } = "";
}

public sealed record CatalogItemSummary(string SysId, string Name, string ShortDescription);

public sealed record CatalogVariableDefinition(
    string Name,
    string Label,
    bool Mandatory,
    IReadOnlyList<Choice> Choices);

public sealed record CatalogOrderResult(string RequestSysId, string RequestNumber);

public sealed record AttachmentSummary(string SysId, string FileName);

public sealed class IncidentChanges
{
    public string? ShortDescription { get; init; }
    public string? Description { get; init; }
    public string? CallerId { get; init; }
    public string? AssignedToId { get; init; }
    public bool ClearAssignedTo { get; init; }
    public string? AssignmentGroupId { get; init; }
    public bool ClearAssignmentGroup { get; init; }
    public string? State { get; init; }
    public string? Impact { get; init; }
    public string? Urgency { get; init; }
    public string? Priority { get; init; }
    public string? Category { get; init; }
    public string? Subcategory { get; init; }
    public string? ContactType { get; init; }
    public string? CloseCode { get; init; }
    public string? CloseNotes { get; init; }
    public string? HoldReason { get; init; }
    public string? ServiceOfferingId { get; init; }
    public bool ClearServiceOffering { get; init; }
    public string? ConfigurationItemId { get; init; }
    public bool ClearConfigurationItem { get; init; }

    public bool HasChanges =>
        ShortDescription is not null
        || Description is not null
        || CallerId is not null
        || AssignedToId is not null
        || ClearAssignedTo
        || AssignmentGroupId is not null
        || ClearAssignmentGroup
        || State is not null
        || Impact is not null
        || Urgency is not null
        || Priority is not null
        || Category is not null
        || Subcategory is not null
        || ContactType is not null
        || CloseCode is not null
        || CloseNotes is not null
        || HoldReason is not null
        || ServiceOfferingId is not null
        || ClearServiceOffering
        || ConfigurationItemId is not null
        || ClearConfigurationItem;
}

public sealed class InteractionChanges
{
    public string? ShortDescription { get; init; }
    public string? Description { get; init; }
    public string? OpenedForId { get; init; }
    public string? AssignedToId { get; init; }
    public bool ClearAssignedTo { get; init; }
    public string? AssignmentGroupId { get; init; }
    public bool ClearAssignmentGroup { get; init; }
    public string? State { get; init; }
    public string? Type { get; init; }

    public bool HasChanges =>
        ShortDescription is not null
        || Description is not null
        || OpenedForId is not null
        || AssignedToId is not null
        || ClearAssignedTo
        || AssignmentGroupId is not null
        || ClearAssignmentGroup
        || State is not null
        || Type is not null;
}

public sealed class RequestChanges
{
    public string? ShortDescription { get; init; }
    public string? Description { get; init; }
    public string? SpecialInstructions { get; init; }
    public string? RequestedForId { get; init; }
    public string? RequestState { get; init; }
    public string? Priority { get; init; }
    public string? DueDate { get; init; }

    public bool HasChanges =>
        ShortDescription is not null
        || Description is not null
        || SpecialInstructions is not null
        || RequestedForId is not null
        || RequestState is not null
        || Priority is not null
        || DueDate is not null;
}

public sealed class RequestedItemChanges
{
    public string? ShortDescription { get; init; }
    public string? Description { get; init; }
    public string? State { get; init; }
    public string? Priority { get; init; }
    public string? AssignedToId { get; init; }
    public bool ClearAssignedTo { get; init; }
    public string? AssignmentGroupId { get; init; }
    public bool ClearAssignmentGroup { get; init; }
    public string? CloseNotes { get; init; }
    public string? ServiceOfferingId { get; init; }
    public bool ClearServiceOffering { get; init; }
    public string? ConfigurationItemId { get; init; }
    public bool ClearConfigurationItem { get; init; }

    public bool HasChanges =>
        ShortDescription is not null
        || Description is not null
        || State is not null
        || Priority is not null
        || AssignedToId is not null
        || ClearAssignedTo
        || AssignmentGroupId is not null
        || ClearAssignmentGroup
        || CloseNotes is not null
        || ServiceOfferingId is not null
        || ClearServiceOffering
        || ConfigurationItemId is not null
        || ClearConfigurationItem;
}
