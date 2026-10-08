namespace ServiceNowDesk.Client;

/// <summary>
/// Local hardware column and search matching. Empty text matches everything.
/// Plain text (no <c>*</c> or <c>?</c>) is a case-insensitive contains match.
/// With wildcards, the pattern is a case-insensitive glob against the whole value:
/// <c>*</c> matches any run of characters, <c>?</c> matches one character.
/// So <c>Elite*</c> is a start match, <c>*Book</c> is an end match, and
/// <c>*Z6*G5*</c> requires <c>Z6</c> then later <c>G5</c>.
/// Terms separated by <c>+</c> are AND (any order).
/// </summary>
public static class HardwareTextFilter
{
    public const string SearchTipsTitle = "Hardware search tips";

    public const string SearchTipsBody =
        "Column filters run on this PC against the computers already downloaded.\n\n"
        + "• Plain text (no * or ?) matches anywhere in the cell (contains).\n"
        + "  Example: Z6 matches EliteBook Z6 G5.\n\n"
        + "• * matches any number of characters (including none).\n"
        + "  Example: *Z6*G5* matches models with Z6 then later G5.\n\n"
        + "• ? matches exactly one character.\n"
        + "  Example: EliteBook Z? G5 matches EliteBook Z6 G5.\n\n"
        + "• Start / end match: omit * on that side.\n"
        + "  Elite* starts with Elite. *G5 ends with G5.\n\n"
        + "• Multiple terms: use + between them (AND). Order does not matter.\n"
        + "  Example: Z6 + G5 requires both spans.\n\n"
        + "• Filters in different columns combine (all must match).\n"
        + "• Matching is case-insensitive.";

    public static bool Matches(string? value, string? expression)
    {
        var terms = Compile(expression);
        if (terms is null)
            return true;

        var text = value ?? "";
        foreach (var term in terms)
        {
            if (term.Wildcard)
            {
                if (!Glob(text.AsSpan(), term.Pattern.AsSpan()))
                    return false;
            }
            else if (!text.Contains(term.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

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
            terms.Add(new CompiledTerm(wildcard, term));
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
            else if (patternIndex < pattern.Length
                     && (pattern[patternIndex] == '?' || Same(pattern[patternIndex], text[textIndex])))
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

    private readonly record struct CompiledTerm(bool Wildcard, string Pattern);
}
