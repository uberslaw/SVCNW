using ServiceNowDesk.Client;
using ServiceNowDesk.GuidedSetup;
using ServiceNowDesk.Models;
using ServiceNowDesk.Navigation;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class GuidedSetupTests
{
    [Fact]
    public void YesOnTheFullOfferStartsAtConnection()
    {
        var setup = Create();
        Assert.Equal(GuidedSetupOfferKind.Full, setup.ConsiderLaunch(signedIn: false));
        Assert.True(setup.ShowOffer);

        var start = setup.ChooseYes(splashVisible: true);

        Assert.True(start.OpenConnection);
        Assert.True(start.CloseSplash);
        Assert.Equal(GuidedSetupPhase.Connection, setup.Phase);
        Assert.Equal(GuidedSetupSpotlight.SignInMethod, setup.Spotlight);
        Assert.Equal(GuidedSetupMotion.ConnectionDim, setup.DimStrength);
        Assert.Equal(GuidedSetupCopy.SessionBanner, setup.BannerText);
        Assert.Equal("Use your mouse to select Browser sign-in (SSO).", setup.InstructionText);
        Assert.True(setup.PulseSignInMethod);
        Assert.False(setup.ShowOffer);
    }

    [Fact]
    public void FullOfferWithoutASplashStillStartsAtConnection()
    {
        var setup = Create();
        setup.ConsiderLaunch(signedIn: false);

        var start = setup.ChooseYes(splashVisible: false);

        Assert.False(start.CloseSplash);
        Assert.True(start.OpenConnection);
        Assert.Equal(GuidedSetupPhase.Connection, setup.Phase);
    }

    [Fact]
    public void SignedInYesStartsAtTheFirstTabAndSkipsConnection()
    {
        var setup = Create();
        Assert.Equal(GuidedSetupOfferKind.Quick, setup.ConsiderLaunch(signedIn: true));

        var start = setup.ChooseYes(splashVisible: false);

        Assert.False(start.OpenConnection);
        Assert.False(start.CloseSplash);
        Assert.Equal(GuidedSetupPhase.Tabs, setup.Phase);
        Assert.Equal(DeskSection.DailyWork, setup.HighlightedTab);
        Assert.Equal(GuidedSetupMotion.TabDim, setup.DimStrength);
        Assert.Equal(DeskSection.DailyWork, setup.TabSections[0]);
        Assert.NotEqual(GuidedSetupPhase.Connection, setup.Phase);
        Assert.NotEqual(GuidedSetupPhase.SignIn, setup.Phase);
    }

    [Fact]
    public void QuickTourLeavesALoadingSplashAloneUntilItCloses()
    {
        var setup = Create();
        setup.ConsiderLaunch(signedIn: true);

        var start = setup.ChooseYes(splashVisible: true);

        Assert.False(start.CloseSplash);
        Assert.False(start.OpenConnection);
        Assert.Equal(GuidedSetupPhase.WaitingToStartTabs, setup.Phase);
        Assert.False(setup.ShowTour);

        setup.SplashClosed(closedItself: true);

        Assert.Equal(GuidedSetupPhase.Tabs, setup.Phase);
        Assert.Equal(DeskSection.DailyWork, setup.HighlightedTab);
    }

    [Fact]
    public void NotThisTimeAsksAgainOnTheNextLaunch()
    {
        var settings = new DeskSettings();
        var setup = Create(settings);
        setup.ConsiderLaunch(signedIn: false);
        setup.ChooseNotThisTime();

        Assert.False(setup.ShowOffer);
        Assert.Equal(GuidedSetupPhase.Idle, setup.Phase);
        Assert.Equal(GuidedSetupOfferChoice.NotThisTime, settings.GuidedSetupOffer);
        Assert.False(settings.GuidedSetupFinished);

        var next = Create(settings);
        Assert.Equal(GuidedSetupOfferKind.Full, next.ConsiderLaunch(signedIn: false));
        Assert.True(next.ShowOffer);
        Assert.Equal(GuidedSetupOfferKind.Quick, Create(settings).ConsiderLaunch(signedIn: true));
    }

    [Fact]
    public void DontAskAgainDoesNotAskOnTheNextLaunch()
    {
        var settings = new DeskSettings();
        var setup = Create(settings);
        setup.ConsiderLaunch(signedIn: false);
        setup.ChooseDontAskAgain();

        Assert.Equal(GuidedSetupOfferChoice.DontAskAgain, settings.GuidedSetupOffer);
        Assert.False(settings.GuidedSetupFinished);
        Assert.Equal(GuidedSetupOfferKind.None, Create(settings).ConsiderLaunch(signedIn: false));
        Assert.Equal(GuidedSetupOfferKind.None, Create(settings).ConsiderLaunch(signedIn: true));

        var fromHelp = Create(settings);
        var start = fromHelp.StartFromHelp(signedIn: true, splashVisible: false);
        Assert.False(start.OpenConnection);
        Assert.Equal(GuidedSetupPhase.Tabs, fromHelp.Phase);

        var signedOut = Create(settings);
        var full = signedOut.StartFromHelp(signedIn: false, splashVisible: false);
        Assert.True(full.OpenConnection);
        Assert.Equal(GuidedSetupPhase.Connection, signedOut.Phase);
    }

    [Fact]
    public void ExitMarksTheTourFinished()
    {
        var settings = new DeskSettings();
        var setup = Create(settings);
        setup.ConsiderLaunch(signedIn: false);
        setup.ChooseYes(splashVisible: false);
        setup.ExitTour();

        Assert.True(settings.GuidedSetupFinished);
        Assert.Equal(GuidedSetupPhase.Finished, setup.Phase);
        Assert.False(setup.ShowTour);
        Assert.Equal(GuidedSetupOfferKind.None, Create(settings).ConsiderLaunch(signedIn: false));
        Assert.Equal(GuidedSetupOfferKind.None, Create(settings).ConsiderLaunch(signedIn: true));

        var resumed = Create(settings);
        resumed.StartFromHelp(signedIn: false, splashVisible: false);
        Assert.Equal(GuidedSetupPhase.Connection, resumed.Phase);
    }

    [Fact]
    public void SignInFailureStaysOnTheSignInStep()
    {
        var setup = ReachSignIn();
        setup.SignInFailed();
        setup.SignInFailed();

        Assert.Equal(GuidedSetupPhase.SignIn, setup.Phase);
        Assert.Equal(GuidedSetupCopy.SignInFailed, setup.FailureText);
        Assert.Equal(GuidedSetupCopy.FollowPrompts, setup.InstructionText);
        Assert.True(setup.ShowTour);

        setup.SplashAppeared();
        Assert.Equal(GuidedSetupPhase.Splash, setup.Phase);
        setup.SignInFailed();
        Assert.Equal(GuidedSetupPhase.SignIn, setup.Phase);
        Assert.Equal(GuidedSetupCopy.SignInFailed, setup.FailureText);
    }

    [Fact]
    public void SplashClosingIncludingClosingItselfAdvancesToTheTabTour()
    {
        var user = ReachSignIn();
        user.SplashAppeared();
        Assert.Equal(GuidedSetupPhase.Splash, user.Phase);
        Assert.Equal(GuidedSetupCopy.SplashCaption, user.InstructionText);
        user.SplashClosed(closedItself: false);
        Assert.Equal(GuidedSetupPhase.Tabs, user.Phase);
        Assert.Equal(DeskSection.DailyWork, user.HighlightedTab);

        var itself = ReachSignIn();
        itself.SignInSucceeded(splashVisible: true);
        Assert.Equal(GuidedSetupPhase.Splash, itself.Phase);
        itself.SplashClosed(closedItself: true);
        Assert.Equal(GuidedSetupPhase.Tabs, itself.Phase);

        var alreadyGone = ReachSignIn();
        alreadyGone.SignInSucceeded(splashVisible: false);
        Assert.Equal(GuidedSetupPhase.Tabs, alreadyGone.Phase);

        var idle = Create();
        idle.SplashClosed(closedItself: true);
        idle.SplashAppeared();
        Assert.Equal(GuidedSetupPhase.Idle, idle.Phase);
    }

    [Fact]
    public void DownloadThatFinishesClosesTheSplashAsItself()
    {
        var setup = ReachSignIn();
        var splash = new StartupDownloadModel();
        splash.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(StartupDownloadModel.ShowScreen))
                return;
            if (splash.ShowScreen)
                setup.SplashAppeared();
            else
                setup.SplashClosed(closedItself: !splash.ClosedByUser);
        };

        splash.Begin(1);
        Assert.False(splash.ClosedByUser);
        Assert.Equal(GuidedSetupPhase.Splash, setup.Phase);
        splash.Start("Choices");
        splash.Complete();

        Assert.False(splash.ShowScreen);
        Assert.False(splash.ClosedByUser);
        Assert.Equal(GuidedSetupPhase.Tabs, setup.Phase);
    }

    [Fact]
    public void UserClosingTheSplashAlsoAdvances()
    {
        var setup = ReachSignIn();
        var splash = new StartupDownloadModel();
        splash.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(StartupDownloadModel.ShowScreen))
                return;
            if (splash.ShowScreen)
                setup.SplashAppeared();
            else
                setup.SplashClosed(closedItself: !splash.ClosedByUser);
        };

        splash.Begin(2);
        splash.Dismiss();

        Assert.True(splash.ClosedByUser);
        Assert.False(splash.ShowScreen);
        Assert.Equal(GuidedSetupPhase.Tabs, setup.Phase);
    }

    [Fact]
    public void NextWalksTheVisibleTabsAndSkipsLockedLeads()
    {
        var visible = DeskNavigation.Visible(leadsEnabled: true).ToList();
        Assert.Contains(visible, item => item.Section == DeskSection.Leads);
        var settings = new DeskSettings();
        var setup = Create(settings, visible, leadsLocked: true);
        setup.StartFromHelp(signedIn: true, splashVisible: false);

        var walked = Walk(setup);

        Assert.Equal(GuidedSetupScript.All.Select(stop => stop.Section), walked.Select(step => step.Section));
        Assert.Equal(GuidedSetupScript.All.Select(stop => stop.Line), walked.Select(step => step.Line));
        Assert.DoesNotContain(walked, step => step.Section == DeskSection.Leads);
        Assert.Equal(GuidedSetupPhase.Finished, setup.Phase);
        Assert.True(settings.GuidedSetupFinished);
        Assert.Equal(
            "Daily Work — Today's list, including your own notes and the group queue.",
            GuidedSetupScript.All[0].Line);
        Assert.Equal(
            "Legend — What the colours on the lists mean.",
            GuidedSetupScript.All[^1].Line);
    }

    [Fact]
    public void AMissingTabSkipsItsLine()
    {
        var visible = DeskNavigation.Visible(leadsEnabled: false)
            .Where(item => item.Section != DeskSection.Hardware)
            .ToList();
        var setup = Create(tabs: visible, leadsLocked: true);
        setup.StartFromHelp(signedIn: true, splashVisible: false);

        var walked = Walk(setup);

        Assert.DoesNotContain(walked, step => step.Section == DeskSection.Hardware);
        Assert.DoesNotContain(walked, step => step.Section == DeskSection.Leads);
        var sections = walked.Select(step => step.Section).ToList();
        Assert.Equal(DeskSection.Search, sections[sections.IndexOf(DeskSection.WalkUps) + 1]);
        Assert.Equal(GuidedSetupPhase.Finished, setup.Phase);
    }

    [Fact]
    public void UnlockedLeadsHasNoInventedLine()
    {
        var tour = GuidedSetupScript.ForTour(DeskNavigation.Visible(leadsEnabled: true).ToList(), leadsLocked: false);
        Assert.DoesNotContain(tour, stop => stop.Section == DeskSection.Leads);
        Assert.Equal(DeskSection.Connection, tour[^2].Section);
        Assert.Equal(DeskSection.Legend, tour[^1].Section);
    }

    [Fact]
    public void BrowserOptionLabelIsTheDropdownText()
    {
        var label = new ConnectionViewModel().AuthChoices.Single(choice => choice.Mode == ServiceNowAuthMode.BrowserSession).Label;
        Assert.Equal("Browser sign-in (SSO)", label);
        Assert.Equal("Sign in with browser", GuidedSetupCopy.SignInButtonLabel);
        Assert.Equal(TimeSpan.FromSeconds(1), GuidedSetupMotion.DimDuration);
        Assert.Equal(TimeSpan.FromSeconds(1), GuidedSetupMotion.GrowDuration);
        Assert.Equal(TimeSpan.FromSeconds(0.6), GuidedSetupMotion.MoveDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), GuidedSetupMotion.SignInMethodPulse);
        Assert.Equal(TimeSpan.FromSeconds(3), GuidedSetupMotion.SignInButtonPulse);
        Assert.Equal(2d, GuidedSetupMotion.PulsePerSecond);
        Assert.Equal(0.6, GuidedSetupMotion.ConnectionDim);
        Assert.Equal(0.3, GuidedSetupMotion.TabDim);

        var renamed = Create(label: "Company SSO");
        renamed.ConsiderLaunch(signedIn: false);
        renamed.ChooseYes(splashVisible: false);
        Assert.Equal("Use your mouse to select Company SSO.", renamed.InstructionText);
    }

    [Fact]
    public void ChoosingBrowserSignInMovesToTheSignInStep()
    {
        var setup = Create();
        setup.ConsiderLaunch(signedIn: false);
        setup.ChooseYes(splashVisible: false);
        setup.NotifyAuthMode(ServiceNowAuthMode.OAuthPassword);
        Assert.Equal(GuidedSetupPhase.Connection, setup.Phase);

        setup.NotifyAuthMode(ServiceNowAuthMode.BrowserSession);

        Assert.Equal(GuidedSetupPhase.SignIn, setup.Phase);
        Assert.Equal("", setup.BannerText);
        Assert.Equal(GuidedSetupCopy.FollowPrompts, setup.InstructionText);
        Assert.True(setup.PulseSignInButton);
        Assert.Equal(GuidedSetupSpotlight.SignInButton, setup.Spotlight);
    }

    [Fact]
    public void ConfirmingTheDropdownWhenBrowserSignInIsAlreadySelectedAdvances()
    {
        var setup = Create(mode: ServiceNowAuthMode.BrowserSession);
        setup.ConsiderLaunch(signedIn: false);
        setup.ChooseYes(splashVisible: false);
        Assert.Equal(GuidedSetupPhase.Connection, setup.Phase);

        setup.ConfirmSignInMethod();

        Assert.Equal(GuidedSetupPhase.SignIn, setup.Phase);
    }

    [Fact]
    public void OfferChoiceRoundTripsThroughTheSettingsFile()
    {
        var settings = new DeskSettings
        {
            InstanceUrl = "https://example.service-now.com",
            Password = "secret",
            GuidedSetupOffer = GuidedSetupOfferChoice.DontAskAgain,
            GuidedSetupFinished = true
        };
        var connection = new ConnectionViewModel();
        connection.Load(settings);
        var built = connection.BuildSettings();
        Assert.Equal(GuidedSetupOfferChoice.DontAskAgain, built.GuidedSetupOffer);
        Assert.True(built.GuidedSetupFinished);
        Assert.Equal("secret", built.Password);

        var json = DeskSettingsFile.Serialize(built, value => value ?? "");
        Assert.Contains("\"GuidedSetupOffer\":", json, StringComparison.Ordinal);
        Assert.Contains("\"GuidedSetupFinished\":", json, StringComparison.Ordinal);
        var loaded = DeskSettingsFile.Deserialize(json, value => value ?? "");
        Assert.Equal(GuidedSetupOfferChoice.DontAskAgain, loaded.GuidedSetupOffer);
        Assert.True(loaded.GuidedSetupFinished);
        Assert.Equal("secret", loaded.Password);

        var missing = DeskSettingsFile.Deserialize("{\"InstanceUrl\":\"https://example.service-now.com\"}", value => value ?? "");
        Assert.Equal(GuidedSetupOfferChoice.Unset, missing.GuidedSetupOffer);
        Assert.False(missing.GuidedSetupFinished);
    }

    [Fact]
    public async Task LaunchOfferFollowsTheSavedChoiceWithoutAWindow()
    {
        var store = new MemorySettingsStore();
        var main = new MainViewModel(store, new RecordingDesktopServices());
        await main.InitializeAsync();
        Assert.Equal(GuidedSetupOfferKind.Full, main.Guided.OfferKind);
        main.Startup.Begin(1);
        Assert.True(main.Startup.ShowScreen);

        main.Guided.ChooseYes(splashVisible: true);

        Assert.False(main.Startup.ShowScreen);
        Assert.Equal(DeskSection.Connection, main.SelectedSection);
        Assert.Equal(GuidedSetupPhase.Connection, main.Guided.Phase);

        main.Guided.ExitTour();
        Assert.True(store.Current.GuidedSetupFinished);
        var again = new MainViewModel(store, new RecordingDesktopServices());
        await again.InitializeAsync();
        Assert.Equal(GuidedSetupOfferKind.None, again.Guided.ConsiderLaunch(signedIn: false));
        Assert.False(again.Guided.ShowOffer);
    }

    [Fact]
    public async Task NotThisTimeIsStoredAndAskedAgain()
    {
        var store = new MemorySettingsStore();
        var main = new MainViewModel(store, new RecordingDesktopServices());
        await main.InitializeAsync();
        main.Guided.ChooseNotThisTime();
        Assert.Equal(GuidedSetupOfferChoice.NotThisTime, store.Current.GuidedSetupOffer);
        Assert.False(store.Current.GuidedSetupFinished);

        var next = new MainViewModel(store, new RecordingDesktopServices());
        await next.InitializeAsync();
        Assert.True(next.Guided.ShowOffer);
        Assert.Equal(GuidedSetupOfferKind.Full, next.Guided.OfferKind);
    }

    [Fact]
    public async Task DontAskAgainIsStoredAndSkipped()
    {
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { GuidedSetupOffer = GuidedSetupOfferChoice.DontAskAgain });
        var main = new MainViewModel(store, new RecordingDesktopServices());
        await main.InitializeAsync();

        Assert.False(main.Guided.ShowOffer);
        Assert.Equal(GuidedSetupOfferKind.None, main.Guided.OfferKind);
    }

    [Fact]
    public async Task SignedInYesDoesNotCloseALoadingSplashOrOpenConnection()
    {
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { UseSampleData = true, DownloadCacheOnLaunch = false });
        var main = new MainViewModel(store, new RecordingDesktopServices(), lists: FreshLists());
        await main.InitializeAsync();
        Assert.True(main.IsConnected);
        Assert.Equal(GuidedSetupOfferKind.Quick, main.Guided.OfferKind);
        Assert.NotEqual(DeskSection.Connection, main.SelectedSection);

        main.Startup.Begin(2);
        var section = main.SelectedSection;
        main.Guided.ChooseYes(splashVisible: true);

        Assert.True(main.Startup.ShowScreen);
        Assert.Equal(section, main.SelectedSection);
        Assert.Equal(GuidedSetupPhase.WaitingToStartTabs, main.Guided.Phase);

        main.Startup.Dismiss();
        Assert.Equal(GuidedSetupPhase.Tabs, main.Guided.Phase);
        Assert.Equal(DeskSection.DailyWork, main.Guided.HighlightedTab);
        main.DisconnectCommand.Execute(null);
    }

    [Fact]
    public async Task HelpStartsTheFullSetupWhenSignedOutAndTheTabTourWhenSignedIn()
    {
        var signedOut = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        await signedOut.InitializeAsync();
        signedOut.StartGuidedSetupFromHelp();
        Assert.Equal(GuidedSetupPhase.Connection, signedOut.Guided.Phase);
        Assert.Equal(DeskSection.Connection, signedOut.SelectedSection);

        var store = new MemorySettingsStore();
        store.Save(new DeskSettings
        {
            UseSampleData = true,
            DownloadCacheOnLaunch = false,
            GuidedSetupOffer = GuidedSetupOfferChoice.DontAskAgain
        });
        var signedIn = new MainViewModel(store, new RecordingDesktopServices(), lists: FreshLists());
        await signedIn.InitializeAsync();
        Assert.False(signedIn.Guided.ShowOffer);
        signedIn.StartGuidedSetupFromHelp();
        Assert.Equal(GuidedSetupPhase.Tabs, signedIn.Guided.Phase);
        Assert.NotEqual(DeskSection.Connection, signedIn.SelectedSection);
        signedIn.DisconnectCommand.Execute(null);
    }

    private static MemoryDeskListStore FreshLists()
    {
        var now = DateTimeOffset.UtcNow;
        CachedTicketList List() => new() { CapturedAt = now };
        var store = new MemoryDeskListStore();
        store.Save(DeskListScope.Practice, new DeskListSnapshot
        {
            ChoicesCapturedAt = now,
            GroupsCapturedAt = now,
            MembersCapturedAt = now,
            ServiceOfferingsCapturedAt = now,
            ConfigurationItemsCapturedAt = now,
            Incidents = List(),
            Requests = List(),
            WalkUps = List(),
            Knowledge = List()
        });
        return store;
    }

    [Fact]
    public async Task CanceledBrowserSignInStaysOnTheSignInStep()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices(), new CanceledBrowserSignIn());
        await main.InitializeAsync();
        main.Connection.InstanceUrl = "https://example.service-now.com";
        main.Guided.ChooseYes(splashVisible: false);
        main.Guided.NotifyAuthMode(ServiceNowAuthMode.BrowserSession);
        Assert.Equal(GuidedSetupPhase.SignIn, main.Guided.Phase);

        await main.SignInWithBrowserCommand.ExecuteAsync(null);

        Assert.Equal(GuidedSetupPhase.SignIn, main.Guided.Phase);
        Assert.Equal(GuidedSetupCopy.SignInFailed, main.Guided.FailureText);
        Assert.False(main.IsConnected);
    }

    [Fact]
    public async Task SplashOnTheShellAdvancesWhenItClosesItselfOrTheUserClosesIt()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        await main.InitializeAsync();
        main.Guided.ChooseYes(splashVisible: false);
        main.Guided.NotifyAuthMode(ServiceNowAuthMode.BrowserSession);

        main.Startup.Begin(1);
        Assert.Equal(GuidedSetupPhase.Splash, main.Guided.Phase);
        main.Startup.Start("Choices");
        main.Startup.Complete();
        Assert.False(main.Startup.ClosedByUser);
        Assert.Equal(GuidedSetupPhase.Tabs, main.Guided.Phase);

        var user = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        await user.InitializeAsync();
        user.Guided.ChooseYes(splashVisible: false);
        user.Guided.NotifyAuthMode(ServiceNowAuthMode.BrowserSession);
        user.Startup.Begin(2);
        user.Startup.Dismiss();
        Assert.True(user.Startup.ClosedByUser);
        Assert.Equal(GuidedSetupPhase.Tabs, user.Guided.Phase);

        var idle = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        await idle.InitializeAsync();
        idle.Guided.ChooseNotThisTime();
        idle.Startup.Begin(1);
        idle.Startup.Dismiss();
        Assert.Equal(GuidedSetupPhase.Idle, idle.Guided.Phase);
    }

    private static GuidedSetupController ReachSignIn()
    {
        var setup = Create();
        setup.ConsiderLaunch(signedIn: false);
        setup.ChooseYes(splashVisible: false);
        setup.NotifyAuthMode(ServiceNowAuthMode.BrowserSession);
        Assert.Equal(GuidedSetupPhase.SignIn, setup.Phase);
        return setup;
    }

    private static List<(DeskSection Section, string Line)> Walk(GuidedSetupController setup)
    {
        var seen = new List<(DeskSection Section, string Line)>();
        Assert.Equal(GuidedSetupPhase.Tabs, setup.Phase);
        while (setup.Phase == GuidedSetupPhase.Tabs)
        {
            seen.Add((setup.HighlightedTab, setup.TabLine));
            Assert.True(setup.ShowNext);
            setup.ChooseNext();
        }

        return seen;
    }

    private static GuidedSetupController Create(
        DeskSettings? settings = null,
        IReadOnlyList<DeskNavItem>? tabs = null,
        bool? leadsLocked = null,
        string? label = null,
        ServiceNowAuthMode mode = ServiceNowAuthMode.Basic)
    {
        settings ??= new DeskSettings();
        tabs ??= DeskNavigation.Visible(leadsEnabled: false).ToList();
        var locked = leadsLocked ?? true;
        var current = mode;
        var setup = new GuidedSetupController(
            (choice, finished) =>
            {
                settings.GuidedSetupOffer = choice;
                settings.GuidedSetupFinished = finished;
            },
            () => tabs,
            () => locked,
            () => label ?? new ConnectionViewModel().AuthChoices.Single(choice => choice.Mode == ServiceNowAuthMode.BrowserSession).Label,
            () => current);
        setup.Load(settings.GuidedSetupOffer, settings.GuidedSetupFinished);
        return setup;
    }

    private sealed class CanceledBrowserSignIn : IBrowserSignIn
    {
        public Task<BrowserSignInResult> SignInAsync(Uri instanceUri, CancellationToken cancellationToken) =>
            Task.FromException<BrowserSignInResult>(new BrowserSignInCanceledException());
    }
}
