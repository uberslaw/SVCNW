using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Query;

namespace ServiceNowDesk.QueueDay;

/// <summary>
/// Open assigned queues are one simple query per person per table.
/// Follow-up lookups are small <c>sys_idIN</c> chunks and never a packed <c>^NQ</c> query.
/// </summary>
public static class QueueDayQuery
{
    public const int LookupChunkSize = 20;

    public const int MaxRowsPerTable = 2000;

    public static readonly string[] Tables = ["incident", "sc_req_item", "interaction"];

    public static string NormalizeTable(string? table)
    {
        var name = (table ?? "").Trim().ToLowerInvariant();
        return name is "sc_req_item" or "interaction" or "incident" ? name : "";
    }

    public static DeskSection Section(string? table) => NormalizeTable(table) switch
    {
        "sc_req_item" => DeskSection.RequestedItems,
        "interaction" => DeskSection.WalkUps,
        _ => DeskSection.Incidents
    };

    /// <summary>
    /// Open tickets assigned to one person. Walk-ups are <c>interaction</c> rows of type walk-up.
    /// Unassigned group tickets are not part of this query.
    /// </summary>
    public static string OpenAssigned(string assigneeSysId, string table)
    {
        var id = EncodedQuery.SafeToken((assigneeSysId ?? "").Trim(), "user id");
        var section = Section(table);
        var query = "assigned_to=" + id + "^" + AlertQueryBuilder.StillWorking(section);
        if (section == DeskSection.WalkUps)
            query += "^type=" + DefaultChoices.WalkUpType;
        return query + "^ORDERBYDESCsys_updated_on";
    }

    public static IReadOnlyList<string> LookupQueries(string table, IEnumerable<string>? sysIds)
    {
        _ = table;
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in sysIds ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            try
            {
                var token = EncodedQuery.SafeToken(raw.Trim(), "record id");
                if (seen.Add(token))
                    ids.Add(token);
            }
            catch (InvalidOperationException)
            {
                // A bad id is omitted from the chunk. The ticket then counts as not returned.
            }
        }

        var queries = new List<string>();
        for (var start = 0; start < ids.Count; start += LookupChunkSize)
        {
            var chunk = string.Join(",", ids.Skip(start).Take(LookupChunkSize));
            queries.Add("sys_idIN" + chunk);
        }

        return queries;
    }
}
