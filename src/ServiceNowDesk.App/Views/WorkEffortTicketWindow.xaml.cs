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
        return Open(credit.Section, credit.RecordSysId, credit.DisplayNumber, credit.Title, client, owner);
    }

    public static WorkEffortTicketWindow Open(
        DeskSection section,
        string sysId,
        string number,
        string title,
        IServiceNowClient client,
        Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var window = new WorkEffortTicketWindow();
        if (owner is not null)
            window.Owner = owner;
        window.Load(section, sysId, number, title, client);
        return window;
    }

    private void Load(DeskSection section, string sysId, string number, string title, IServiceNowClient client)
    {
        var desktop = new WindowsDesktopServices();
        var windowTitle = string.IsNullOrWhiteSpace(number) ? "Ticket" : number.Trim();
        if (!string.IsNullOrWhiteSpace(title))
            windowTitle += " — " + title.Trim();
        Title = windowTitle;

        if (section == DeskSection.Knowledge)
        {
            var knowledge = new KnowledgeWorkspaceViewModel(desktop);
            Host.Content = new KnowledgeView { DataContext = knowledge };
            knowledge.Attach(client);
            _ = knowledge.OpenAsync(client, sysId);
            return;
        }

        FrameworkElement view;
        RecordWorkspaceViewModel workspace;
        switch (section)
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
        _ = workspace.OpenFromSearchAsync(sysId);
    }
}
