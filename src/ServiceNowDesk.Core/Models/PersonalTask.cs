namespace ServiceNowDesk.Models;

public enum TrafficLight
{
    Red,
    Amber,
    Green
}

/// <summary>
/// A text note that lives only on this PC. It is not a ServiceNow task.
/// </summary>
public sealed record PersonalTask(Guid Id, string Text, TrafficLight Light, DateTime CreatedUtc)
{
    public const int MaxTextLength = 500;

    public static PersonalTask Create(string text, TrafficLight light, DateTime? createdUtc = null, Guid? id = null)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Enter a note.", nameof(text));
        if (trimmed.Length > MaxTextLength)
            trimmed = trimmed[..MaxTextLength];

        var created = createdUtc ?? DateTime.UtcNow;
        if (created.Kind != DateTimeKind.Utc)
            created = created.ToUniversalTime();

        return new PersonalTask(id ?? Guid.NewGuid(), trimmed, Normalize(light), created);
    }

    public static TrafficLight Normalize(TrafficLight light) =>
        Enum.IsDefined(light) ? light : TrafficLight.Amber;

    public static string Hex(TrafficLight light) => Normalize(light) switch
    {
        TrafficLight.Red => "#C0392B",
        TrafficLight.Green => "#2E9B4F",
        _ => "#E6A317"
    };
}

public static class PersonalTaskConversion
{
    public const string NotConnectedMessage =
        "Connect to ServiceNow before creating a record from a note. The note stays in the list.";

    public static IncidentChanges ToIncident(string text, string? callerSysId) => new()
    {
        ShortDescription = RequiredText(text),
        CallerId = KnownUser(callerSysId)
    };

    public static RequestedItemChanges ToRequestedItem(string text, string? requestedForSysId) => new()
    {
        ShortDescription = RequiredText(text),
        RequestedForId = KnownUser(requestedForSysId)
    };

    private static string RequiredText(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Enter a note.", nameof(text));
        if (trimmed.Length > PersonalTask.MaxTextLength)
            trimmed = trimmed[..PersonalTask.MaxTextLength];
        return trimmed;
    }

    private static string? KnownUser(string? sysId)
    {
        var id = sysId?.Trim() ?? "";
        return id.Length == 0 ? null : id;
    }
}
