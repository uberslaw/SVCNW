namespace ServiceNowDesk.ViewModels;

/// <summary>
/// The Leads menu stays hidden until this password is entered. Only the resulting flag is saved.
/// </summary>
public static class LeadsAccess
{
    public const string Password = "iddqd";

    public const string WrongPasswordMessage = "That password did not unlock Leads.";

    public static bool Unlocks(string? entered) =>
        string.Equals(entered, Password, StringComparison.Ordinal);
}
