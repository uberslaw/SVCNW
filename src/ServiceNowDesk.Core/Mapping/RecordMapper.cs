using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ServiceNowDesk.Alerts;
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
            ServiceOffering = Reference(record, "service_offering"),
            ConfigurationItem = Reference(record, "cmdb_ci"),
            Location = LocationName(record),
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
        var hold = SnowField.Read(record, "hold_reason");
        var followUp = SnowField.Read(record, "follow_up");
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
            HoldReason = hold.Value,
            HoldReasonLabel = LabelOrValue(hold),
            FollowUp = followUp.Display,
            Request = Reference(record, "request"),
            CatalogItem = Reference(record, "cat_item"),
            AssignedTo = Reference(record, "assigned_to"),
            AssignmentGroup = Reference(record, "assignment_group"),
            ServiceOffering = Reference(record, "service_offering"),
            ConfigurationItem = Reference(record, "cmdb_ci"),
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
        var kind = JournalElement(element);
        var label = kind switch
        {
            "work_notes" => "Work note",
            "comments" => "Customer comment",
            _ => FirstFilled(element.Display, element.Value, "Note")
        };
        var table = SnowField.Read(record, "name");
        return new JournalEntry(
            SnowField.Read(record, "sys_id").Value,
            kind,
            label,
            ReadJournalText(record, "value"),
            JournalBody(SnowField.Read(record, "sys_created_by")),
            JournalBody(SnowField.Read(record, "sys_created_on")))
        {
            Table = FirstFilled(table.Value, table.Display, "")
        };
    }

    /// <summary>
    /// Work notes and customer comments as ServiceNow returns them on the parent record
    /// when <c>sysparm_display_value=all</c>. The readable text is often only in
    /// <c>display_value</c> while <c>value</c> is empty.
    /// </summary>
    public static IReadOnlyList<JournalEntry> ActivityHistory(JsonElement record)
    {
        var notes = new List<JournalEntry>();
        notes.AddRange(ParseActivity(ReadJournalText(record, "work_notes"), "work_notes"));
        var comments = ReadJournalText(record, "comments");
        if (string.IsNullOrWhiteSpace(comments))
            comments = ReadJournalText(record, "additional_comments");
        notes.AddRange(ParseActivity(comments, "comments"));
        return notes
            .OrderByDescending(note => AlertClassifier.TryParseInstant(note.CreatedDisplay, out var created) ? created : DateTime.MinValue)
            .ThenByDescending(note => note.SysId, StringComparer.Ordinal)
            .ToArray();
    }

    private static string JournalElement(SnowField element)
    {
        foreach (var candidate in new[] { element.Value, element.Display })
        {
            var normalized = NormalizeJournalToken(candidate);
            if (normalized.Length > 0)
                return normalized;
        }

        return FirstFilled(element.Value, element.Display, "");
    }

    private static string NormalizeJournalToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var compact = text.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        return compact switch
        {
            "work_notes" or "work_note" or "worknotes" => "work_notes",
            "comments" or "comment" or "additional_comments" or "additional_comment" or "customer_comment" or "customer_comments" => "comments",
            _ => ""
        };
    }

    private static string JournalBody(SnowField field) => FirstFilled(field.Display, field.Value, "");

    /// <summary>
    /// <c>sysparm_display_value=all</c> wraps each field as <c>{ value, display_value }</c>.
    /// Journal text may be only under <c>value</c>, only under <c>display_value</c>, or nested again.
    /// </summary>
    private static string ReadJournalText(JsonElement record, string name)
    {
        if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty(name, out var element))
            return "";
        return ReadTextNode(element);
    }

    private static string ReadTextNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return (element.GetString() ?? "").Trim();
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return "";
            case JsonValueKind.Object:
                var display = element.TryGetProperty("display_value", out var shown) ? ReadTextNode(shown) : "";
                if (!string.IsNullOrWhiteSpace(display))
                    return display;
                return element.TryGetProperty("value", out var raw) ? ReadTextNode(raw) : "";
            default:
                return SnowField.AsString(element).Trim();
        }
    }

    private static IReadOnlyList<JournalEntry> ParseActivity(string text, string fallbackKind)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var found = new List<JournalEntry>();
        string? when = null;
        string? author = null;
        string? kind = null;
        var body = new StringBuilder();
        var sawHeader = false;

        void Flush()
        {
            if (!sawHeader || when is null || kind is null)
                return;
            var note = body.ToString().Trim();
            body.Clear();
            if (note.Length == 0)
                return;
            found.Add(new JournalEntry(
                "activity-" + kind + "-" + found.Count + "-" + when,
                kind,
                kind == "comments" ? "Customer comment" : "Work note",
                note,
                author ?? "",
                when));
        }

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (TryActivityHeader(raw.Trim(), out var parsedWhen, out var parsedAuthor, out var parsedKind))
            {
                Flush();
                sawHeader = true;
                when = parsedWhen;
                author = parsedAuthor;
                kind = parsedKind;
                continue;
            }

            if (!sawHeader)
                continue;
            if (body.Length > 0)
                body.Append('\n');
            body.Append(raw.TrimEnd());
        }

        Flush();
        if (found.Count > 0)
            return found;

        var plain = text.Trim();
        if (plain.Length == 0)
            return [];
        return
        [
            new JournalEntry(
                "activity-" + fallbackKind + "-plain",
                fallbackKind,
                fallbackKind == "comments" ? "Customer comment" : "Work note",
                plain,
                "",
                "")
        ];
    }

    private static readonly Regex ActivityHeaderPattern = new(
        @"^(?<when>.+?) - (?<author>.+?) \((?<label>[^)]+)\)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static bool TryActivityHeader(string line, out string when, out string author, out string kind)
    {
        when = "";
        author = "";
        kind = "";
        var match = ActivityHeaderPattern.Match(line);
        if (!match.Success)
            return false;
        var normalized = NormalizeJournalToken(match.Groups["label"].Value);
        if (normalized.Length == 0)
            return false;
        when = match.Groups["when"].Value.Trim();
        author = match.Groups["author"].Value.Trim();
        kind = normalized;
        return when.Length > 0 && author.Length > 0;
    }

    private static string FirstFilled(string? first, string? second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
            return first.Trim();
        if (!string.IsNullOrWhiteSpace(second))
            return second.Trim();
        return fallback;
    }

    public static ReferenceValue Reference(JsonElement record, string name)
    {
        var field = SnowField.Read(record, name);
        return new ReferenceValue(field.Value, field.Display);
    }

    public static HardwareAsset Hardware(JsonElement record)
    {
        var status = SnowField.Read(record, "install_status");
        var substatus = SnowField.Read(record, "substatus");
        var model = Reference(record, "model");
        var serial = SnowField.Read(record, "serial_number");
        var displayName = SnowField.Read(record, "display_name");
        return new HardwareAsset
        {
            SysId = SnowField.Read(record, "sys_id").Value,
            SerialNumber = string.IsNullOrWhiteSpace(serial.Display) ? serial.Value : serial.Display,
            DisplayName = string.IsNullOrWhiteSpace(displayName.Display) ? displayName.Value : displayName.Display,
            Model = string.IsNullOrWhiteSpace(model.Display) ? model.SysId : model.Display,
            ModelCategory = SnowField.Read(record, "model_category").Display,
            AssignedTo = Reference(record, "assigned_to"),
            Location = Reference(record, "location"),
            Stockroom = Reference(record, "stockroom"),
            InstallStatus = status.Value,
            InstallStatusLabel = LabelOrValue(status),
            Substatus = substatus.Value,
            SubstatusLabel = string.IsNullOrWhiteSpace(substatus.Value) ? "" : LabelOrValue(substatus),
            Comments = SnowField.Read(record, "comments").Display
        };
    }

    private static string LocationName(JsonElement record)
    {
        var location = SnowField.Read(record, "location");
        return string.IsNullOrWhiteSpace(location.Display) ? location.Value : location.Display;
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
            ["hold_reason"] = changes.HoldReason,
            ["service_offering"] = changes.ClearServiceOffering ? "" : changes.ServiceOfferingId,
            ["cmdb_ci"] = changes.ClearConfigurationItem ? "" : changes.ConfigurationItemId
        });
    }

    public static string FromHardware(HardwareChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return Serialize(new Dictionary<string, string?>
        {
            ["assigned_to"] = changes.ClearAssignedTo ? "" : changes.AssignedToId,
            ["install_status"] = changes.InstallStatus,
            ["substatus"] = changes.ClearSubstatus ? "" : changes.Substatus,
            ["stockroom"] = changes.ClearStockroom ? "" : changes.StockroomId,
            ["location"] = changes.ClearLocation ? "" : changes.LocationId,
            ["comments"] = changes.Comments
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
            ["close_notes"] = changes.CloseNotes,
            ["service_offering"] = changes.ClearServiceOffering ? "" : changes.ServiceOfferingId,
            ["cmdb_ci"] = changes.ClearConfigurationItem ? "" : changes.ConfigurationItemId,
            ["requested_for"] = changes.RequestedForId,
            ["hold_reason"] = changes.HoldReason,
            ["follow_up"] = changes.FollowUp
        });
    }
}
