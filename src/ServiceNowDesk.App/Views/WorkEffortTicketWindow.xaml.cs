using System.Windows;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Views;

public partial class WorkEffortTicketWindow : Window
{
    private RecordWorkspaceViewModel? _workspace;

    public WorkEffortTicketWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            _workspace?.Detach();
            _workspace = null;
        };
    }

    public static WorkEffortTicketWindow Open(WorkEffortCredit credit, IServiceNowClient client, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(credit);
        ArgumentNullException.ThrowIfNull(client);
        var window = new WorkEffortTicketWindow();
        if (owner is not null)
            window.Owner = owner;
        window.Load(credit, client);
        return window;
    }

    private void Load(WorkEffortCredit credit, IServiceNowClient client)
    {
        var desktop = new WindowsDesktopServices();
        var title = string.IsNullOrWhiteSpace(credit.DisplayNumber) ? "Ticket" : credit.DisplayNumber.Trim();
        if (!string.IsNullOrWhiteSpace(credit.Title))
            title += " — " + credit.Title.Trim();
        Title = title;

        FrameworkElement view;
        RecordWorkspaceViewModel workspace;
        switch (credit.Section)
        {
            case DeskSection.RequestedItems:
                workspace = new RequestedItemWorkspaceViewModel(desktop);
                view = new RequestedItemView { EditorOnly = true, DataContext = workspace };
                break;
            case DeskSection.WalkUps:
                workspace = new InteractionWorkspaceViewModel(desktop);
                view = new InteractionView { EditorOnly = true, DataContext = workspace };
                break;
            case DeskSection.Requests:
                workspace = new RequestWorkspaceViewModel(desktop);
                view = new RequestView { EditorOnly = true, DataContext = workspace };
                break;
            default:
                workspace = new IncidentWorkspaceViewModel(desktop);
                view = new IncidentView { EditorOnly = true, DataContext = workspace };
                break;
        }

        _workspace = workspace;
        Host.Content = view;
        workspace.Attach(client);
        _ = workspace.OpenFromSearchAsync(credit.RecordSysId);
    }
}
