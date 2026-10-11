using System.Windows;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Views;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Services;

public sealed class WindowsTicketPopOut : ITicketPopOut
{
    public void Show(WorkEffortCredit credit, IServiceNowClient client)
    {
        ArgumentNullException.ThrowIfNull(credit);
        ArgumentNullException.ThrowIfNull(client);
        ShowTicket(credit.Section, credit.RecordSysId, credit.DisplayNumber, credit.Title, client);
    }

    public void ShowTicket(DeskSection section, string sysId, string number, string title, IServiceNowClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Window? owner = null;
        if (Application.Current?.MainWindow is { IsVisible: true } main)
            owner = main;
        var window = WorkEffortTicketWindow.Open(section, sysId, number, title, client, owner);
        window.Show();
    }
}
