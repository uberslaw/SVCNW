using System.Windows;
using ServiceNowDesk.Client;
using ServiceNowDesk.Views;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Services;

public sealed class WindowsTicketPopOut : ITicketPopOut
{
    public void Show(WorkEffortCredit credit, IServiceNowClient client)
    {
        ArgumentNullException.ThrowIfNull(credit);
        ArgumentNullException.ThrowIfNull(client);
        Window? owner = null;
        if (Application.Current?.MainWindow is { IsVisible: true } main)
            owner = main;
        var window = WorkEffortTicketWindow.Open(credit, client, owner);
        window.Show();
    }
}
