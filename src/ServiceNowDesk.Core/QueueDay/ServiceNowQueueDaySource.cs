using ServiceNowDesk.Client;

namespace ServiceNowDesk.QueueDay;

public sealed class ServiceNowQueueDaySource : IQueueDaySource
{
    private readonly IServiceNowClient _client;

    public ServiceNowQueueDaySource(IServiceNowClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public Task<IReadOnlyList<QueueDayTicket>> ListOpenAssignedAsync(string assigneeSysId, CancellationToken cancellationToken) =>
        _client.ListOpenAssignedQueueAsync(assigneeSysId, cancellationToken);

    public Task<IReadOnlyList<QueueDayTicket>> LookupAsync(IReadOnlyList<QueueDayTicketRef> tickets, CancellationToken cancellationToken) =>
        _client.LookupQueueTicketsAsync(tickets, cancellationToken);
}