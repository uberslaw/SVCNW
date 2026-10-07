using System.Text;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class LeadsAccessTests
{
    [Fact]
    public void IddqdUnlocksLeadsAndAnyOtherPasswordDoesNot()
    {
        var connection = new ConnectionViewModel();
        Assert.False(connection.LeadsEnabled);
        Assert.False(new DeskSettings().LeadsEnabled);

        connection.LeadsPassword = "";
        connection.UnlockLeads();
        Assert.False(connection.LeadsEnabled);
        Assert.Equal("", connection.LeadsAccessStatus);

        foreach (var wrong in new[] { "IDDQD", "Iddqd", "iddqD", " iddqd", "iddqd ", "IDKFA", "Idkfa", " idkfa", "idkfa ", "nope" })
        {
            connection.LeadsPassword = wrong;
            connection.UnlockLeads();
            Assert.False(connection.LeadsEnabled);
            Assert.Equal(LeadsAccess.WrongPasswordMessage, connection.LeadsAccessStatus);
        }

        connection.LeadsPassword = LeadsAccess.Password;
        connection.UnlockLeads();
        Assert.True(connection.LeadsEnabled);
        Assert.Equal("", connection.LeadsPassword);
        Assert.Equal("", connection.LeadsAccessStatus);

        connection.HideLeads();
        Assert.False(connection.LeadsEnabled);
        Assert.False(connection.LeadsTeamLocked);
        Assert.Equal("", connection.LeadsPassword);
    }

    [Fact]
    public void IdkfaLocksTheTeamAndIddqdStaysEditable()
    {
        var connection = new ConnectionViewModel();
        connection.LeadsPassword = LeadsAccess.Password;
        connection.UnlockLeads();
        Assert.True(connection.LeadsEnabled);
        Assert.False(connection.LeadsTeamLocked);
        Assert.Equal("", connection.LeadsPassword);

        connection.LeadsPassword = LeadsAccess.LockedPassword;
        connection.UnlockLeads();
        Assert.True(connection.LeadsEnabled);
        Assert.True(connection.LeadsTeamLocked);
        Assert.Equal("", connection.LeadsPassword);

        var json = DeskSettingsFile.Serialize(connection.BuildSettings(), Protect);
        Assert.Contains("\"LeadsTeamLocked\": true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("idkfa", json, StringComparison.Ordinal);
        Assert.DoesNotContain("iddqd", json, StringComparison.Ordinal);
        Assert.DoesNotContain("LeadsPassword", json, StringComparison.Ordinal);

        var again = new ConnectionViewModel();
        again.Load(DeskSettingsFile.Deserialize(json, Unprotect));
        Assert.True(again.LeadsEnabled);
        Assert.True(again.LeadsTeamLocked);
        Assert.Equal("", again.LeadsPassword);

        again.LeadsPassword = "iddqd";
        again.UnlockLeads();
        Assert.True(again.LeadsEnabled);
        Assert.False(again.LeadsTeamLocked);

        again.LeadsPassword = "idkfa";
        again.UnlockLeads();
        again.HideLeads();
        Assert.False(again.LeadsEnabled);
        Assert.False(again.LeadsTeamLocked);
        var hidden = DeskSettingsFile.Serialize(again.BuildSettings(), Protect);
        Assert.Contains("\"LeadsEnabled\": false", hidden, StringComparison.Ordinal);
        Assert.Contains("\"LeadsTeamLocked\": false", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("idkfa", hidden, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUnlockFlagRoundTripsAndThePasswordIsNotStored()
    {
        var connection = new ConnectionViewModel();
        connection.Password = "snow-secret";
        connection.ClientSecret = "oauth-secret";
        connection.LeadsPassword = "iddqd";
        connection.UnlockLeads();

        var settings = connection.BuildSettings();
        Assert.True(settings.LeadsEnabled);
        Assert.Equal("snow-secret", settings.Password);

        var json = DeskSettingsFile.Serialize(settings, Protect);
        Assert.Contains("\"LeadsEnabled\": true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("iddqd", json, StringComparison.Ordinal);
        Assert.DoesNotContain("LeadsPassword", json, StringComparison.Ordinal);
        Assert.DoesNotContain("snow-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("oauth-secret", json, StringComparison.Ordinal);

        var loaded = DeskSettingsFile.Deserialize(json, Unprotect);
        Assert.True(loaded.LeadsEnabled);
        Assert.Equal("snow-secret", loaded.Password);
        Assert.Equal("oauth-secret", loaded.ClientSecret);

        var again = new ConnectionViewModel();
        again.Load(loaded);
        Assert.True(again.LeadsEnabled);
        Assert.Equal("", again.LeadsPassword);
        Assert.True(again.BuildSettings().LeadsEnabled);

        again.HideLeads();
        var hidden = DeskSettingsFile.Serialize(again.BuildSettings(), Protect);
        Assert.Contains("\"LeadsEnabled\": false", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("iddqd", hidden, StringComparison.Ordinal);
        Assert.False(DeskSettingsFile.Deserialize(hidden, Unprotect).LeadsEnabled);

        var missing = DeskSettingsFile.Deserialize("{\"InstanceUrl\":\"https://example.service-now.com\"}", Unprotect);
        Assert.False(missing.LeadsEnabled);
    }

    [Fact]
    public void ALockedLeadsShortcutDoesNothingAndHideLeavesThePage()
    {
        var store = new MemorySettingsStore();
        var main = new MainViewModel(store, new RecordingDesktopServices());
        main.SelectedSection = DeskSection.Incidents;

        Assert.False(main.TrySelect(DeskSection.Leads));
        Assert.Equal(DeskSection.Incidents, main.SelectedSection);
        Assert.False(store.Current.LeadsEnabled);

        main.Connection.LeadsPassword = "wrong";
        main.Connection.UnlockLeadsCommand.Execute(null);
        Assert.False(main.Connection.LeadsEnabled);
        Assert.False(main.TrySelect(DeskSection.Leads));
        Assert.Equal(DeskSection.Incidents, main.SelectedSection);

        main.Connection.LeadsPassword = "iddqd";
        main.Connection.UnlockLeadsCommand.Execute(null);
        Assert.True(store.Current.LeadsEnabled);
        Assert.DoesNotContain("iddqd", DeskSettingsFile.Serialize(store.Current, Protect), StringComparison.Ordinal);
        Assert.True(main.TrySelect(DeskSection.Leads));
        Assert.Equal(DeskSection.Leads, main.SelectedSection);

        main.Connection.HideLeadsCommand.Execute(null);
        Assert.False(store.Current.LeadsEnabled);
        Assert.False(main.Connection.LeadsEnabled);
        Assert.Equal(DeskSection.Incidents, main.SelectedSection);
        Assert.False(main.TrySelect(DeskSection.Leads));
        Assert.Equal("", main.Connection.LeadsPassword);

        var reloaded = new ConnectionViewModel();
        reloaded.Load(store.Current);
        Assert.False(reloaded.LeadsEnabled);
        Assert.Equal("", reloaded.LeadsPassword);
    }

    private static string Protect(string? value) =>
        string.IsNullOrEmpty(value) ? "" : "ENC:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        const string prefix = "ENC:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
            return "";
        return Encoding.UTF8.GetString(Convert.FromBase64String(value[prefix.Length..]));
    }
}
