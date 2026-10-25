using System.Text.Json;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Mapping;
using ServiceNowDesk.Models;
using ServiceNowDesk.QueueDay;

namespace ServiceNowDesk.Client;

public sealed partial class ServiceNowClient
{
    private const string QueueIncidentFields =
        "sys_id,number,short_description,state,assigned_to,assigned_to.user_name,assignment_group,sys_updated_on,sys_updated_by,caller_id,caller_id.user_name,active";

    private const string QueueItemFields =
        "sys_id,number,short_description,state,assigned_to,assigned_to.user_name,assignment_group,sys_updated_on,sys_updated_by,requested_for,requested_for.user_name,request.requested_for,request.requested_for.user_name,active";

    private const string QueueWalkUpFields =
        "sys_id,number,short_description,state,type,assigned_to,assigned_to.user_name,assignment_group,sys_updated_on,sys_updated_by,opened_for,opened_for.user_name,active";

    public async Task<IReadOnlyList<QueueDayTicket>> ListOpenAssignedQueueAsync(
        string assigneeSysId,
        CancellationToken cancellationToken)
    {
        var id = (assigneeSysId ?? "").Trim();
        if (id.Length == 0)
            return [];

        var found = new List<QueueDayTicket>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in QueueDayQuery.Tables)
        {
            var query = QueueDayQuery.OpenAssigned(id, table);
            var truncated = await PageRowsAsync(
                table,
                QueueFields(table),
                query,
                QueueDayQuery.MaxRowsPerTable,
                progress: null,
                row =>
                {
                    var ticket = MapQueueTicket(row, table, id, openOnly: true);
                    if (ticket is not null && seen.Add(ticket.SysId))
                        found.Add(ticket);
                },
                cancellationToken).ConfigureAwait(false);
            if (truncated)
            {
                throw new InvalidOperationException(
                    "Queue check stopped because " + table + " returned more than "
                    + QueueDayQuery.MaxRowsPerTable
                    + " open tickets for this person. The baseline was not saved.");
            }
        }

        return found;
    }

    public async Task<IReadOnlyList<QueueDayTicket>> LookupQueueTicketsAsync(
        IReadOnlyList<QueueDayTicketRef> tickets,
        CancellationToken cancellationToken)
    {
        var found = new List<QueueDayTicket>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in QueueDayQuery.Tables)
        {
            var ids = (tickets ?? [])
                .Where(ticket => QueueDayQuery.NormalizeTable(ticket.Table) == table)
                .Select(ticket => ticket.SysId);
            foreach (var query in QueueDayQuery.LookupQueries(table, ids))
            {
                var result = await GetListAsync(table, QueueFields(table), query, 200, 0, cancellationToken).ConfigureAwait(false);
                using (result)
                {
                    foreach (var row in RequireArray(result.Document).EnumerateArray())
                    {
                        var ticket = MapQueueTicket(row, table, requiredAssignee: null, openOnly: false);
                        if (ticket is not null && seen.Add(ticket.Key))
                            found.Add(ticket);
                    }
                }
            }
        }

        return found;
    }

    private static string QueueFields(string table) => table switch
    {
        "sc_req_item" => QueueItemFields,
        "interaction" => QueueWalkUpFields,
        _ => QueueIncidentFields
    };

    private static QueueDayTicket? MapQueueTicket(JsonElement row, string table, string? requiredAssignee, bool openOnly)
    {
        var sysId = SnowField.Read(row, "sys_id").Value.Trim();
        if (sysId.Length == 0)
            return null;

        var assignee = SnowField.Read(row, "assigned_to");
        var assigneeId = assignee.Value.Trim();
        var state = SnowField.Read(row, "state");
        if (openOnly)
        {
            if (string.IsNullOrWhiteSpace(requiredAssignee)
                || !assigneeId.Equals(requiredAssignee.Trim(), StringComparison.OrdinalIgnoreCase))
                return null;
            var active = SnowField.Read(row, "active");
            if (active.Value.Length > 0 && !SnowField.IsTrue(active))
                return null;
            if (table == "interaction")
            {
                var type = SnowField.Read(row, "type");
                var typeValue = (type.Value.Length > 0 ? type.Value : type.Display).Trim();
                if (typeValue.Length > 0 && !typeValue.Equals(DefaultChoices.WalkUpType, StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            if (!AlertClassifier.IsStillOpen(QueueDayQuery.Section(table), state.Value, state.Display))
                return null;
        }

        var group = SnowField.Read(row, "assignment_group");
        var updated = SnowField.Read(row, "sys_updated_on");
        var updatedText = updated.Display.Length > 0 ? updated.Display : updated.Value;
        var caller = ReadQueueCaller(row, table);
        var number = SnowField.Read(row, "number");
        var title = SnowField.Read(row, "short_description");
        return new QueueDayTicket(
            TextOr(number.Display, number.Value),
            sysId,
            table,
            TextOr(title.Display, title.Value),
            assigneeId,
            AssigneeName(row),
            FirstText(row, "assigned_to.user_name"),
            group.Value.Trim(),
            group.Display.Trim(),
            state.Value.Trim(),
            TextOr(state.Display, state.Value),
            updatedText.Trim(),
            AlertClassifier.TryParseInstant(updatedText, out var updatedAt) ? updatedAt : null,
            FirstText(row, "sys_updated_by"),
            caller.Id,
            caller.Name,
            caller.UserName);
    }

    private static (string Id, string Name, string UserName) ReadQueueCaller(JsonElement row, string table)
    {
        if (table == "sc_req_item")
        {
            var direct = ReadQueueRef(row, "requested_for", "requested_for.user_name");
            if (direct.Id.Length > 0 || direct.UserName.Length > 0)
                return direct;
            // Some items only carry the requester on the parent request.
            return ReadQueueRef(row, "request.requested_for", "request.requested_for.user_name");
        }

        if (table == "interaction")
            return ReadQueueRef(row, "opened_for", "opened_for.user_name");
        return ReadQueueRef(row, "caller_id", "caller_id.user_name");
    }

    private static (string Id, string Name, string UserName) ReadQueueRef(JsonElement row, string field, string userNameField)
    {
        var value = SnowField.Read(row, field);
        var id = value.Value.Trim();
        var name = value.Display.Trim();
        var userName = FirstText(row, userNameField);
        if (name.Length == 0 || name.Equals(id, StringComparison.OrdinalIgnoreCase))
            name = userName.Length > 0 ? userName : name;
        return (id, name, userName);
    }

    private static string TextOr(string preferred, string fallback)
    {
        var text = preferred?.Trim() ?? "";
        return text.Length > 0 ? text : (fallback ?? "").Trim();
    }
}
