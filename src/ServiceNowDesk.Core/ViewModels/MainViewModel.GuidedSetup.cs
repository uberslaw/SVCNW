using System.ComponentModel;
using ServiceNowDesk.GuidedSetup;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.ViewModels;

public partial class MainViewModel
{
    public void StartGuidedSetupFromHelp() =>
        Guided.StartFromHelp(IsConnected, Startup.ShowScreen);

    private static string BrowserSignInOptionLabel(ConnectionViewModel connection)
    {
        var choice = connection.AuthChoices
            .FirstOrDefault(item => item.Mode == Models.ServiceNowAuthMode.BrowserSession);
        return choice?.Label ?? "Browser sign-in (SSO)";
    }

    private void PersistGuidedSetup(GuidedSetupOfferChoice choice, bool finished)
    {
        Connection.RememberGuidedSetup(choice, finished);
        try
        {
            _store.Save(Connection.BuildSettings());
        }
        catch
        {
            // The choice stays on the connection model for this session.
        }
    }

    private void OnGuidedStart(object? sender, GuidedSetupStart start)
    {
        if (start.CloseSplash)
        {
            try
            {
                // Hide the splash for the connection step only. Dismiss must not cancel
                // a download that is still running — the compact bar keeps reporting it.
                if (Startup.ShowScreen)
                    Startup.Dismiss();
            }
            catch
            {
                // No splash, or it already closed: keep going.
            }
        }

        if (!start.OpenConnection)
            return;

        try
        {
            SelectedSection = Models.DeskSection.Connection;
        }
        catch
        {
            // No account connection yet: the tour still starts.
        }
    }

    private void OnStartupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StartupDownloadModel.ShowScreen))
            return;

        try
        {
            if (Startup.ShowScreen)
                Guided.SplashAppeared();
            else
                Guided.SplashClosed(closedItself: !Startup.ClosedByUser);
        }
        catch
        {
            // Closing the splash while signed out must not stop the app.
        }
    }
}
