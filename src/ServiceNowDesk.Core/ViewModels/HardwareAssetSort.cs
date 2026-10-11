using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

public enum HardwareSortColumn
{
    Serial,
    Model,
    Assigned,
    Location,
    State,
    Substate,
    Comments
}

public static class HardwareAssetSort
{
    public static int Compare(HardwareAsset left, HardwareAsset right, HardwareSortColumn column, bool descending)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var leftText = Text(left, column);
        var rightText = Text(right, column);
        var leftEmpty = string.IsNullOrWhiteSpace(leftText);
        var rightEmpty = string.IsNullOrWhiteSpace(rightText);
        if (leftEmpty || rightEmpty)
        {
            if (leftEmpty && rightEmpty)
                return 0;
            return leftEmpty ? 1 : -1;
        }

        var compared = string.Compare(leftText, rightText, StringComparison.OrdinalIgnoreCase);
        return descending ? -compared : compared;
    }

    public static string Text(HardwareAsset asset, HardwareSortColumn column) => column switch
    {
        HardwareSortColumn.Serial => asset.SerialNumber,
        HardwareSortColumn.Model => asset.Model,
        HardwareSortColumn.Assigned => asset.AssignedTo.Display,
        HardwareSortColumn.Location => asset.Location.Display,
        HardwareSortColumn.State => asset.InstallStatusLabel,
        HardwareSortColumn.Substate => asset.SubstatusLabel,
        HardwareSortColumn.Comments => asset.Comments,
        _ => ""
    };
}
