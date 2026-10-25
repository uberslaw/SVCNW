using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServiceNowDesk.QueueDay;

/// <summary>
/// JSON for the queue-day baseline. Ticket fields only. No passwords, cookies, or tokens.
/// </summary>
public static class QueueDayFileCodec
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(IEnumerable<QueueDayBaseline> days)
    {
        var file = new StoredFile
        {
            Days = (days ?? []).Select(ToStored).ToList()
        };
        return JsonSerializer.Serialize(file, Json);
    }

    public static IReadOnlyList<QueueDayBaseline> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            var file = JsonSerializer.Deserialize<StoredFile>(json, Json);
            return (file?.Days ?? [])
                .Select(FromStored)
                .Where(day => day is not null)
                .Cast<QueueDayBaseline>()
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Writes the file, reads it back, and fails if today's baseline is not the payload on disk.
    /// </summary>
    public static void WriteVerified(string path, IReadOnlyList<QueueDayBaseline> days, QueueDayBaseline expect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(expect);
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        var json = Serialize(days);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        Confirm(File.ReadAllText(temp), expect, temp);
        File.Move(temp, path, overwrite: true);
        Confirm(File.ReadAllText(path), expect, path);
    }

    private static void Confirm(string json, QueueDayBaseline expect, string path)
    {
        var found = Deserialize(json).FirstOrDefault(day => day.LocalDate == expect.LocalDate);
        var expectTickets = expect.People.Sum(person => person.Tickets.Count);
        var foundTickets = found?.People.Sum(person => person.Tickets.Count) ?? -1;
        if (found is null
            || found.TakenAt != expect.TakenAt
            || found.People.Count != expect.People.Count
            || foundTickets != expectTickets)
        {
            throw new InvalidOperationException(
                "Queue check could not confirm the baseline file at " + path + ".");
        }
    }

    private static StoredDay ToStored(QueueDayBaseline day) => new()
    {
        LocalDate = day.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TakenAt = day.TakenAt.ToString("o", CultureInfo.InvariantCulture),
        People = day.People.Select(person => new StoredPerson
        {
            PersonId = person.PersonId ?? "",
            PersonName = person.PersonName ?? "",
            Tickets = person.Tickets.Select(ToStoredTicket).ToList()
        }).ToList()
    };

    private static StoredTicket ToStoredTicket(QueueDayTicket ticket) => new()
    {
        Number = ticket.Number ?? "",
        SysId = ticket.SysId ?? "",
        Table = ticket.Table ?? "",
        ShortDescription = ticket.ShortDescription ?? "",
        AssigneeId = ticket.AssigneeId ?? "",
        AssigneeName = ticket.AssigneeName ?? "",
        AssigneeUserName = ticket.AssigneeUserName ?? "",
        AssignmentGroupId = ticket.AssignmentGroupId ?? "",
        AssignmentGroupName = ticket.AssignmentGroupName ?? "",
        StateValue = ticket.StateValue ?? "",
        StateDisplay = ticket.StateDisplay ?? "",
        SysUpdatedOn = ticket.SysUpdatedOn ?? "",
        UpdatedBy = ticket.UpdatedBy ?? "",
        CallerId = ticket.CallerId ?? "",
        CallerName = ticket.CallerName ?? "",
        CallerUserName = ticket.CallerUserName ?? ""
    };

    private static QueueDayBaseline? FromStored(StoredDay? day)
    {
        if (day is null || !DateOnly.TryParseExact(day.LocalDate?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;
        var taken = DateTime.TryParse(day.TakenAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : date.ToDateTime(TimeOnly.MinValue);
        var people = (day.People ?? [])
            .Where(person => person is not null && !string.IsNullOrWhiteSpace(person.PersonId))
            .Select(person => new QueueDayPersonBaseline(
                person.PersonId.Trim(),
                person.PersonName?.Trim() ?? "",
                (person.Tickets ?? []).Where(ticket => ticket is not null).Select(FromStoredTicket).ToArray()))
            .ToArray();
        return new QueueDayBaseline(date, taken, people);
    }

    private static QueueDayTicket FromStoredTicket(StoredTicket ticket)
    {
        var updatedOn = ticket.SysUpdatedOn?.Trim() ?? "";
        DateTime? updatedAt = ServiceNowDesk.Alerts.AlertClassifier.TryParseInstant(updatedOn, out var parsed)
            ? parsed
            : null;
        return new QueueDayTicket(
            ticket.Number?.Trim() ?? "",
            ticket.SysId?.Trim() ?? "",
            ticket.Table?.Trim() ?? "",
            ticket.ShortDescription?.Trim() ?? "",
            ticket.AssigneeId?.Trim() ?? "",
            ticket.AssigneeName?.Trim() ?? "",
            ticket.AssigneeUserName?.Trim() ?? "",
            ticket.AssignmentGroupId?.Trim() ?? "",
            ticket.AssignmentGroupName?.Trim() ?? "",
            ticket.StateValue?.Trim() ?? "",
            ticket.StateDisplay?.Trim() ?? "",
            updatedOn,
            updatedAt,
            ticket.UpdatedBy?.Trim() ?? "",
            ticket.CallerId?.Trim() ?? "",
            ticket.CallerName?.Trim() ?? "",
            ticket.CallerUserName?.Trim() ?? "");
    }

    private sealed class StoredFile
    {
        public List<StoredDay> Days { get; set; } = [];
    }

    private sealed class StoredDay
    {
        public string LocalDate { get; set; } = "";

        public string TakenAt { get; set; } = "";

        public List<StoredPerson> People { get; set; } = [];
    }

    private sealed class StoredPerson
    {
        public string PersonId { get; set; } = "";

        public string PersonName { get; set; } = "";

        public List<StoredTicket> Tickets { get; set; } = [];
    }

    private sealed class StoredTicket
    {
        public string Number { get; set; } = "";

        public string SysId { get; set; } = "";

        public string Table { get; set; } = "";

        public string ShortDescription { get; set; } = "";

        public string AssigneeId { get; set; } = "";

        public string AssigneeName { get; set; } = "";

        public string AssigneeUserName { get; set; } = "";

        public string AssignmentGroupId { get; set; } = "";

        public string AssignmentGroupName { get; set; } = "";

        public string StateValue { get; set; } = "";

        public string StateDisplay { get; set; } = "";

        public string SysUpdatedOn { get; set; } = "";

        public string UpdatedBy { get; set; } = "";

        public string CallerId { get; set; } = "";

        public string CallerName { get; set; } = "";

        public string CallerUserName { get; set; } = "";
    }
}
