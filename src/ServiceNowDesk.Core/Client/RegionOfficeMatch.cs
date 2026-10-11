using System.Text;

namespace ServiceNowDesk.Client;

/// <summary>
/// Whether a hardware location belongs to a saved region office.
/// The office name, or an alias split on "/" or parentheses, must occur as whole tokens.
/// A shorter name does not match when it only sits inside a longer catalog city
/// (York does not match New York). A trailing "Office" does not block a match.
/// </summary>
public static class RegionOfficeMatch
{
    private static readonly string[][] CatalogPhrases = BuildCatalogPhrases();

    public static bool LocationMatchesOffice(string? location, string? officeName)
    {
        var locationTokens = Tokenize(location);
        if (locationTokens.Count == 0)
            return false;

        foreach (var phrase in Phrases(officeName))
        {
            var phraseTokens = Tokenize(phrase);
            if (phraseTokens.Count == 0)
                continue;
            if (HasStandaloneOccurrence(locationTokens, phraseTokens))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Hardware locations that belong to <paramref name="offices"/>.
    /// No saved offices yields an empty list, not every location.
    /// </summary>
    public static IReadOnlyList<string> LocationsInRegion(IEnumerable<string?>? locations, IEnumerable<string>? offices)
    {
        var saved = NormalizeOffices(offices);
        if (saved.Count == 0 || locations is null)
            return [];

        var matches = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in locations)
        {
            var location = (raw ?? "").Trim();
            if (location.Length == 0 || !seen.Add(location))
                continue;
            if (saved.Any(office => LocationMatchesOffice(location, office)))
                matches.Add(location);
        }

        return matches;
    }

    public static IReadOnlyList<string> NormalizeOffices(IEnumerable<string>? offices)
    {
        if (offices is null)
            return [];

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var office in offices)
        {
            var name = (office ?? "").Trim();
            if (name.Length == 0 || !seen.Add(name))
                continue;
            names.Add(name);
        }

        return names;
    }

    public static IEnumerable<string> Phrases(string? officeName)
    {
        var text = (officeName ?? "").Trim();
        if (text.Length == 0)
            yield break;

        foreach (var segment in text.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var phrase in PhrasesInSegment(segment))
                yield return phrase;
        }
    }

    private static IEnumerable<string> PhrasesInSegment(string segment)
    {
        var main = new StringBuilder();
        var index = 0;
        while (index < segment.Length)
        {
            if (segment[index] != '(')
            {
                main.Append(segment[index]);
                index++;
                continue;
            }

            var end = segment.IndexOf(')', index + 1);
            if (end < 0)
            {
                main.Append(segment.AsSpan(index));
                break;
            }

            var inner = segment[(index + 1)..end].Trim();
            if (inner.Length > 0)
                yield return inner;
            index = end + 1;
        }

        var phrase = main.ToString().Trim();
        if (phrase.Length > 0)
            yield return phrase;
    }

    internal static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text))
            return tokens;

        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && (char.IsLetter(text[i]) || char.IsDigit(text[i]));
            if (inWord)
            {
                if (start < 0)
                    start = i;
                continue;
            }

            if (start < 0)
                continue;
            tokens.Add(text[start..i]);
            start = -1;
        }

        return tokens;
    }

    private static bool HasStandaloneOccurrence(IReadOnlyList<string> locationTokens, IReadOnlyList<string> phraseTokens)
    {
        var last = locationTokens.Count - phraseTokens.Count;
        for (var index = 0; index <= last; index++)
        {
            if (!SliceEquals(locationTokens, index, phraseTokens))
                continue;
            if (!InsideLongerCatalogPhrase(locationTokens, index, phraseTokens.Count))
                return true;
        }

        return false;
    }

    private static bool InsideLongerCatalogPhrase(IReadOnlyList<string> locationTokens, int index, int length)
    {
        foreach (var phrase in CatalogPhrases)
        {
            if (phrase.Length <= length || phrase.Length > locationTokens.Count)
                continue;

            var last = locationTokens.Count - phrase.Length;
            for (var start = 0; start <= last; start++)
            {
                if (!SliceEquals(locationTokens, start, phrase))
                    continue;

                var end = start + phrase.Length;
                var matchEnd = index + length;
                if (index >= start && matchEnd <= end && (index > start || matchEnd < end))
                    return true;
            }
        }

        return false;
    }

    private static bool SliceEquals(IReadOnlyList<string> location, int start, IReadOnlyList<string> phrase)
    {
        for (var i = 0; i < phrase.Count; i++)
        {
            if (!location[start + i].Equals(phrase[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string[][] BuildCatalogPhrases()
    {
        var phrases = new List<string[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var office in RegionOfficeCatalog.OfficeNames)
        {
            foreach (var phrase in Phrases(office))
            {
                var tokens = Tokenize(phrase);
                if (tokens.Count == 0)
                    continue;
                var key = string.Join('\u001f', tokens.Select(token => token.ToUpperInvariant()));
                if (!seen.Add(key))
                    continue;
                phrases.Add(tokens.ToArray());
            }
        }

        return phrases.ToArray();
    }
}
