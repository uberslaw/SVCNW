using System.IO;
using System.Security.Cryptography;
using System.Text;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.Services;

public sealed class DpapiSettingsStore : ISettingsStore
{
    public DeskSettings Load()
    {
        try
        {
            if (!File.Exists(DeskAppData.SettingsPath))
                return new DeskSettings();

            return DeskSettingsFile.Deserialize(File.ReadAllText(DeskAppData.SettingsPath), Unprotect);
        }
        catch
        {
            return new DeskSettings();
        }
    }

    public void Save(DeskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DeskAppData.WriteSettings(DeskSettingsFile.Serialize(settings, Protect));
    }

    private static string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
    }

    private static string Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
