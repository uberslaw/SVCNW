namespace ServiceNowDesk.QueueDay;

public sealed class MemoryQueueDayStore : IQueueDayStore
{
    private readonly Dictionary<DateOnly, QueueDayBaseline> _days = [];

    public QueueDayBaseline? Find(DateOnly localDate) =>
        _days.TryGetValue(localDate, out var baseline) ? baseline : null;

    public void Save(QueueDayBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        _days[baseline.LocalDate] = Copy(baseline);
    }

    private static QueueDayBaseline Copy(QueueDayBaseline baseline) =>
        new(
            baseline.LocalDate,
            baseline.TakenAt,
            baseline.People.Select(person => new QueueDayPersonBaseline(
                person.PersonId,
                person.PersonName,
                person.Tickets.ToArray())).ToArray());
}
