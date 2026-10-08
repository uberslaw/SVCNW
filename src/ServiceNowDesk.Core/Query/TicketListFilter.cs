using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Query;

/// <summary>
/// Human-readable active filters and encoded-query previews for ticket lists.
/// Keeps the same field names and operators ServiceNow expects so a wrong value is obvious.
/// </summary>
public static class TicketListFilter
{
    public const string CurrentUserScript = "javascript:gs.getUserID()";

    /// <summary>
    /// Short chrome line: preset, assignee mode, offices when they apply.
    /// </summary>
    public static string DescribeActive(
        PresetOption? preset,
        IReadOnlyList<string>? officeCities,
        IReadOnlyList<string>? teamMemberIds = null)
    {
        var label = string.IsNullOrWhiteSpace(preset?.Label) ? "List" : preset!.Label.Trim();
        var parts = new List<string> { label };
        var assignment = preset?.Assignment ?? AssignmentScope.Any;
        if (!string.IsNullOrWhiteSpace(preset?.AssignmentClause))
        {
            parts.Add(preset!.AssignmentClause!.Trim());
        }
        else
        {
            switch (assignment)
            {
                case AssignmentScope.Mine:
                    parts.Add("assigned_to=signed-in user");
                    break;
                case AssignmentScope.MyGroups when teamMemberIds is not null:
                    parts.Add(teamMemberIds.Count == 0
                        ? "assigned_to=NO_TEAM"
                        : "assigned_toIN " + teamMemberIds.Count + " teammate(s)");
                    break;
                case AssignmentScope.MyGroups:
                    parts.Add("assignment_groupIN my groups");
                    break;
                case AssignmentScope.Unassigned:
                    parts.Add("assigned_toISEMPTY^assignment_groupIN my groups");
                    break;
            }
        }

        if (assignment is AssignmentScope.MyGroups or AssignmentScope.Unassigned
            && teamMemberIds is null
            && officeCities is not null)
        {
            if (officeCities.Count == 0)
                parts.Add("location.name=NO_OFFICE");
            else
                parts.Add("location.name NQ " + string.Join(", ", ExpandOffices(officeCities)));
        }
        else if (assignment == AssignmentScope.Mine)
        {
            parts.Add("no office filter");
        }

        var activity = preset?.Activity ?? ActivityFilter.Open;
        parts.Add(activity switch
        {
            ActivityFilter.Open => "open (StillWorking)",
            ActivityFilter.Closed => "closed",
            _ => "any activity"
        });

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Replace Table API <c>javascript:gs.getUserID()</c> with the signed-in sys_id when known.
    /// Daily Work and alerts already use the sys_id; list Mine must match or the queue stays empty.
    /// </summary>
    public static string BindCurrentUser(string? clause, string? userSysId)
    {
        var text = clause ?? "";
        if (text.Length == 0 || string.IsNullOrWhiteSpace(userSysId))
            return text;

        string id;
        try
        {
            id = EncodedQuery.SafeToken(userSysId, "user id");
        }
        catch (InvalidOperationException)
        {
            return text;
        }

        return text.Replace(CurrentUserScript, id, StringComparison.Ordinal);
    }

    public static string AssignedToMeClause(string? userSysId) =>
        string.IsNullOrWhiteSpace(userSysId)
            ? "assigned_to=" + CurrentUserScript
            : "assigned_to=" + EncodedQuery.SafeToken(userSysId, "user id");

    private static IEnumerable<string> ExpandOffices(IReadOnlyList<string> cities)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var city in cities)
        {
            foreach (var variant in OfficeQueue.Variants(city))
            {
                if (seen.Add(variant))
                    yield return "\"" + variant + "\"";
            }
        }
    }
}
