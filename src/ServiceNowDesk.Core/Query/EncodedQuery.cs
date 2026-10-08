using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Query;

/// <summary>
/// Task tables search short description, description, and journal fields.
/// Knowledge articles store the body in <c>text</c> and have no work notes or comments.
/// </summary>
public enum SearchFieldSet
{
    Task,
    Knowledge
}

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

    /// <summary>
    /// A plain phrase with no <c>+</c> and no <c>*</c> stays on <c>123TEXTQUERY321</c>
    /// so ticket-number lookup is unchanged. Quotes are optional: a quoted phrase is one
    /// term, and a single <c>+</c>-separated token is one term without quotes.
    /// <c>+</c> surrounded by spaces, or a leading <c>+</c> on a token, means AND.
    /// A phone number (<c>+1 555-0100</c>) and a hyphenated word are not operators.
    /// A <c>*</c> is a wildcard LIKE. Required terms without a star use the text index,
    /// which covers short description, description, and journal text on task.
    /// </summary>
    public static string TextSearch(string? text) => TextSearch(text, SearchFieldSet.Task);

    public static string TextSearch(string? text, SearchFieldSet fields)
    {
        var terms = ParseTerms(text);
        if (terms.Count == 0)
            return "";

        if (terms.Count == 1 && !terms[0].Wildcard)
            return PlainClause(terms[0].Text);

        return string.Join("^", terms.Select(term => TermClause(term, fields)));
    }

    /// <summary>
    /// In-memory stand-in for <see cref="TextSearch"/>: every term must match (AND),
    /// and a <c>*</c> is a wildcard against the supplied field values.
    /// </summary>
    public static bool Matches(string? text, string? number, IEnumerable<string>? fields)
    {
        var terms = ParseTerms(text);
        if (terms.Count == 0)
            return true;

        var ticket = number ?? "";
        var haystack = fields ?? [];
        foreach (var term in terms)
        {
            if (!TermMatches(term, ticket, haystack))
                return false;
        }

        return true;
    }

    public static string RecordFilters(string? groupId, string? assigneeId, string? openedFrom, string? openedTo)
    {
        var parts = new List<string>();
        if (TrySysId(groupId, out var group))
            parts.Add("assignment_group=" + group);
        if (TrySysId(assigneeId, out var assignee))
            parts.Add("assigned_to=" + assignee);
        if (TryOpenedDay(openedFrom, out var from))
            parts.Add("opened_at>=" + from + "@00:00:00");
        if (TryOpenedDay(openedTo, out var to))
            parts.Add("opened_at<=" + to + "@23:59:59");
        return string.Join("^", parts);
    }

    public static bool HasRecordFilter(string? groupId, string? assigneeId, string? openedFrom, string? openedTo) =>
        RecordFilters(groupId, assigneeId, openedFrom, openedTo).Length > 0;

    public static bool SameReference(string? wanted, string? actual)
    {
        if (!TrySysId(wanted, out var id))
            return true;
        return id.Equals((actual ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool OpenedInRange(string? openedFrom, string? openedTo, string? openedDisplay)
    {
        var hasFrom = TryOpenedDay(openedFrom, out var from);
        var hasTo = TryOpenedDay(openedTo, out var to);
        if (!hasFrom && !hasTo)
            return true;
        if (!TryParseOpened(openedDisplay, out var opened))
            return false;
        if (hasFrom && opened.Date < DateTime.ParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture).Date)
            return false;
        if (hasTo && opened.Date > DateTime.ParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture).Date)
            return false;
        return true;
    }

    public static DeskSection? SectionForNumber(string? text)
    {
        var term = Sanitize(text).ToUpperInvariant();
        if (term.StartsWith("RITM", StringComparison.Ordinal) || term.StartsWith("SCTASK", StringComparison.Ordinal))
            return DeskSection.RequestedItems;
        if (term.StartsWith("IMS", StringComparison.Ordinal) && (term.Length == 3 || char.IsDigit(term[3])))
            return DeskSection.WalkUps;
        if (term.StartsWith("INC", StringComparison.Ordinal))
            return DeskSection.Incidents;
        if (term.StartsWith("REQ", StringComparison.Ordinal))
            return DeskSection.Requests;
        if (term.StartsWith("KB", StringComparison.Ordinal))
            return DeskSection.Knowledge;
        return null;
    }

    public static string ActiveUserSearch(string term) =>
        "nameLIKE" + term
        + "^ORfirst_nameLIKE" + term
        + "^ORlast_nameLIKE" + term
        + "^ORemailLIKE" + term
        + "^ORuser_nameLIKE" + term
        + "^active=true";

    public static string ActiveUserExact(string term)
    {
        static string Group(string clause) => clause + "^active=true";
        return string.Join("^NQ",
        [
            Group("name=" + term),
            Group("email=" + term),
            Group("user_name=" + term),
            Group("first_name=" + term),
            Group("last_name=" + term),
            Group("nameSTARTSWITH" + term + "^nameENDSWITH" + term),
            Group("emailSTARTSWITH" + term + "^emailENDSWITH" + term),
            Group("user_nameSTARTSWITH" + term + "^user_nameENDSWITH" + term)
        ]);
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

    private readonly record struct SearchTerm(string Text, bool Wildcard);

    /// <summary>
    /// Quotes keep a phrase as one term. A <c>+</c> with spaces around it, or a leading
    /// <c>+</c> on a non-phone token, starts another required term.
    /// </summary>
    private static List<SearchTerm> ParseTerms(string? text)
    {
        var tokens = Tokenize(text ?? "");
        var rawTerms = new List<string>();
        var current = new List<string>();
        foreach (var token in tokens)
        {
            if (token == "+")
            {
                FlushTerm(rawTerms, current);
                continue;
            }

            if (token.StartsWith('+') && !IsPhoneToken(token))
            {
                FlushTerm(rawTerms, current);
                var rest = token[1..];
                if (rest.Length > 0)
                    current.Add(rest);
                continue;
            }

            current.Add(token);
        }

        FlushTerm(rawTerms, current);
        return rawTerms
            .Select(CleanTerm)
            .Where(term => term.Length > 0)
            .Select(term => new SearchTerm(term, term.Contains('*')))
            .ToList();
    }

    private static void FlushTerm(List<string> terms, List<string> current)
    {
        if (current.Count == 0)
            return;
        terms.Add(string.Join(' ', current));
        current.Clear();
    }

    private static List<string> Tokenize(string source)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var character in source)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    private static bool IsPhoneToken(string token) =>
        token.Length > 1 && token[0] == '+' && char.IsDigit(token[1]);

    private static string CleanTerm(string? value)
    {
        var cleaned = Sanitize(value);
        if (cleaned.IndexOfAny(['(', ')']) < 0)
            return cleaned;

        var builder = new StringBuilder(cleaned.Length);
        foreach (var character in cleaned)
            builder.Append(character is '(' or ')' ? ' ' : character);
        return CollapseSpaces(builder.ToString());
    }

    private static string PlainClause(string term)
    {
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

    private static string TermClause(SearchTerm term, SearchFieldSet fields) =>
        term.Wildcard ? WildcardClause(term.Text, fields) : PlainClause(term.Text);

    /// <summary>
    /// <c>123TEXTQUERY321</c> cannot express a star. ServiceNow LIKE treats <c>*</c> as a wildcard,
    /// so the star stays in the value. Task journal fields are included here because this builder
    /// only concatenates the encoded string and does not reject <c>work_notesLIKE</c> or
    /// <c>commentsLIKE</c>. Those are the same fields <c>AddJournalAsync</c> writes. A separate
    /// <c>sys_journal_field</c> query per row is not used. Knowledge has no journal fields; its
    /// body is <c>text</c>.
    /// </summary>
    private static string WildcardClause(string term, SearchFieldSet fields)
    {
        var columns = fields == SearchFieldSet.Knowledge
            ? new[] { "short_description", "text" }
            : new[] { "short_description", "description", "work_notes", "comments" };
        var parts = columns.Select(column => column + "LIKE" + term).ToList();
        if (IsTicketPrefix(term))
            parts.Add("numberLIKE" + NumberWildcard(term));
        return "(" + string.Join("^OR", parts) + ")";
    }

    private static bool IsTicketPrefix(string term)
    {
        var stem = term.Replace("*", "");
        return stem.Length > 0 && PartialNumberPattern().IsMatch(stem);
    }

    private static string NumberWildcard(string term)
    {
        var builder = new StringBuilder(term.Length);
        foreach (var character in term)
            builder.Append(character == '*' ? '*' : char.ToUpperInvariant(character));
        return builder.ToString();
    }

    private static bool TermMatches(SearchTerm term, string number, IEnumerable<string> fields)
    {
        if (term.Wildcard)
            return WildcardMatches(term.Text, number, fields);

        var clause = PlainClause(term.Text);
        if (clause.Length == 0)
            return true;
        if (clause.StartsWith("number=", StringComparison.Ordinal))
            return number.Equals(clause["number=".Length..], StringComparison.OrdinalIgnoreCase);
        if (clause.StartsWith("numberSTARTSWITH", StringComparison.Ordinal))
            return number.StartsWith(clause["numberSTARTSWITH".Length..], StringComparison.OrdinalIgnoreCase);
        if (clause.StartsWith("numberLIKE", StringComparison.Ordinal))
            return number.Contains(clause["numberLIKE".Length..], StringComparison.OrdinalIgnoreCase);

        const string marker = "123TEXTQUERY321=";
        var needle = clause.StartsWith(marker, StringComparison.Ordinal) ? clause[marker.Length..] : clause;
        return string.Join('\n', fields).Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static bool WildcardMatches(string term, string number, IEnumerable<string> fields)
    {
        if (fields.Any(field => FieldLike(field, term)))
            return true;
        return IsTicketPrefix(term) && FieldLike(number, term);
    }

    private static bool FieldLike(string? value, string pattern)
    {
        if (string.IsNullOrEmpty(value) || pattern.Length == 0)
            return false;
        var regex = Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal);
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool TrySysId(string? value, out string sysId)
    {
        sysId = Sanitize(value ?? "");
        if (sysId.Length == 0 || !TokenPattern().IsMatch(sysId))
        {
            sysId = "";
            return false;
        }

        return true;
    }

    private static bool TryOpenedDay(string? value, out string day)
    {
        day = "";
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Any(character => character is '^' or '\r' or '\n' or '\t' or '(' or ')'))
            return false;
        if (!DateTime.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        day = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryParseOpened(string? display, out DateTime opened)
    {
        opened = default;
        var trimmed = (display ?? "").Trim();
        if (trimmed.Length == 0)
            return false;
        string[] formats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"];
        if (DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out opened))
            return true;
        return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out opened);
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

    [GeneratedRegex(@"^(INC|RITM|SCTASK|TASK|REQ|CHG|PRB|KB|IMS)\d{4,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FullNumberPattern();

    [GeneratedRegex(@"^(INC|RITM|SCTASK|TASK|REQ|CHG|PRB|KB|IMS)\d*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartialNumberPattern();

    [GeneratedRegex(@"^\d{4,}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
