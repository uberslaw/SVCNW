using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.ViewModels;

public partial class WorkEffortViewModel : ObservableObject
{
    public WorkEffortViewModel()
    {
    }

    public ObservableCollection<WorkEffortRow> Rows { get; } = [];

    public event EventHandler? ScaleChanged;

    [ObservableProperty] private WorkEffortScale scale = WorkEffortScale.Today;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string emptyMessage = "";
    [ObservableProperty] private bool hasRows;

    public void MarkLoading()
    {
        Rows.Clear();
        HasRows = false;
        EmptyMessage = "";
        Status = "Loading work effort…";
    }

    public void Show(WorkEffortReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Rows.Clear();
        foreach (var row in report.Rows)
            Rows.Add(row);
        HasRows = Rows.Count > 0;
        EmptyMessage = HasRows ? "" : report.EmptyMessage;
        Status = report.Status;
    }

    public void ShowError(string message)
    {
        Rows.Clear();
        HasRows = false;
        EmptyMessage = "";
        Status = message ?? "";
    }

    public void Clear()
    {
        Rows.Clear();
        HasRows = false;
        EmptyMessage = "";
        Status = "";
    }

    partial void OnScaleChanged(WorkEffortScale value)
    {
        _ = value;
        ScaleChanged?.Invoke(this, EventArgs.Empty);
    }
}
