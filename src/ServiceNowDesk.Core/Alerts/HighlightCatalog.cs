using ServiceNowDesk.Models;

namespace ServiceNowDesk.Alerts;

/// <summary>A list row that can carry a highlight color.</summary>
public interface IHighlightRow
{
    string SysId { get; }

    bool Unassigned { get; }

    string HighlightHex { get; set; }
}

public sealed record HighlightEntry(
    string Key,
    string Title,
    string Explanation,
    string SwatchHex,
    string RowHex,
    bool EnabledByDefault,
    AlertKind? Kind);

/// <summary>
/// Row colors for incidents, request items, walk-ups, and search.
/// Earlier entries win when a ticket matches more than one highlight that is turned on.
/// </summary>
public static class HighlightCatalog
{
    public const string Unassigned = "unassigned";
    public const string AssignedToMe = "assigned-to-me";
    public const string WatchedGroup = "watched-group";
    public const string SlaBreaching = "sla-breaching";
    public const string OnHoldPastFollowUp = "on-hold-past-follow-up";
    public const string UpdatedByCaller = "updated-by-caller";
    public const string ReturnedWithNotes = "returned-with-notes";

    public const string UnassignedRowHex = "#FDECEC";
    public const string UnassignedSwatchHex = "#8E2F2F";

    static HighlightCatalog()
    {
        Entries =
        [
            Alert(
                SlaBreaching,
                AlertKind.SlaBreaching,
                "SLA breaching",
                "Light crimson is the same hue as the SLA breaching circle. The ticket is in that notification.",
                true),
            Alert(
                OnHoldPastFollowUp,
                AlertKind.OnHoldPastFollowUp,
                "On hold past follow-up",
                "Light blue is the same hue as the On hold past follow-up circle. The ticket is on hold and the follow-up time has passed.",
                true),
            Alert(
                UpdatedByCaller,
                AlertKind.UpdatedByCaller,
                "Updated by caller",
                "Light violet is the same hue as the Updated by caller circle. The latest update is from the caller, and the ticket is assigned to you, in the watched group, or unassigned in that group or with no group, at one of the notification offices.",
                true),
            Alert(
                ReturnedWithNotes,
                AlertKind.ReturnedWithNotes,
                "Returned with notes",
                "Light cyan is the same hue as the Returned with notes circle. The latest journal note is from someone other than the caller and the assignee.",
                true),
            new HighlightEntry(
                Unassigned,
                "Unassigned",
                "Light red means nobody is assigned. A group with no person is included. A person who is assigned, even with no group, stays on the normal background. This highlight does not use a notification circle.",
                UnassignedSwatchHex,
                UnassignedRowHex,
                true,
                null),
            Alert(
                AssignedToMe,
                AlertKind.AssignedToMe,
                "Assigned to me",
                "Light green is the same hue as the Assigned to me circle. The ticket is in that queue. It starts off so a list of your own tickets stays readable.",
                false),
            Alert(
                WatchedGroup,
                AlertKind.WatchedGroup,
                "Group queue",
                "Light amber is the same hue as the Group queue circle. The ticket is in the watched group queue for the office locations under Notifications. It starts off.",
                false)
        ];

        DefaultKeys = Entries.Where(entry => entry.EnabledByDefault).Select(entry => entry.Key).ToArray();
    }

    public static IReadOnlyList<HighlightEntry> Entries { get; }

    public static IReadOnlyList<string> DefaultKeys { get; }

    public static HighlightEntry? Find(string? key) =>
        key is null ? null : Entries.FirstOrDefault(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static string Lighten(string hex)
    {
        var text = hex.Trim();
        if (text.StartsWith('#'))
            text = text[1..];
        if (text.Length != 6)
            throw new ArgumentException("Highlight color must be a 6-digit hex value.", nameof(hex));

        var red = Convert.ToByte(text[..2], 16);
        var green = Convert.ToByte(text.Substring(2, 2), 16);
        var blue = Convert.ToByte(text.Substring(4, 2), 16);
        return $"#{Mix(red):X2}{Mix(green):X2}{Mix(blue):X2}";
    }

    private static HighlightEntry Alert(string key, AlertKind kind, string title, string explanation, bool enabled)
    {
        var hex = AlertCatalog.Swatch(kind).Hex;
        return new HighlightEntry(key, title, explanation, hex, Lighten(hex), enabled, kind);
    }

    private static byte Mix(byte channel) =>
        (byte)Math.Round(channel * 0.14 + 255 * 0.86, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Which legend entries paint list rows. A missing saved list means the defaults.
/// An empty saved list means every highlight is off.
/// </summary>
public sealed class HighlightPreferences
{
    private readonly HashSet<string> _enabled;

    private HighlightPreferences(IEnumerable<string> keys) =>
        _enabled = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);

    public static HighlightPreferences Default { get; } = FromKeys(null);

    public IReadOnlyList<string> EnabledKeys =>
        HighlightCatalog.Entries.Where(entry => _enabled.Contains(entry.Key)).Select(entry => entry.Key).ToArray();

    public static HighlightPreferences From(DeskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return FromKeys(settings.EnabledHighlights);
    }

    public static HighlightPreferences FromKeys(IReadOnlyList<string>? keys)
    {
        if (keys is null)
            return new HighlightPreferences(HighlightCatalog.DefaultKeys);

        var enabled = new List<string>();
        foreach (var key in keys)
        {
            if (key is not null && HighlightCatalog.Find(key) is not null)
                enabled.Add(key);
        }

        return new HighlightPreferences(enabled);
    }

    public bool IsEnabled(string key) => key is not null && _enabled.Contains(key);

    public string ChooseRowHex(bool unassigned, IEnumerable<AlertKind>? kinds)
    {
        HashSet<AlertKind>? matched = kinds switch
        {
            null => null,
            HashSet<AlertKind> ready => ready,
            _ => [.. kinds]
        };

        foreach (var entry in HighlightCatalog.Entries)
        {
            if (!_enabled.Contains(entry.Key))
                continue;
            if (entry.Kind is null)
            {
                if (unassigned)
                    return entry.RowHex;
                continue;
            }

            if (matched is not null && matched.Contains(entry.Kind.Value))
                return entry.RowHex;
        }

        return "";
    }

    public void ApplyTo(DeskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.EnabledHighlights = [.. EnabledKeys];
    }
}

/// <summary>Paints list rows from the legend and the latest notification snapshot.</summary>
public sealed class RowHighlighter
{
    private HighlightPreferences _preferences = HighlightPreferences.Default;
    private Dictionary<string, HashSet<AlertKind>> _kinds = new(StringComparer.OrdinalIgnoreCase);

    public void Use(HighlightPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _preferences = preferences;
    }

    public void Use(AlertSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var next = new Dictionary<string, HashSet<AlertKind>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in AlertCatalog.All)
        {
            foreach (var row in snapshot.Bucket(kind).Rows)
            {
                if (string.IsNullOrWhiteSpace(row.SysId))
                    continue;
                if (!next.TryGetValue(row.SysId, out var set))
                {
                    set = [];
                    next[row.SysId] = set;
                }

                set.Add(kind);
            }
        }

        _kinds = next;
    }

    public void Paint(IHighlightRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.HighlightHex = Choose(row.SysId, row.Unassigned);
    }

    public string Choose(string? sysId, bool unassigned)
    {
        HashSet<AlertKind>? kinds = null;
        if (!string.IsNullOrWhiteSpace(sysId))
            _kinds.TryGetValue(sysId, out kinds);
        return _preferences.ChooseRowHex(unassigned, kinds);
    }
}
