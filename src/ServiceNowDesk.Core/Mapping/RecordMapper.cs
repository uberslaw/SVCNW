using System.Text.Json;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Mapping;

public static class RecordMapper
{
    public static IncidentRecord Incident(JsonElement record)
    {
        var state = SnowField.Read(record, "state");
        var priority = SnowField.Read(record, "priority");
        var impact = SnowField.Read(record, "impact");
        var urgency = SnowField.Read(record, "urgency");
        var category = SnowField.Read(record, "category");
        var subcategory = SnowField.Read(record, "subcategory");
        var contact = SnowField.Read(record, "contact_type");
        var closeCode = SnowField.Read(record, "close_code");
        var hold = SnowField.Read(record, "hold_reason");
        var updated = SnowField.Read(record, "sys_updated_on");
        return new IncidentRecord
        {
            SysId = SnowField.Read(record, "sys_id").Value,
            Number = SnowField.Read(record, "number").Display,
            ShortDescription = SnowField.Read(record, "short_description").Display,
            Description = SnowField.Read(record, "description").Display,
            State = state.Value,
            StateLabel = LabelOrValue(state),
            Priority = priority.Value,
            PriorityLabel = LabelOrValue(priority),
            Impact = impact.Value,
            ImpactLabel = LabelOrValue(impact),
            Urgency = urgency.Value,
            UrgencyLabel = LabelOrValue(urgency),
            Category = category.Value,
            CategoryLabel = LabelOrValue(category),
            Subcategory = subcategory.Value,
            SubcategoryLabel = LabelOrValue(subcategory),
            ContactType = contact.Value,
            ContactTypeLabel = LabelOrValue(contact),
            CloseCode = closeCode.Value,
            CloseCodeLabel = LabelOrValue(closeCode),
            CloseNotes = SnowField.Read(record, "close_notes").Display,
            HoldReason = hold.Value,
            HoldReasonLabel = LabelOrValue(hold),
            Caller = Reference(record, "caller_id"),
            AssignedTo = Reference(record, "assigned_to"),
            AssignmentGroup = Reference(record, "assignment_group"),
            OpenedAtDisplay = SnowField.Read(record, "opened_at").Display,
            UpdatedAtDisplay = updated.Display,
            UpdatedAtValue = updated.Value,
            Active = SnowField.IsTrue(SnowField.Read(record, "active"))
        };
    }

    public static RequestRecord Request(JsonElement record)
    {
        var state = SnowField.Read(record, "request_state");
        var priority = SnowField.Read(record, "priority");
        var updated = SnowField.Read(record, "sys_updated_on");
        return new RequestRecord
        {
            SysId = SnowField.Read(record, "sys_id").Value,
            Number = SnowField.Read(record, "number").Display,
            ShortDescription = SnowField.Read(record, "short_description").Display,
            Description = SnowField.Read(record, "description").Display,
            RequestState = state.Value,
            RequestStateLabel = LabelOrValue(state),
            Priority = priority.Value,
            PriorityLabel = LabelOrValue(priority),
            SpecialInstructions = SnowField.Read(record, "special_instructions").Display,
            ApprovalLabel = SnowField.Read(record, "approval").Display,
            StageLabel = SnowField.Read(record, "stage").Display,
            DueDate = SnowField.Read(record, "due_date").Display,
            RequestedFor = Reference(record, "requested_for"),
            OpenedBy = Reference(record, "opened_by"),
            OpenedAtDisplay = SnowField.Read(record, "opened_at").Display,
            UpdatedAtDisplay = updated.Display,
            UpdatedAtValue = updated.Value,
            Active = SnowField.IsTrue(SnowField.Read(record, "active"))
        };
    }

    public static RequestedItemRecord RequestedItem(JsonElement record)
    {
        var state = SnowField.Read(record, "state");
        var priority = SnowField.Read(record, "priority");
        var updated = SnowField.Read(record, "sys_updated_on");
        return new RequestedItemRecord
        {
            SysId = SnowField.Read(record, "sys_id").Value,
            Number = SnowField.Read(record, "number").Display,
            ShortDescription = SnowField.Read(record, "short_description").Display,
            Description = SnowField.Read(record, "description").Display,
            State = state.Value,
            StateLabel = LabelOrValue(state),
            Priority = priority.Value,
            PriorityLabel = LabelOrValue(priority),
            StageLabel = SnowField.Read(record, "stage").Display,
            Quantity = SnowField.Read(record, "quantity").Display,
            CloseNotes = SnowField.Read(record, "close_notes").Display,
            Request = Reference(record, "request"),
            CatalogItem = Reference(record, "cat_item"),
            AssignedTo = Reference(record, "assigned_to"),
            AssignmentGroup = Reference(record, "assignment_group"),
            OpenedAtDisplay = SnowField.Read(record, "opened_at").Display,
            UpdatedAtDisplay = updated.Display,
            UpdatedAtValue = updated.Value,
            Active = SnowField.IsTrue(SnowField.Read(record, "active"))
        };
    }

    public static InteractionRecord Interaction(JsonElement record)
    {
        var state = SnowField.Read(record, "state");
        var type = SnowField.Read(record, "type");
        var updated = SnowField.Read(record, "sys_updated_on");
        return new InteractionRecord
        {
            SysId = SnowField.Read(record, "sys_id").Value,
            Number = SnowField.Read(record, "number").Display,
            ShortDescription = SnowField.Read(record, "short_description").Display,
            Description = SnowField.Read(record, "description").Display,
            State = state.Value,
            StateLabel = LabelOrValue(state),
            Type = type.Value,
            TypeLabel = LabelOrValue(type),
            OpenedFor = Reference(record, "opened_for"),
            AssignedTo = Reference(record, "assigned_to"),
            AssignmentGroup = Reference(record, "assignment_group"),
            OpenedAtDisplay = SnowField.Read(record, "opened_at").Display,
            UpdatedAtDisplay = updated.Display,
            UpdatedAtValue = updated.Value,
            Active = SnowField.IsTrue(SnowField.Read(record, "active"))
        };
    }

    public static KnowledgeArticle Knowledge(JsonElement record)
    {
        var state = SnowField.Read(record, "workflow_state");
        var updated = SnowField.Read(record, "sys_updated_on");
        var category = SnowField.Read(record, "kb_category");
        var knowledgeBase = SnowField.Read(record, "kb_knowledge_base");
        return new KnowledgeArticle
        {
            SysId = SnowField.Read(record, "sys_id").Value,
            Number = SnowField.Read(record, "number").Display,
            ShortDescription = SnowField.Read(record, "short_description").Display,
            Text = ArticleBody(record),
            Topic = LabelOrValue(SnowField.Read(record, "topic")),
            WorkflowState = state.Value,
            WorkflowStateLabel = LabelOrValue(state),
            Category = LabelOrValue(category),
            KnowledgeBase = LabelOrValue(knowledgeBase),
            Author = Reference(record, "author"),
            UpdatedAtDisplay = updated.Display,
            UpdatedAtValue = updated.Value,
            PublishedDisplay = SnowField.Read(record, "published").Display
        };
    }

    public static JournalEntry Journal(JsonElement record)
    {
        var element = SnowField.Read(record, "element");
        var kind = element.Value;
        var label = kind switch
        {
            "work_notes" => "Work note",
            "comments" => "Customer comment",
            _ => string.IsNullOrWhiteSpace(element.Display) ? "Note" : element.Display
        };
        return new JournalEntry(
            SnowField.Read(record, "sys_id").Value,
            kind,
            label,
            SnowField.Read(record, "value").Display,
            SnowField.Read(record, "sys_created_by").Display,
            SnowField.Read(record, "sys_created_on").Display);
    }

    public static ReferenceValue Reference(JsonElement record, string name)
    {
        var field = SnowField.Read(record, name);
        return new ReferenceValue(field.Value, field.Display);
    }

    public static string LabelOrValue(SnowField field) =>
        string.IsNullOrWhiteSpace(field.Display) ? field.Value : field.Display;

    private static string ArticleBody(JsonElement record)
    {
        var text = SnowField.Read(record, "text");
        if (!string.IsNullOrWhiteSpace(text.Display))
            return text.Display;
        return text.Value;
    }
}

public static class ChangeJson
{
    public static string Serialize(IReadOnlyDictionary<string, string?> fields)
    {
        var payload = new Dictionary<string, string>();
        foreach (var field in fields)
        {
            if (field.Value is null)
                continue;
            payload[field.Key] = field.Value;
        }

        if (payload.Count == 0)
            throw new InvalidOperationException("There is nothing to send to ServiceNow.");

        return JsonSerializer.Serialize(payload);
    }

    public static string FromIncident(IncidentChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return Serialize(new Dictionary<string, string?>
        {
            ["short_description"] = changes.ShortDescription,
            ["description"] = changes.Description,
            ["caller_id"] = changes.CallerId,
            ["assigned_to"] = changes.ClearAssignedTo ? "" : changes.AssignedToId,
            ["assignment_group"] = changes.ClearAssignmentGroup ? "" : changes.AssignmentGroupId,
            ["state"] = changes.State,
            ["impact"] = changes.Impact,
            ["urgency"] = changes.Urgency,
            ["priority"] = changes.Priority,
            ["category"] = changes.Category,
            ["subcategory"] = changes.Subcategory,
            ["contact_type"] = changes.ContactType,
            ["close_code"] = changes.CloseCode,
            ["close_notes"] = changes.CloseNotes,
            ["hold_reason"] = changes.HoldReason
        });
    }

    public static string FromInteraction(InteractionChanges changes, bool creating)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return Serialize(new Dictionary<string, string?>
        {
            ["short_description"] = changes.ShortDescription,
            ["description"] = changes.Description,
            ["opened_for"] = changes.OpenedForId,
            ["assigned_to"] = changes.ClearAssignedTo ? "" : changes.AssignedToId,
            ["assignment_group"] = changes.ClearAssignmentGroup ? "" : changes.AssignmentGroupId,
            ["state"] = changes.State,
            ["type"] = creating ? "walkup" : changes.Type
        });
    }

    public static string FromRequest(RequestChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return Serialize(new Dictionary<string, string?>
        {
            ["short_description"] = changes.ShortDescription,
            ["description"] = changes.Description,
            ["special_instructions"] = changes.SpecialInstructions,
            ["requested_for"] = changes.RequestedForId,
            ["request_state"] = changes.RequestState,
            ["priority"] = changes.Priority,
            ["due_date"] = changes.DueDate
        });
    }

    public static string FromRequestedItem(RequestedItemChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return Serialize(new Dictionary<string, string?>
        {
            ["short_description"] = changes.ShortDescription,
            ["description"] = changes.Description,
            ["state"] = changes.State,
            ["priority"] = changes.Priority,
            ["assigned_to"] = changes.ClearAssignedTo ? "" : changes.AssignedToId,
            ["assignment_group"] = changes.ClearAssignmentGroup ? "" : changes.AssignmentGroupId,
            ["close_notes"] = changes.CloseNotes
        });
    }
}
