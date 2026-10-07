using System.Text.Json;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Mapping;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Client;

public sealed partial class ServiceNowClient
{
    private const string AssignmentAuditFields = "documentkey,fieldname,newvalue,sys_created_on";
    private const int AssignmentAuditLimit = 200;

    /// <summary>
    /// Fills <see cref="AlertRecord.AssignedOn"/> from <c>sys_audit</c> for these rows only.
    /// A blocked or failed history read leaves the date blank and does not fail the rest of the report.
    /// </summary>
    private async Task<IReadOnlyList<AlertRecord>> AttachAssignmentTimesAsync(
        string userSysId,
        IReadOnlyList<AlertRecord> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
            return rows;

        var queries = AlertQueryBuilder.AssignmentAuditQueries(userSysId, rows.Select(row => row.SysId));
        if (queries.Count == 0)
            return rows;

        var times = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            try
            {
                await ReadAssignmentAuditAsync(userSysId, query, times, cancellationToken).ConfigureAwait(false);
            }
            catch (ServiceNowException)
            {
            }
        }

        if (times.Count == 0)
            return rows;

        return rows
            .Select(row => times.TryGetValue(row.SysId, out var when) ? row with { AssignedOn = when } : row)
            .ToArray();
    }

    private async Task ReadAssignmentAuditAsync(
        string userSysId,
        string query,
        Dictionary<string, string> times,
        CancellationToken cancellationToken)
    {
        var result = await GetListAsync("sys_audit", AssignmentAuditFields, query, AssignmentAuditLimit, 0, cancellationToken).ConfigureAwait(false);
        using (result)
        {
            foreach (var row in RequireArray(result.Document).EnumerateArray())
                ConsiderAssignmentAudit(userSysId, row, times);
        }
    }

    private static void ConsiderAssignmentAudit(string userSysId, JsonElement row, Dictionary<string, string> times)
    {
        var field = Text(SnowField.Read(row, "fieldname"));
        if (!field.Equals("assigned_to", StringComparison.OrdinalIgnoreCase))
            return;

        var assignee = Text(SnowField.Read(row, "newvalue"));
        if (!assignee.Equals(userSysId, StringComparison.OrdinalIgnoreCase))
            return;

        var key = Text(SnowField.Read(row, "documentkey"));
        if (key.Length == 0)
            return;

        var when = AuditInstant(SnowField.Read(row, "sys_created_on"));
        if (when.Length == 0)
            return;

        if (!times.TryGetValue(key, out var current) || IsNewer(when, current))
            times[key] = when;
    }

    private static string AuditInstant(SnowField field)
    {
        if (AlertClassifier.TryParseInstant(field.Display, out _))
            return field.Display;
        if (AlertClassifier.TryParseInstant(field.Value, out _))
            return field.Value;
        return "";
    }

    private static bool IsNewer(string candidate, string current)
    {
        if (!AlertClassifier.TryParseInstant(candidate, out var next))
            return false;
        if (!AlertClassifier.TryParseInstant(current, out var previous))
            return true;
        return next > previous;
    }

    private static string Text(SnowField field) => field.Value.Length > 0 ? field.Value : field.Display;
}
