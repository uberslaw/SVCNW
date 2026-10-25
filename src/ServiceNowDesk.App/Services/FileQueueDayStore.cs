using System.IO;
using ServiceNowDesk.QueueDay;

namespace ServiceNowDesk.Services;

/// <summary>
/// Today's queue baseline under %AppData%\ServiceNowDesk\queue-day.json.
/// The file holds ticket fields only. It does not store passwords, cookies, or tokens.
/// </summary>
public sealed class FileQueueDayStore : IQueueDayStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileQueueDayStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Enter a path for the queue check file.", nameof(path));
        _path = path;
    }

    public static FileQueueDayStore InApplicationData() => new(DeskAppData.QueueDayPath);

    public QueueDayBaseline? Find(DateOnly localDate)
    {
        lock (_gate)
            return Read().FirstOrDefault(day => day.LocalDate == localDate);
    }

    public void Save(QueueDayBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        lock (_gate)
        {
            var keep = Read()
                .Where(day => day.LocalDate >= baseline.LocalDate.AddDays(-1) && day.LocalDate != baseline.LocalDate)
                .ToList();
            keep.Add(baseline);
            QueueDayFileCodec.WriteVerified(_path, keep, baseline);
        }
    }

    private IReadOnlyList<QueueDayBaseline> Read()
    {
        try
        {
            if (!File.Exists(_path))
                return [];
            return QueueDayFileCodec.Deserialize(File.ReadAllText(_path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
