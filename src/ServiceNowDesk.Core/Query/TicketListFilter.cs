using ServiceNowDesk.Client;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Query;

/// <summary>
/// Human-readable active filters for ticket lists and Settings Cache.
/// Encoded ServiceNow queries stay on LastEncodedQuery for logging only.
/// </summary>
public static class TicketListFilter
{
    public const string CurrentUserScript = "javascript:gs.getUserID()";

    /// <summary>
    /// Short chrome line: preset name and plain-language scope. Never includes encoded operators.
    /// </summary>
    public static string DescribeActive(
        PresetOption? preset,
        IReadOnlyList<string>? officeCities,
        IReadOnlyList<string>? teamMemberIds = null)
    {
        var label = string.IsNullOrWhiteSpace(preset?.Label) ? "List" : preset!.Label.Trim();
        var parts = new List<string> { label };
        var assignment = preset?.Assignment ?? AssignmentScope.Any;
        if (string.IsNullOrWhiteSpace(preset?.AssignmentClause))
        {
            switch (assignment)
            {
                case AssignmentScope.Mine:
                    parts.Add("assigned to you");
                    break;
                case AssignmentScope.MyGroups when teamMemberIds is not null:
                    parts.Add(teamMemberIds.Count == 0
                        ? "no teammates selected"
                        : teamMemberIds.Count + " teammates");
                    break;
                case AssignmentScope.MyGroups:
                    parts.Add("your groups");
                    break;
                case AssignmentScope.Unassigned:
                    parts.Add("unassigned in your groups");
                    break;
            }
        }

        if (assignment is AssignmentScope.MyGroups or AssignmentScope.Unassigned
            && teamMemberIds is null
            && officeCities is not null)
        {
            if (officeCities.Count == 0)
                parts.Add("no office selected");
            else
                parts.Add("offices: " + string.Join(", ", officeCities));
        }

        var activity = preset?.Activity ?? ActivityFilter.Open;
        parts.Add(activity switch
        {
            ActivityFilter.Open => "open",
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
}
