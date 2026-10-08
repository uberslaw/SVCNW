namespace ServiceNowDesk.Services;

/// <summary>
/// Files under %AppData%\ServiceNowDesk. Settings and personal notes are separate files.
/// A settings save writes only settings.json.
/// </summary>
public static class DeskAppData
{
    public const string FolderName = "ServiceNowDesk";
    public const string SettingsFileName = "settings.json";
    public const string DailyTasksFileName = "daily-tasks.json";
    public const string QueueDismissalsFileName = "queue-dismissals.json";

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    public static string SettingsPath => Path.Combine(Folder, SettingsFileName);

    public static string DailyTasksPath => Path.Combine(Folder, DailyTasksFileName);

    public static string QueueDismissalsPath => Path.Combine(Folder, QueueDismissalsFileName);

    public static void WriteSettings(string json, string? folder = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        var directory = string.IsNullOrWhiteSpace(folder) ? Folder : folder;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, SettingsFileName), json);
    }
}
