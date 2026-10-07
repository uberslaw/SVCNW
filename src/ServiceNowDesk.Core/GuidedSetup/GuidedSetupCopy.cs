using ServiceNowDesk.Models;
using ServiceNowDesk.Navigation;

namespace ServiceNowDesk.GuidedSetup;

/// <summary>Last answer to the launch offer. Not this time still asks on the next launch.</summary>
public enum GuidedSetupOfferChoice
{
    Unset = 0,
    NotThisTime = 1,
    DontAskAgain = 2
}

public enum GuidedSetupOfferKind
{
    None,
    Full,
    Quick
}

public enum GuidedSetupPhase
{
    Idle,
    Offer,
    Connection,
    SignIn,
    Splash,
    WaitingToStartTabs,
    Tabs,
    Finished
}

public enum GuidedSetupSpotlight
{
    None,
    SignInMethod,
    SignInButton,
    Tab
}

public readonly record struct GuidedSetupStart(bool CloseSplash, bool OpenConnection);

public readonly record struct GuidedSetupStop(DeskSection Section, string Line);

/// <summary>
/// Timings for the in-window spotlight. The hole grows, then moves; it is not a separate topmost window.
/// </summary>
public static class GuidedSetupMotion
{
    public static readonly TimeSpan DimDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan GrowDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MoveDuration = TimeSpan.FromSeconds(0.6);
    public static readonly TimeSpan SignInMethodPulse = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan SignInButtonPulse = TimeSpan.FromSeconds(3);
    public const double PulsePerSecond = 2;
    public const double ConnectionDim = 0.6;
    public const double TabDim = 0.3;
}

public static class GuidedSetupCopy
{
    public const string SignInButtonLabel = "Sign in with browser";

    public const string SessionBanner =
        "The sign-in process needs to be completed once every 24hrs or whenever the browser session is reset (currently 24hrs). Closing the application does not lose the browser session.";

    public const string FollowPrompts =
        "Follow the prompts to sign in with your Arup username, password, or passkey. Wait a few moments for the authentication window to close. You will then see a splash screen of data downloading.";

    public const string SplashCaption =
        "You can close the splash screen and the data will continue to load in the background.";

    public const string SignInFailed = "Sign-in did not finish.";

    public const string FullTitle = "Guided setup";

    public const string FullBody = "Sign in, then take a short tour of each tab.";

    public const string QuickTitle = "Quick tour";

    public const string QuickBody = "Take a short tour of each tab.";

    public static string SelectMethod(string optionLabel)
    {
        var label = string.IsNullOrWhiteSpace(optionLabel) ? "Browser sign-in (SSO)" : optionLabel.Trim();
        return "Use your mouse to select " + label + ".";
    }
}

public static class GuidedSetupScript
{
    public static IReadOnlyList<GuidedSetupStop> All { get; } =
    [
        new(DeskSection.DailyWork, "Daily Work — Today's list, including your own notes and the group queue."),
        new(DeskSection.InTheMix, "In The Mix — Incidents, request items, and walk-ups for your team, in one list."),
        new(DeskSection.Incidents, "Incidents — Open and update incidents."),
        new(DeskSection.RequestedItems, "Request items — Open request items, or create one from the catalog."),
        new(DeskSection.WalkUps, "Walk-up — Record someone who comes to the desk."),
        new(DeskSection.Hardware, "Hardware — Computers at your office, including stock you receive."),
        new(DeskSection.Search, "Search — Find a ticket or a knowledge article."),
        new(DeskSection.Knowledge, "Knowledge — Read a knowledge article."),
        new(DeskSection.Notifications, "Notifications — Choose what makes the top bar jiggle."),
        new(DeskSection.Settings, "Settings — Notification offices, jiggle speed, and the Leads password."),
        new(DeskSection.Connection, "Connection — Sign in and see the session."),
        new(DeskSection.Legend, "Legend — What the colours on the lists mean.")
    ];

    /// <summary>
    /// Tabs in left-nav order. Locked Leads is skipped. A tab with no line in this build is skipped.
    /// </summary>
    public static IReadOnlyList<GuidedSetupStop> ForTour(IReadOnlyList<DeskNavItem> visibleInOrder, bool leadsLocked)
    {
        ArgumentNullException.ThrowIfNull(visibleInOrder);
        var lines = All.ToDictionary(stop => stop.Section);
        var tour = new List<GuidedSetupStop>();
        foreach (var item in visibleInOrder)
        {
            if (!item.ShownInNav)
                continue;
            if (leadsLocked && (item.RequiresLeads || item.Section == DeskSection.Leads))
                continue;
            if (!lines.TryGetValue(item.Section, out var stop))
                continue;
            tour.Add(stop);
        }

        return tour;
    }
}
