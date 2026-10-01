using System.Text;
using System.Text.RegularExpressions;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Query;

public static partial class EncodedQuery
{
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(character is '^' or '\r' or '\n' or '\t' ? ' ' : character);
        }

        return CollapseSpaces(builder.ToString());
    }

    public static bool IsNumberQuery(string? text)
    {
        var term = Sanitize(text);
        return term.Length > 0
            && (FullNumberPattern().IsMatch(term) || PartialNumberPattern().IsMatch(term) || DigitsPattern().IsMatch(term));
    }

    public static string TextSearch(string? text)
    {
        var term = Sanitize(text);
        if (term.Length == 0)
            return "";

        if (FullNumberPattern().IsMatch(term))
            return "number=" + term.ToUpperInvariant();

        if (PartialNumberPattern().IsMatch(term))
            return "numberSTARTSWITH" + term.ToUpperInvariant();

        if (DigitsPattern().IsMatch(term))
            return "numberLIKE" + term;

        return "123TEXTQUERY321=" + term;
    }

    public static DeskSection? SectionForNumber(string? text)
    {
        var term = Sanitize(text).ToUpperInvariant();
        if (term.StartsWith("RITM", StringComparison.Ordinal) || term.StartsWith("SCTASK", StringComparison.Ordinal))
            return DeskSection.RequestedItems;
        if (term.StartsWith("INC", StringComparison.Ordinal))
            return DeskSection.Incidents;
        if (term.StartsWith("REQ", StringComparison.Ordinal))
            return DeskSection.Requests;
        return null;
    }

    public static string ActivityClause(ActivityFilter activity) => activity switch
    {
        ActivityFilter.Open => "active=true",
        ActivityFilter.Closed => "active=false",
        _ => ""
    };

    public static string Build(string? textClause, string? assignmentClause, string? activityClause, string? extraClause)
    {
        var parts = new[] { textClause, assignmentClause, activityClause, extraClause }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .ToArray();

        var encoded = string.Join("^", parts);
        const string order = "ORDERBYDESCsys_updated_on";
        return encoded.Length == 0 ? order : encoded + "^" + order;
    }

    public static string SafeToken(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !TokenPattern().IsMatch(value))
            throw new InvalidOperationException($"ServiceNow returned a {label} this app will not place in a query.");

        return value;
    }

    private static string CollapseSpaces(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousSpace = false;
        foreach (var character in value)
        {
            var isSpace = character == ' ';
            if (isSpace && previousSpace)
                continue;
            builder.Append(character);
            previousSpace = isSpace;
        }

        return builder.ToString().Trim();
    }

    [GeneratedRegex(@"^(INC|RITM|SCTASK|TASK|REQ|CHG|PRB)\d{4,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FullNumberPattern();

    [GeneratedRegex(@"^(INC|RITM|SCTASK|TASK|REQ|CHG|PRB)\d*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartialNumberPattern();

    [GeneratedRegex(@"^\d{4,}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
