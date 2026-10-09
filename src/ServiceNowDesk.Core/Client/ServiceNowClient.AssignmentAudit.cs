using System.Text.Json;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Mapping;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Client;

public sealed partial class ServiceNowClient
{
    private const string AssignmentAuditFields = "documentkey,fieldname,newvalue,sys_created_on";
    private const string AssignmentJournalFields = "sys_id,element_id,element,name,value,sys_created_on,sys_created_by";
    private const int AssignmentAuditLimit = 200;

    /// <summary>
    /// Fills <see cref="AlertRecord.AssignedOn"/> for these rows.
    /// Prefer <c>sys_audit</c> where <c>assigned_to</c> became this user. When that table is
    /// blocked or has no row, read journal / work notes for the newest line that assigns the
    /// ticket to the current assignee (or "me"). A total miss leaves the date blank and does
    /// not fail the rest of the report.
    /// </summary>
    private async Task<IReadOnlyList<AlertRecord>> AttachAssignmentTimesAsync(
        string userSysId,
        IReadOnlyList<AlertRecord> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
            return rows;

        var times = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await TryFillFromAuditAsync(userSysId, rows, times, cancellationToken).ConfigureAwait(false);

        var missing = rows.Where(row => !times.ContainsKey(row.SysId)).ToArray();
        if (missing.Length > 0)
            await TryFillFromJournalAsync(missing, times, cancellationToken).ConfigureAwait(false);

        if (times.Count == 0)
            return rows;

        return rows
            .Select(row => times.TryGetValue(row.SysId, out var when) ? row with { AssignedOn = when } : row)
            .ToArray();
    }

    private async Task TryFillFromAuditAsync(
        string userSysId,
        IReadOnlyList<AlertRecord> rows,
        Dictionary<string, string> times,
        CancellationToken cancellationToken)
    {
        var queries = AlertQueryBuilder.AssignmentAuditQueries(userSysId, rows.Select(row => row.SysId));
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
    }

    private async Task TryFillFromJournalAsync(
        IReadOnlyList<AlertRecord> rows,
        Dictionary<string, string> times,
        CancellationToken cancellationToken)
    {
        var queries = AlertQueryBuilder.AssignmentJournalQueries(rows.Select(row => row.SysId));
        if (queries.Count == 0)
            return;

        var notesByRecord = new Dictionary<string, List<JournalEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            try
            {
                await ReadAssignmentJournalAsync(query, notesByRecord, cancellationToken).ConfigureAwait(false);
            }
            catch (ServiceNowException)
            {
            }
        }

        foreach (var row in rows)
        {
            if (times.ContainsKey(row.SysId))
                continue;
            if (!notesByRecord.TryGetValue(row.SysId, out var notes) || notes.Count == 0)
                continue;

            var tokens = AssignmentNoteReader.TokensFor(row.Assignee);
            var when = AssignmentNoteReader.FindAssignedOn(notes, tokens);
            if (!string.IsNullOrWhiteSpace(when))
                times[row.SysId] = when;
        }
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

    private async Task ReadAssignmentJournalAsync(
        string query,
        Dictionary<string, List<JournalEntry>> notesByRecord,
        CancellationToken cancellationToken)
    {
        var result = await GetListAsync(
            "sys_journal_field",
            AssignmentJournalFields,
            query,
            AssignmentAuditLimit,
            0,
            cancellationToken).ConfigureAwait(false);
        using (result)
        {
            foreach (var row in RequireArray(result.Document).EnumerateArray())
            {
                var key = Text(SnowField.Read(row, "element_id"));
                if (key.Length == 0)
                    continue;
                var note = RecordMapper.Journal(row);
                if (string.IsNullOrWhiteSpace(note.Text))
                    continue;
                if (!notesByRecord.TryGetValue(key, out var list))
                {
                    list = [];
                    notesByRecord[key] = list;
                }

                list.Add(note);
            }
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
