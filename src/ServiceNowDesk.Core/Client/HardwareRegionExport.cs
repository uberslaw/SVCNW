using System.Text;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Client;

/// <summary>
/// CSV of In use computers in a region: model, configuration item, and assigned to.
/// Callers that write a file should use <see cref="Utf8Bom"/> so Excel reads names correctly.
/// </summary>
public static class HardwareRegionExport
{
    public const string FileName = "region-laptops.csv";
    public const string DialogTitle = "Download region laptops";
    public static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static bool IsInUseComputer(HardwareAsset asset, IEnumerable<Choice>? statuses = null)
    {
        if (!HardwareCatalog.IsComputer(asset))
            return false;

        if (HardwareCatalog.SameLabel(asset.InstallStatusLabel, HardwareCatalog.InUse)
            || HardwareCatalog.SameLabel(asset.InstallStatus, HardwareCatalog.InUse))
            return true;

        if (statuses is null)
            return false;

        return HardwareCatalog.SameLabel(
            HardwareCatalog.LabelOf(statuses, asset.InstallStatus),
            HardwareCatalog.InUse);
    }

    public static bool InRegion(HardwareAsset asset, IReadOnlyList<string> offices)
    {
        if (offices.Count == 0)
            return false;

        var place = asset.Location.Display;
        return offices.Any(office => RegionOfficeMatch.LocationMatchesOffice(place, office));
    }

    public static string Format(IEnumerable<HardwareAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var rows = assets
            .Select(asset => (
                Model: asset.Model ?? "",
                ConfigurationItem: asset.ConfigurationItem.Display ?? "",
                AssignedTo: asset.AssignedTo.Display ?? ""))
            .OrderBy(row => row.AssignedTo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ConfigurationItem, StringComparer.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        builder.Append("Model,Configuration item,Assigned to");
        builder.Append('\n');
        foreach (var row in rows)
        {
            builder.Append(Escape(row.Model));
            builder.Append(',');
            builder.Append(Escape(row.ConfigurationItem));
            builder.Append(',');
            builder.Append(Escape(row.AssignedTo));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny(['"', ',', '\r', '\n']) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
