using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Models;
using ServiceNowDesk.Navigation;

namespace ServiceNowDesk.GuidedSetup;

/// <summary>
/// First-run offer and tour. The window draws this; the steps themselves do not open a window.
/// </summary>
public sealed partial class GuidedSetupController : ObservableObject
{
    private readonly Action<GuidedSetupOfferChoice, bool> _persist;
    private readonly Func<IReadOnlyList<DeskNavItem>> _visibleTabs;
    private readonly Func<bool> _leadsLocked;
    private readonly Func<string> _browserSignInLabel;
    private readonly Func<ServiceNowAuthMode> _currentAuthMode;
    private readonly Func<bool> _splashVisible;
    private GuidedSetupOfferChoice _choice;
    private bool _finished;
    private ServiceNowAuthMode _seenMode;
    private IReadOnlyList<GuidedSetupStop> _tabs = [];
    private int _tabIndex;

    public GuidedSetupController(
        Action<GuidedSetupOfferChoice, bool> persist,
        Func<IReadOnlyList<DeskNavItem>> visibleTabs,
        Func<bool> leadsLocked,
        Func<string> browserSignInLabel,
        Func<ServiceNowAuthMode> currentAuthMode,
        Func<bool>? splashVisible = null)
    {
        ArgumentNullException.ThrowIfNull(persist);
        ArgumentNullException.ThrowIfNull(visibleTabs);
        ArgumentNullException.ThrowIfNull(leadsLocked);
        ArgumentNullException.ThrowIfNull(browserSignInLabel);
        ArgumentNullException.ThrowIfNull(currentAuthMode);
        _persist = persist;
        _visibleTabs = visibleTabs;
        _leadsLocked = leadsLocked;
        _browserSignInLabel = browserSignInLabel;
        _currentAuthMode = currentAuthMode;
        _splashVisible = splashVisible ?? (() => false);
        _seenMode = currentAuthMode();
    }

    public event EventHandler<GuidedSetupStart>? StartApplied;

    [ObservableProperty] private bool showOffer;
    [ObservableProperty] private bool showTour;
    [ObservableProperty] private bool showChrome;
    [ObservableProperty] private GuidedSetupOfferKind offerKind;
    [ObservableProperty] private string offerTitle = "";
    [ObservableProperty] private string offerBody = "";
    [ObservableProperty] private GuidedSetupPhase phase = GuidedSetupPhase.Idle;
    [ObservableProperty] private string bannerText = "";
    [ObservableProperty] private string instructionText = "";
    [ObservableProperty] private string failureText = "";
    [ObservableProperty] private string tabLine = "";
    [ObservableProperty] private bool showNext;
    [ObservableProperty] private DeskSection highlightedTab = DeskSection.DailyWork;
    [ObservableProperty] private GuidedSetupSpotlight spotlight;
    [ObservableProperty] private double dimStrength;
    [ObservableProperty] private bool pulseSignInMethod;
    [ObservableProperty] private bool pulseSignInButton;

    public GuidedSetupOfferChoice StoredChoice => _choice;

    public bool StoredFinished => _finished;

    public IReadOnlyList<DeskSection> TabSections => _tabs.Select(stop => stop.Section).ToArray();

    partial void OnShowOfferChanged(bool value) => ShowChrome = value || ShowTour;

    partial void OnShowTourChanged(bool value) => ShowChrome = ShowOffer || value;

    public void Load(GuidedSetupOfferChoice choice, bool finished)
    {
        _choice = choice;
        _finished = finished;
    }

    public GuidedSetupOfferKind ConsiderLaunch(bool signedIn)
    {
        if (_finished || _choice == GuidedSetupOfferChoice.DontAskAgain)
        {
            ShowOffer = false;
            OfferKind = GuidedSetupOfferKind.None;
            if (Phase is GuidedSetupPhase.Idle or GuidedSetupPhase.Offer)
                Phase = GuidedSetupPhase.Idle;
            return GuidedSetupOfferKind.None;
        }

        OfferKind = signedIn ? GuidedSetupOfferKind.Quick : GuidedSetupOfferKind.Full;
        OfferTitle = signedIn ? GuidedSetupCopy.QuickTitle : GuidedSetupCopy.FullTitle;
        OfferBody = signedIn ? GuidedSetupCopy.QuickBody : GuidedSetupCopy.FullBody;
        Phase = GuidedSetupPhase.Offer;
        ShowOffer = true;
        ShowTour = false;
        return OfferKind;
    }

    public GuidedSetupStart ChooseYes(bool splashVisible)
    {
        if (Phase != GuidedSetupPhase.Offer)
            return new GuidedSetupStart(false, false);
        return ApplyStart(splashVisible);
    }

    public GuidedSetupStart StartFromHelp(bool signedIn, bool splashVisible)
    {
        OfferKind = signedIn ? GuidedSetupOfferKind.Quick : GuidedSetupOfferKind.Full;
        OfferTitle = signedIn ? GuidedSetupCopy.QuickTitle : GuidedSetupCopy.FullTitle;
        OfferBody = signedIn ? GuidedSetupCopy.QuickBody : GuidedSetupCopy.FullBody;
        ShowOffer = false;
        return ApplyStart(splashVisible);
    }

    public void ChooseNotThisTime()
    {
        if (Phase != GuidedSetupPhase.Offer)
            return;
        _choice = GuidedSetupOfferChoice.NotThisTime;
        ShowOffer = false;
        OfferKind = GuidedSetupOfferKind.None;
        Phase = GuidedSetupPhase.Idle;
        Persist();
    }

    public void ChooseDontAskAgain()
    {
        if (Phase != GuidedSetupPhase.Offer)
            return;
        _choice = GuidedSetupOfferChoice.DontAskAgain;
        ShowOffer = false;
        OfferKind = GuidedSetupOfferKind.None;
        Phase = GuidedSetupPhase.Idle;
        Persist();
    }

    public void ExitTour()
    {
        if (Phase is GuidedSetupPhase.Idle or GuidedSetupPhase.Offer or GuidedSetupPhase.Finished)
            return;
        CompleteTour();
    }

    public void ChooseNext()
    {
        if (Phase != GuidedSetupPhase.Tabs)
            return;
        if (_tabIndex >= _tabs.Count - 1)
        {
            CompleteTour();
            return;
        }

        _tabIndex++;
        ApplyTab();
    }

    public void NotifyAuthMode(ServiceNowAuthMode mode)
    {
        var previous = _seenMode;
        _seenMode = mode;
        if (Phase != GuidedSetupPhase.Connection)
            return;
        if (mode == ServiceNowAuthMode.BrowserSession && previous != ServiceNowAuthMode.BrowserSession)
            EnterSignIn();
    }

    public void ConfirmSignInMethod()
    {
        if (Phase != GuidedSetupPhase.Connection)
            return;
        if (_seenMode == ServiceNowAuthMode.BrowserSession || _currentAuthMode() == ServiceNowAuthMode.BrowserSession)
        {
            _seenMode = ServiceNowAuthMode.BrowserSession;
            EnterSignIn();
        }
    }

    public void SignInFailed()
    {
        if (Phase is not (GuidedSetupPhase.SignIn or GuidedSetupPhase.Splash))
            return;
        if (Phase != GuidedSetupPhase.SignIn)
            EnterSignIn();
        FailureText = GuidedSetupCopy.SignInFailed;
    }

    public void SignInSucceeded(bool splashVisible)
    {
        if (Phase != GuidedSetupPhase.SignIn)
            return;
        FailureText = "";
        if (splashVisible)
            EnterSplash();
        else
            BeginTabTour();
    }

    public void SplashAppeared()
    {
        if (Phase is GuidedSetupPhase.SignIn)
            EnterSplash();
    }

    /// <summary>
    /// User close and the splash closing itself both leave the splash step.
    /// Closing while this tour is not waiting does nothing and does not throw.
    /// </summary>
    public void SplashClosed(bool closedItself)
    {
        _ = closedItself;
        if (Phase is GuidedSetupPhase.Splash or GuidedSetupPhase.WaitingToStartTabs)
            BeginTabTour();
    }

    [RelayCommand]
    private void Yes() => ChooseYes(_splashVisible());

    [RelayCommand]
    private void NotThisTime() => ChooseNotThisTime();

    [RelayCommand]
    private void DontAskAgain() => ChooseDontAskAgain();

    [RelayCommand]
    private void Exit() => ExitTour();

    [RelayCommand]
    private void Next() => ChooseNext();

    private GuidedSetupStart ApplyStart(bool splashVisible)
    {
        GuidedSetupStart start;
        if (OfferKind == GuidedSetupOfferKind.Full)
        {
            EnterConnection();
            start = new GuidedSetupStart(CloseSplash: splashVisible, OpenConnection: true);
        }
        else if (splashVisible)
        {
            ShowOffer = false;
            ShowTour = false;
            ShowNext = false;
            PulseSignInMethod = false;
            PulseSignInButton = false;
            Phase = GuidedSetupPhase.WaitingToStartTabs;
            start = new GuidedSetupStart(false, false);
        }
        else
        {
            BeginTabTour();
            start = new GuidedSetupStart(false, false);
        }

        StartApplied?.Invoke(this, start);
        return start;
    }

    private void EnterConnection()
    {
        _seenMode = _currentAuthMode();
        Phase = GuidedSetupPhase.Connection;
        ShowOffer = false;
        ShowTour = true;
        ShowNext = false;
        Spotlight = GuidedSetupSpotlight.SignInMethod;
        DimStrength = GuidedSetupMotion.ConnectionDim;
        BannerText = GuidedSetupCopy.SessionBanner;
        InstructionText = GuidedSetupCopy.SelectMethod(_browserSignInLabel());
        FailureText = "";
        TabLine = "";
        PulseSignInMethod = true;
        PulseSignInButton = false;
    }

    private void EnterSignIn()
    {
        if (Phase == GuidedSetupPhase.SignIn)
            return;
        Phase = GuidedSetupPhase.SignIn;
        ShowOffer = false;
        ShowTour = true;
        ShowNext = false;
        Spotlight = GuidedSetupSpotlight.SignInButton;
        DimStrength = GuidedSetupMotion.ConnectionDim;
        BannerText = "";
        InstructionText = GuidedSetupCopy.FollowPrompts;
        FailureText = "";
        TabLine = "";
        PulseSignInMethod = false;
        PulseSignInButton = true;
    }

    private void EnterSplash()
    {
        Phase = GuidedSetupPhase.Splash;
        ShowOffer = false;
        ShowTour = true;
        ShowNext = false;
        Spotlight = GuidedSetupSpotlight.None;
        DimStrength = 0;
        BannerText = "";
        InstructionText = GuidedSetupCopy.SplashCaption;
        FailureText = "";
        TabLine = "";
        PulseSignInMethod = false;
        PulseSignInButton = false;
    }

    private void BeginTabTour()
    {
        _tabs = GuidedSetupScript.ForTour(_visibleTabs(), _leadsLocked());
        _tabIndex = 0;
        if (_tabs.Count == 0)
        {
            CompleteTour();
            return;
        }

        Phase = GuidedSetupPhase.Tabs;
        ShowOffer = false;
        ShowTour = true;
        Spotlight = GuidedSetupSpotlight.Tab;
        DimStrength = GuidedSetupMotion.TabDim;
        BannerText = "";
        InstructionText = "";
        FailureText = "";
        PulseSignInMethod = false;
        PulseSignInButton = false;
        ApplyTab();
    }

    private void ApplyTab()
    {
        var stop = _tabs[_tabIndex];
        HighlightedTab = stop.Section;
        TabLine = stop.Line;
        ShowNext = true;
    }

    private void CompleteTour()
    {
        _finished = true;
        Phase = GuidedSetupPhase.Finished;
        ShowOffer = false;
        ShowTour = false;
        ShowNext = false;
        Spotlight = GuidedSetupSpotlight.None;
        DimStrength = 0;
        BannerText = "";
        InstructionText = "";
        FailureText = "";
        TabLine = "";
        PulseSignInMethod = false;
        PulseSignInButton = false;
        Persist();
    }

    private void Persist() => _persist(_choice, _finished);
}
