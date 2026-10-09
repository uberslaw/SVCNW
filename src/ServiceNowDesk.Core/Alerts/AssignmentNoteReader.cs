using System.Text.RegularExpressions;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Alerts;

/// <summary>
/// Finds when a ticket was assigned to the current assignee by reading journal / work notes.
/// Rule: use the most recent note that indicates assignment to that person (or "me");
/// whole local calendar days from that stamp is Days assigned (today = 0). Prefer
/// <c>sys_audit</c> assigned_to→me when the instance returns it; notes fill the gap when
/// audit is blocked. Blank only when neither source yields a time.
/// </summary>
public static class AssignmentNoteReader
{
    private static readonly Regex ChangedToPattern = new(
        @"assigned\s+to\s+changed\s+from\s+.+?\s+to\s+(?<who>.+?)(?:\.|$|\r|\n)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AssignedToPattern = new(
        @"assigned\s+to\s*:?\s*(?<who>.+?)(?:\.|$|\r|\n)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Display name, user name, and "me" tokens used to match assignment notes for a person.
    /// </summary>
    public static IReadOnlyList<string> TokensFor(string? displayName, string? userName = null)
    {
        var tokens = new List<string>();
        AddToken(tokens, displayName);
        AddToken(tokens, userName);
        if (tokens.Count > 0 && !tokens.Contains("me", StringComparer.OrdinalIgnoreCase))
            tokens.Add("me");
        return tokens;
    }

    public static IReadOnlyList<string> TokensFor(CurrentUser? user) =>
        user is null ? [] : TokensFor(user.Name, user.UserName);

    /// <summary>
    /// Instant of the newest journal line that assigns the ticket to one of <paramref name="assigneeTokens"/>.
    /// </summary>
    public static string? FindAssignedOn(
        IEnumerable<JournalEntry>? notes,
        IEnumerable<string>? assigneeTokens)
    {
        if (notes is null || assigneeTokens is null)
            return null;

        var tokens = assigneeTokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Select(token => token.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tokens.Length == 0)
            return null;

        string? best = null;
        DateTime bestWhen = DateTime.MinValue;
        foreach (var note in notes)
        {
            if (note is null || string.IsNullOrWhiteSpace(note.Text))
                continue;
            if (!IndicatesAssignmentTo(note.Text, tokens))
                continue;
            if (!AlertClassifier.TryParseInstant(note.CreatedDisplay, out var when))
                continue;
            if (best is not null && when <= bestWhen)
                continue;
            best = note.CreatedDisplay.Trim();
            bestWhen = when;
        }

        return best;
    }

    /// <summary>
    /// True when the note text says the ticket was assigned to one of the given people (or "me").
    /// </summary>
    public static bool IndicatesAssignmentTo(string? text, IEnumerable<string>? assigneeTokens)
    {
        if (string.IsNullOrWhiteSpace(text) || assigneeTokens is null)
            return false;

        var tokens = assigneeTokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Select(token => token.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tokens.Length == 0)
            return false;

        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            var who = MatchWho(ChangedToPattern, trimmed) ?? MatchWho(AssignedToPattern, trimmed);
            if (who is null)
                continue;
            if (MatchesToken(who, tokens))
                return true;
        }

        return false;
    }

    private static string? MatchWho(Regex pattern, string line)
    {
        var match = pattern.Match(line);
        if (!match.Success)
            return null;
        var who = match.Groups["who"].Value.Trim().TrimEnd('.');
        return who.Length == 0 ? null : who;
    }

    private static bool MatchesToken(string who, IReadOnlyList<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (who.Equals(token, StringComparison.OrdinalIgnoreCase))
                return true;
            // Short tokens like "me" must be exact; longer names may appear as a substring.
            if (token.Length < 3 || who.Length < 3)
                continue;
            if (who.Contains(token, StringComparison.OrdinalIgnoreCase)
                || token.Contains(who, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void AddToken(List<string> tokens, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        var trimmed = value.Trim();
        if (!tokens.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            tokens.Add(trimmed);
    }
}
