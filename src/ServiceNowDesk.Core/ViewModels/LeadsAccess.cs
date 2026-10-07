namespace ServiceNowDesk.ViewModels;

/// <summary>
/// The Leads menu stays hidden until a leads password is entered. The password itself is not saved.
/// <see cref="Password"/> opens the editable team. <see cref="LockedPassword"/> opens the locked city team.
/// </summary>
public static class LeadsAccess
{
    public const string Password = "iddqd";

    public const string LockedPassword = "idkfa";

    public const string WrongPasswordMessage = "That password did not unlock Leads.";

    public static bool Unlocks(string? entered) =>
        IsEditable(entered) || IsLocked(entered);

    public static bool IsEditable(string? entered) =>
        string.Equals(entered, Password, StringComparison.Ordinal);

    public static bool IsLocked(string? entered) =>
        string.Equals(entered, LockedPassword, StringComparison.Ordinal);
}
