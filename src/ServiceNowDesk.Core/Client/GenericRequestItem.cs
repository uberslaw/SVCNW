using ServiceNowDesk.Models;

namespace ServiceNowDesk.Client;

/// <summary>
/// Arup's generic catalog item, opened from the service portal as Other Request.
/// The portal labels are Requested for, Request Title, and Request Description.
/// Live variable names come from the catalog item when the client can read them.
/// These constants are the names used when that list cannot be read. A wrong name is one of these constants.
/// </summary>
public static class GenericRequestItem
{
    public const string SysId = "06fbd61cdb810410c93568684b9619a9";
    public const string Name = "Other Request";
    public const string Summary = "Request any other type of hardware, help or support not available within the Service Catalog";
    public const string Warning = "Use this only when no specific catalog item fits. A generic request can be slower and may miss fields.";

    public const string RequestedForLabel = "Requested for";
    public const string TitleLabel = "Request Title";
    public const string DescriptionLabel = "Request Description";

    public const string RequestedForVariable = "requested_for";
    public const string TitleVariable = "request_title";
    public const string DescriptionVariable = "request_description";
}

/// <summary>
/// Variable names for one submit of <see cref="GenericRequestItem"/>.
/// Requested for is always sent as sysparm_requested_for. It is also a variable when the item defines one,
/// or when the item variables could not be read.
/// </summary>
public sealed class GenericRequestVariables
{
    private GenericRequestVariables(string requestedFor, string title, string description, bool includeRequestedForVariable)
    {
        RequestedFor = requestedFor;
        Title = title;
        Description = description;
        IncludeRequestedForVariable = includeRequestedForVariable;
    }

    public string RequestedFor { get; }
    public string Title { get; }
    public string Description { get; }
    public bool IncludeRequestedForVariable { get; }

    public static GenericRequestVariables Fallback { get; } = new(
        GenericRequestItem.RequestedForVariable,
        GenericRequestItem.TitleVariable,
        GenericRequestItem.DescriptionVariable,
        includeRequestedForVariable: true);

    public static GenericRequestVariables Resolve(IReadOnlyList<CatalogVariableDefinition>? definitions)
    {
        var list = definitions ?? [];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requestedFor = Take(
            list,
            used,
            GenericRequestItem.RequestedForLabel,
            GenericRequestItem.RequestedForVariable,
            static name => NameHasToken(name, "requested") && NameHasToken(name, "for"));
        var title = Take(
            list,
            used,
            GenericRequestItem.TitleLabel,
            GenericRequestItem.TitleVariable,
            static name => NameHasToken(name, "title"));
        var description = Take(
            list,
            used,
            GenericRequestItem.DescriptionLabel,
            GenericRequestItem.DescriptionVariable,
            static name => NameHasToken(name, "description"));

        return new GenericRequestVariables(
            requestedFor ?? GenericRequestItem.RequestedForVariable,
            title ?? GenericRequestItem.TitleVariable,
            description ?? GenericRequestItem.DescriptionVariable,
            includeRequestedForVariable: list.Count == 0 || requestedFor is not null);
    }

    public Dictionary<string, string> ToPayload(string requestedForSysId, string title, string description)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        values[Title] = title.Trim();
        if (!values.ContainsKey(Description))
            values[Description] = description.Trim();

        if (IncludeRequestedForVariable
            && !string.IsNullOrWhiteSpace(requestedForSysId)
            && !values.ContainsKey(RequestedFor))
        {
            values[RequestedFor] = requestedForSysId.Trim();
        }

        return values;
    }

    public static string Read(IReadOnlyDictionary<string, string> variables, string exactName, string token)
    {
        foreach (var pair in variables)
        {
            if (pair.Key.Equals(exactName, StringComparison.OrdinalIgnoreCase))
                return (pair.Value ?? "").Trim();
        }

        string? fallback = null;
        foreach (var pair in variables)
        {
            if (!NameHasToken(pair.Key, token))
                continue;
            var value = (pair.Value ?? "").Trim();
            if (pair.Key.Contains("request", StringComparison.OrdinalIgnoreCase))
                return value;
            fallback ??= value;
        }

        return fallback ?? "";
    }

    private static string? Take(
        IReadOnlyList<CatalogVariableDefinition> list,
        HashSet<string> used,
        string label,
        string exactName,
        Func<string, bool> token)
    {
        var match = list.FirstOrDefault(variable => Unused(used, variable) && LabelIs(variable, label))
            ?? list.FirstOrDefault(variable => Unused(used, variable) && NameIs(variable, exactName))
            ?? list.Where(variable => Unused(used, variable) && token(variable.Name))
                .OrderByDescending(variable => variable.Name.Contains("request", StringComparison.OrdinalIgnoreCase))
                .ThenBy(variable => variable.Name.Length)
                .FirstOrDefault();
        if (match is null)
            return null;

        used.Add(match.Name);
        return match.Name;
    }

    private static bool Unused(HashSet<string> used, CatalogVariableDefinition variable) =>
        !used.Contains(variable.Name);

    private static bool LabelIs(CatalogVariableDefinition variable, string label) =>
        string.Equals((variable.Label ?? "").Trim(), label, StringComparison.OrdinalIgnoreCase);

    private static bool NameIs(CatalogVariableDefinition variable, string name) =>
        string.Equals((variable.Name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase);

    private static bool NameHasToken(string name, string token)
    {
        var parts = (name ?? "").Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Any(part => part.Equals(token, StringComparison.OrdinalIgnoreCase));
    }
}
