using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

/// <summary>
/// Case-insensitive name search for cached reference rows.
/// A term with no wildcard is a contains match. <c>*</c> matches any run of characters and <c>?</c> matches one.
/// <c>term + term</c> requires every term to match, in either order.
/// </summary>
public static class ReferenceNameMatcher
{
    public const int PageSize = 50;

    public static bool Matches(string? name, string? expression)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        return Hits(name, Compile(expression));
    }

    public static ChoicePage TakePage(IReadOnlyList<Choice> source, string? expression, int pageSize, string? selectedId)
    {
        if (pageSize < 1)
            pageSize = 1;

        var terms = Compile(expression);
        var selectedKey = selectedId ?? "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = new List<Choice>();
        Choice? selected = null;

        foreach (var choice in source)
        {
            if (string.IsNullOrEmpty(choice.Value) || !seen.Add(choice.Value))
                continue;
            if (selectedKey.Length > 0 && choice.Value.Equals(selectedKey, StringComparison.OrdinalIgnoreCase))
                selected = choice;
            if (!Hits(choice.Label, terms))
                continue;
            matches.Add(choice);
        }

        var matchCount = matches.Count;
        matches.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Label, right.Label));
        if (matches.Count > pageSize)
            matches.RemoveRange(pageSize, matches.Count - pageSize);

        if (selected is not null && !ContainsValue(matches, selected.Value))
        {
            if (matches.Count >= pageSize)
                matches[^1] = selected;
            else
                matches.Add(selected);
        }

        return new ChoicePage(matches, matchCount);
    }

    private static bool Hits(string name, CompiledTerm[]? terms)
    {
        if (terms is null)
            return true;
        foreach (var term in terms)
        {
            if (term.Wildcard)
            {
                if (!Glob(name.AsSpan(), term.Pattern.AsSpan()))
                    return false;
            }
            else if (!name.Contains(term.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Null means the box is empty (or only separators), so every name matches.
    /// Wildcard terms are wrapped so <c>print*</c> matches a name that contains that span,
    /// including <c>HQ-PRINTER-01</c>.
    /// </summary>
    private static CompiledTerm[]? Compile(string? expression)
    {
        var query = (expression ?? "").Trim();
        if (query.Length == 0)
            return null;

        var parts = query.Split('+');
        var terms = new List<CompiledTerm>();
        foreach (var raw in parts)
        {
            var term = raw.Trim();
            if (term.Length == 0)
                continue;
            var wildcard = term.Contains('*') || term.Contains('?');
            terms.Add(new CompiledTerm(wildcard, wildcard ? "*" + term + "*" : term));
        }

        return terms.Count == 0 ? null : terms.ToArray();
    }

    private static bool Glob(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        var textIndex = 0;
        var patternIndex = 0;
        var starText = -1;
        var starPattern = -1;
        while (textIndex < text.Length)
        {
            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starText = textIndex;
                starPattern = patternIndex;
                patternIndex++;
            }
            else if (patternIndex < pattern.Length && (pattern[patternIndex] == '?' || Same(pattern[patternIndex], text[textIndex])))
            {
                textIndex++;
                patternIndex++;
            }
            else if (starPattern >= 0)
            {
                starText++;
                textIndex = starText;
                patternIndex = starPattern + 1;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            patternIndex++;
        return patternIndex == pattern.Length;
    }

    private static bool Same(char pattern, char text) =>
        char.ToUpperInvariant(pattern) == char.ToUpperInvariant(text);

    private static bool ContainsValue(List<Choice> rows, string value)
    {
        foreach (var row in rows)
        {
            if (row.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private readonly record struct CompiledTerm(bool Wildcard, string Pattern);
}

public readonly record struct ChoicePage(IReadOnlyList<Choice> Rows, int MatchCount);
