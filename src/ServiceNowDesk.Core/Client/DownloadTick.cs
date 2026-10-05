namespace ServiceNowDesk.Client;

public readonly record struct DownloadTick(int Completed, int Total)
{
    public int Percent
    {
        get
        {
            if (Total <= 0 || Completed <= 0)
                return 0;
            if (Completed >= Total)
                return 100;
            return (int)Math.Min(99, Math.Round(100d * Completed / Total));
        }
    }
}
