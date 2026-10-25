using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;
using ServiceNowDesk.QueueDay;

namespace ServiceNowDesk.Client;

public sealed partial class SampleServiceNowClient
{
    public int QueueDayListCalls { get; private set; }

    public int QueueDayLookupCalls { get; private set; }

    public Task<IReadOnlyList<QueueDayTicket>> ListOpenAssignedQueueAsync(
        string assigneeSysId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = (assigneeSysId ?? "").Trim();
        if (id.Length == 0)
            return Task.FromResult<IReadOnlyList<QueueDayTicket>>([]);

        QueueDayListCalls++;
        var rows = new List<QueueDayTicket>();
        lock (_data)
        {
            foreach (var table in QueueDayQuery.Tables)
                Record("GET", "api/now/table/" + table + "?sysparm_query=" + Uri.EscapeDataString(QueueDayQuery.OpenAssigned(id, table)));
            foreach (var record in _incidents)
            {
                if (OpenFor(id, record.AssignedTo.SysId, record.Active, DeskSection.Incidents, record.State, record.StateLabel))
                    rows.Add(ToQueueTicket("incident", record.Number, record.SysId, record.ShortDescription, record.State, record.StateLabel, record.AssignedTo, record.AssignmentGroup, record.UpdatedAtValue, record.Caller));
            }

            foreach (var record in _items)
            {
                if (!OpenFor(id, record.AssignedTo.SysId, record.Active, DeskSection.RequestedItems, record.State, record.StateLabel))
                    continue;
                var caller = ItemCaller(record);
                rows.Add(ToQueueTicket("sc_req_item", record.Number, record.SysId, record.ShortDescription, record.State, record.StateLabel, record.AssignedTo, record.AssignmentGroup, record.UpdatedAtValue, caller));
            }

            foreach (var record in _interactions)
            {
                if (!OpenFor(id, record.AssignedTo.SysId, record.Active, DeskSection.WalkUps, record.State, record.StateLabel, record.Type))
                    continue;
                rows.Add(ToQueueTicket("interaction", record.Number, record.SysId, record.ShortDescription, record.State, record.StateLabel, record.AssignedTo, record.AssignmentGroup, record.UpdatedAtValue, record.OpenedFor));
            }
        }

        return Task.FromResult<IReadOnlyList<QueueDayTicket>>(rows);
    }

    public Task<IReadOnlyList<QueueDayTicket>> LookupQueueTicketsAsync(
        IReadOnlyList<QueueDayTicketRef> tickets,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var wanted = (tickets ?? []).Where(ticket => !string.IsNullOrWhiteSpace(ticket.SysId)).ToArray();
        if (wanted.Length == 0)
            return Task.FromResult<IReadOnlyList<QueueDayTicket>>([]);

        QueueDayLookupCalls++;
        var rows = new List<QueueDayTicket>();
        lock (_data)
        {
            foreach (var table in QueueDayQuery.Tables)
            {
                var ids = wanted.Where(ticket => QueueDayQuery.NormalizeTable(ticket.Table) == table).Select(ticket => ticket.SysId);
                foreach (var query in QueueDayQuery.LookupQueries(table, ids))
                    Record("GET", "api/now/table/" + table + "?sysparm_query=" + Uri.EscapeDataString(query));
            }

            foreach (var ticket in wanted)
            {
                var table = QueueDayQuery.NormalizeTable(ticket.Table);
                var match = table switch
                {
                    "sc_req_item" => FindItem(ticket.SysId),
                    "interaction" => FindInteraction(ticket.SysId),
                    _ => FindIncident(ticket.SysId)
                };
                if (match is not null)
                    rows.Add(match);
            }
        }

        return Task.FromResult<IReadOnlyList<QueueDayTicket>>(rows);
    }

    private bool OpenFor(
        string personId,
        string? assigneeId,
        bool active,
        DeskSection section,
        string stateValue,
        string stateLabel,
        string? interactionType = null)
    {
        if (!active || !string.Equals((assigneeId ?? "").Trim(), personId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (section == DeskSection.WalkUps)
        {
            var type = (interactionType ?? "").Trim();
            if (type.Length > 0 && !type.Equals(DefaultChoices.WalkUpType, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return AlertClassifier.IsStillOpen(section, stateValue, stateLabel);
    }

    private ReferenceValue ItemCaller(RequestedItemRecord record)
    {
        var parent = _requests.FirstOrDefault(request =>
            string.Equals(request.SysId, record.Request.SysId, StringComparison.OrdinalIgnoreCase));
        return parent is null ? ReferenceValue.Empty : parent.RequestedFor;
    }

    private QueueDayTicket? FindIncident(string sysId)
    {
        var record = _incidents.FirstOrDefault(item => item.SysId.Equals(sysId, StringComparison.OrdinalIgnoreCase));
        return record is null
            ? null
            : ToQueueTicket("incident", record.Number, record.SysId, record.ShortDescription, record.State, record.StateLabel, record.AssignedTo, record.AssignmentGroup, record.UpdatedAtValue, record.Caller);
    }

    private QueueDayTicket? FindItem(string sysId)
    {
        var record = _items.FirstOrDefault(item => item.SysId.Equals(sysId, StringComparison.OrdinalIgnoreCase));
        return record is null
            ? null
            : ToQueueTicket("sc_req_item", record.Number, record.SysId, record.ShortDescription, record.State, record.StateLabel, record.AssignedTo, record.AssignmentGroup, record.UpdatedAtValue, ItemCaller(record));
    }

    private QueueDayTicket? FindInteraction(string sysId)
    {
        var record = _interactions.FirstOrDefault(item => item.SysId.Equals(sysId, StringComparison.OrdinalIgnoreCase));
        return record is null
            ? null
            : ToQueueTicket("interaction", record.Number, record.SysId, record.ShortDescription, record.State, record.StateLabel, record.AssignedTo, record.AssignmentGroup, record.UpdatedAtValue, record.OpenedFor);
    }

    private QueueDayTicket ToQueueTicket(
        string table,
        string number,
        string sysId,
        string title,
        string stateValue,
        string stateLabel,
        ReferenceValue assignee,
        ReferenceValue group,
        string updatedOn,
        ReferenceValue caller)
    {
        var updated = (updatedOn ?? "").Trim();
        var updatedBy = _signals.TryGetValue(sysId, out var signals) ? signals.UpdatedBy ?? "" : "";
        return new QueueDayTicket(
            number ?? "",
            sysId ?? "",
            table,
            title ?? "",
            assignee.SysId ?? "",
            assignee.Display ?? "",
            UserNameOf(assignee),
            group.SysId ?? "",
            group.Display ?? "",
            stateValue ?? "",
            string.IsNullOrWhiteSpace(stateLabel) ? stateValue ?? "" : stateLabel,
            updated,
            AlertClassifier.TryParseInstant(updated, out var at) ? at : null,
            updatedBy,
            caller.SysId ?? "",
            caller.Display ?? "",
            UserNameOf(caller));
    }
}
