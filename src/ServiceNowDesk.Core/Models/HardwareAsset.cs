namespace ServiceNowDesk.Models;

public sealed record HardwareAsset
{
    public string SysId { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Model { get; init; } = "";
    public string ModelCategory { get; init; } = "";
    public ReferenceValue AssignedTo { get; init; }
    public ReferenceValue Location { get; init; }
    public ReferenceValue Stockroom { get; init; }
    public string InstallStatus { get; init; } = "";
    public string InstallStatusLabel { get; init; } = "";
    public string Substatus { get; init; } = "";
    public string SubstatusLabel { get; init; } = "";
    public string Comments { get; init; } = "";
}

public sealed class HardwareChanges
{
    public string? AssignedToId { get; init; }
    public bool ClearAssignedTo { get; init; }
    public string? InstallStatus { get; init; }
    public string? Substatus { get; init; }
    public bool ClearSubstatus { get; init; }
    public string? StockroomId { get; init; }
    public bool ClearStockroom { get; init; }
    public string? LocationId { get; init; }
    public bool ClearLocation { get; init; }
    public string? Comments { get; init; }

    public bool HasChanges =>
        AssignedToId is not null
        || ClearAssignedTo
        || InstallStatus is not null
        || Substatus is not null
        || ClearSubstatus
        || StockroomId is not null
        || ClearStockroom
        || LocationId is not null
        || ClearLocation
        || Comments is not null;
}
