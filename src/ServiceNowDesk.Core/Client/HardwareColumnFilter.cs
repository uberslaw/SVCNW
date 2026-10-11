namespace ServiceNowDesk.Client;

/// <summary>
/// Choice lists for the hardware State, Substate, Model, and Location filters.
/// An empty selection matches every row. One or more selections match when the
/// cell equals any selected value (OR). Callers AND this with the other columns.
/// </summary>
public static class HardwareColumnFilter
{
    /// <summary>Install-status labels in the order the hardware form already uses.</summary>
    public static IReadOnlyList<string> StateOrder { get; } =
        HardwareCatalog.InstallStatuses.Select(choice => choice.Label).ToArray();

    public static IReadOnlyList<string> Distinct(IEnumerable<string?>? values, IReadOnlyList<string>? preferred = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<string>();
        if (values is not null)
        {
            foreach (var raw in values)
            {
                var text = (raw ?? "").Trim();
                if (text.Length == 0 || !seen.Add(text))
                    continue;
                items.Add(text);
            }
        }

        if (preferred is null || preferred.Count == 0)
        {
            items.Sort(StringComparer.OrdinalIgnoreCase);
            return items;
        }

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < preferred.Count; i++)
        {
            var label = (preferred[i] ?? "").Trim();
            if (label.Length > 0)
                rank.TryAdd(label, i);
        }

        items.Sort((left, right) =>
        {
            var leftRank = rank.TryGetValue(left, out var leftOrder) ? leftOrder : int.MaxValue;
            var rightRank = rank.TryGetValue(right, out var rightOrder) ? rightOrder : int.MaxValue;
            var compared = leftRank.CompareTo(rightRank);
            return compared != 0
                ? compared
                : StringComparer.OrdinalIgnoreCase.Compare(left, right);
        });
        return items;
    }

    /// <summary>Empty or null <paramref name="selected"/> does not filter.</summary>
    public static bool Matches(string? value, IEnumerable<string>? selected)
    {
        if (selected is null)
            return true;

        var any = false;
        var text = (value ?? "").Trim();
        foreach (var choice in selected)
        {
            var label = (choice ?? "").Trim();
            if (label.Length == 0)
                continue;
            any = true;
            if (string.Equals(text, label, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return !any;
    }
}
